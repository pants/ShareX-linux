#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using ShareX.Linux.Interop;
using ShareX.Linux.Portals;

namespace ShareX.Linux.Recording;

/// <summary>What the user wants to record.</summary>
public enum RecordingTarget
{
    /// <summary>Let the user pick a monitor in the compositor's dialog.</summary>
    Monitor,

    /// <summary>Let the user pick a single window in the compositor's dialog.</summary>
    Window
}

/// <summary>
/// A live screen-capture feed for the recorder.
/// </summary>
/// <remarks>
/// On Wayland a continuous capture always comes from the ScreenCast portal: the compositor asks
/// the user what may be shared and then publishes it as a PipeWire node. Nothing in ffmpeg's
/// stock build reads PipeWire, so GStreamer's pipewiresrc - which ships with PipeWire itself -
/// pulls the frames and writes them out as yuv4mpegpipe. That container states its own size and
/// frame rate, so ffmpeg can consume the pipe without being told the geometry up front.
/// </remarks>
public sealed class WaylandRecordingSource : IAsyncDisposable
{
    private const string GStreamerCommand = "gst-launch-1.0";

    private readonly InhibitPortal inhibit = new();
    private ScreenCastSession? session;
    private Process? pipeline;
    private bool disposed;

    public static Action<Exception, string>? ExceptionLogger { get; set; }

    /// <summary>Where the user's earlier choice is remembered, so repeat recordings skip the picker.</summary>
    public static string? RestoreToken { get; set; }

    /// <summary>The geometry of the stream, which is what the recorded video's crop is relative to.</summary>
    public Rectangle Bounds { get; private set; }

    /// <summary>The yuv4mpegpipe byte stream to feed to ffmpeg's standard input.</summary>
    public Stream? Output => pipeline?.StandardOutput.BaseStream;

    public static bool IsAvailable => LinuxEnvironment.HasExecutable(GStreamerCommand);

    /// <summary>Explains what to install when the pipeline cannot be built.</summary>
    public static string UnavailableReason =>
        $"Screen recording needs the GStreamer PipeWire plugin. Install \"{GStreamerCommand}\" " +
        "(package gstreamer) and the \"pipewire\" GStreamer plugin, then try again.";

    /// <summary>
    /// Negotiates a capture with the compositor and starts the pipeline. The user is shown the
    /// portal's own picker unless a restore token from a previous recording still applies.
    /// </summary>
    public async Task StartAsync(RecordingTarget target, int framesPerSecond, bool captureCursor,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!IsAvailable) throw new PortalException(UnavailableReason);

        session = await new ScreenCastPortal().StartAsync(
            sourceTypes: target == RecordingTarget.Window ? ScreenCastSourceTypes.Window : ScreenCastSourceTypes.Monitor,
            cursorMode: captureCursor ? ScreenCastCursorMode.Embedded : ScreenCastCursorMode.Hidden,
            multiple: false,
            restoreToken: RestoreToken,
            persistMode: ScreenCastPersistMode.Application,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        RestoreToken = session.RestoreToken ?? RestoreToken;

        ScreenCastStream stream = session.Streams[0];
        Bounds = new Rectangle(stream.X, stream.Y, stream.Width, stream.Height);

        int fileDescriptor = await session.GetPipeWireFileDescriptorAsync(cancellationToken).ConfigureAwait(false);

        // The descriptor must survive exec for GStreamer to see it; .NET opens everything
        // close-on-exec. Passing it is what keeps the capture inside the portal's permission.
        bool inheritable = LibC.MakeInheritable(fileDescriptor);

        pipeline = StartPipeline(stream.NodeId, inheritable ? fileDescriptor : -1, framesPerSecond);

        await inhibit.InhibitAsync("Screen recording in progress", InhibitFlags.Idle,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static Process StartPipeline(uint nodeId, int fileDescriptor, int framesPerSecond)
    {
        string executable = LinuxEnvironment.FindExecutable(GStreamerCommand)!;

        ProcessStartInfo startInfo = new(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        startInfo.ArgumentList.Add("-q");
        startInfo.ArgumentList.Add("pipewiresrc");
        startInfo.ArgumentList.Add($"path={nodeId.ToString(CultureInfo.InvariantCulture)}");

        if (fileDescriptor >= 0)
        {
            startInfo.ArgumentList.Add($"fd={fileDescriptor.ToString(CultureInfo.InvariantCulture)}");
        }

        // Timestamps come from the pipeline clock so dropped frames do not shift the video.
        startInfo.ArgumentList.Add("do-timestamp=true");
        startInfo.ArgumentList.Add("keepalive-time=1000");
        startInfo.ArgumentList.Add("!");
        startInfo.ArgumentList.Add("videoconvert");
        startInfo.ArgumentList.Add("!");
        startInfo.ArgumentList.Add("videorate");
        startInfo.ArgumentList.Add("!");

        // y4m carries only planar YUV, and I420 is what every encoder downstream expects anyway.
        startInfo.ArgumentList.Add($"video/x-raw,format=I420,framerate={framesPerSecond}/1");
        startInfo.ArgumentList.Add("!");
        startInfo.ArgumentList.Add("y4menc");
        startInfo.ArgumentList.Add("!");
        startInfo.ArgumentList.Add("fdsink");
        startInfo.ArgumentList.Add("fd=1");

        Process process = Process.Start(startInfo)
            ?? throw new PortalException("Starting the GStreamer capture pipeline failed.");

        // Drain diagnostics so a chatty pipeline cannot fill its error pipe and stall.
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data)) Debug.WriteLine($"gst-launch: {e.Data}");
        };

        process.BeginErrorReadLine();

        return process;
    }

    /// <summary>Asks the pipeline to finish, flushing whatever it has already produced.</summary>
    public void Stop()
    {
        Process? process = pipeline;

        if (process == null || process.HasExited) return;

        try
        {
            // SIGINT makes gst-launch send an end-of-stream through the pipeline, so the y4m
            // output is closed cleanly instead of being truncated mid-frame.
            if (LibC.SendSignal(process.Id, LibC.SIGINT) != 0)
            {
                process.Kill(entireProcessTree: true);
            }

            if (!process.WaitForExit(5000)) process.Kill(entireProcessTree: true);
        }
        catch (Exception exception)
        {
            ExceptionLogger?.Invoke(exception, "Stopping the screen capture pipeline failed.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;

        Stop();

        pipeline?.Dispose();
        pipeline = null;

        await inhibit.DisposeAsync().ConfigureAwait(false);

        if (session != null)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            session = null;
        }
    }
}
