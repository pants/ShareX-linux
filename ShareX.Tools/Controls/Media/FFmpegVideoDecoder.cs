#nullable enable

using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ShareX.Linux;

namespace ShareX.Tools;

/// <summary>Size and length of a video file, as reported by ffprobe.</summary>
public sealed record VideoProbeResult(double Duration, int Width, int Height, double FrameRate);

/// <summary>
/// Decodes video with the ffmpeg command-line tools.
/// </summary>
/// <remarks>
/// Linux has no system media framework comparable to Media Foundation, and ShareX already
/// depends on ffmpeg for recording and conversion, so the same binary decodes frames here.
/// Frames come back as raw BGRA, which is exactly what Avalonia's WriteableBitmap wants.
/// </remarks>
internal static class FFmpegVideoDecoder
{
    /// <summary>Reads duration, dimensions and frame rate without decoding any pictures.</summary>
    public static VideoProbeResult Probe(string ffprobePath, string path)
    {
        string output = RunToString(ffprobePath,
        [
            "-v", "error",
            "-select_streams", "v:0",
            "-show_entries", "stream=width,height,r_frame_rate:format=duration",
            "-of", "default=noprint_wrappers=1",
            path
        ]);

        int width = 0, height = 0;
        double duration = 0, frameRate = 0;

        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = line.IndexOf('=');
            if (separator <= 0) continue;

            string key = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim();

            switch (key)
            {
                case "width": int.TryParse(value, CultureInfo.InvariantCulture, out width); break;
                case "height": int.TryParse(value, CultureInfo.InvariantCulture, out height); break;
                case "duration": double.TryParse(value, CultureInfo.InvariantCulture, out duration); break;
                case "r_frame_rate": frameRate = ParseRational(value); break;
            }
        }

        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException($"ffprobe found no video stream in \"{path}\".");
        }

        return new VideoProbeResult(Math.Max(0, duration), width, height, frameRate > 0 ? frameRate : 30);
    }

    /// <summary>Decodes the frame at <paramref name="position"/> seconds, scaled to fit the given box.</summary>
    public static WriteableBitmap DecodeFrame(string ffmpegPath, string path, double position,
        int maxWidth, int maxHeight, CancellationToken token = default)
    {
        (int width, int height) = default((int, int));

        ProcessStartInfo startInfo = CreateStartInfo(ffmpegPath);

        // Seeking before -i uses the container index, which is fast and accurate enough here.
        startInfo.ArgumentList.Add("-ss");
        startInfo.ArgumentList.Add(position.ToString("0.###", CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(path);
        startInfo.ArgumentList.Add("-frames:v");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("-vf");
        startInfo.ArgumentList.Add($"scale='min({maxWidth},iw)':'min({maxHeight},ih)':force_original_aspect_ratio=decrease");
        startInfo.ArgumentList.Add("-pix_fmt");
        startInfo.ArgumentList.Add("bgra");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("rawvideo");
        startInfo.ArgumentList.Add("-");

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Starting ffmpeg failed.");

        using MemoryStream buffer = new();

        try
        {
            process.StandardOutput.BaseStream.CopyTo(buffer);
            string diagnostics = process.StandardError.ReadToEnd();
            process.WaitForExit(30000);

            byte[] pixels = buffer.ToArray();

            if (pixels.Length == 0)
            {
                throw new InvalidDataException($"ffmpeg produced no frame at {position:0.##}s: {Tail(diagnostics)}");
            }

            // rawvideo carries no header, so the frame size is recovered from the filter graph's output.
            (width, height) = ReadScaledSize(diagnostics);

            if (width <= 0 || height <= 0 || pixels.Length < width * height * 4)
            {
                throw new InvalidDataException($"ffmpeg returned an unexpected frame: {Tail(diagnostics)}");
            }

            token.ThrowIfCancellationRequested();

            return CreateBitmap(pixels, width, height);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            }
        }
    }

    /// <summary>
    /// Starts ffmpeg streaming raw BGRA frames of a fixed size to its standard output, for
    /// continuous playback. The caller owns the process and must dispose it.
    /// </summary>
    public static Process StartFrameStream(string ffmpegPath, string path, double position,
        int width, int height, double frameRate)
    {
        ProcessStartInfo startInfo = CreateStartInfo(ffmpegPath);

        if (position > 0)
        {
            startInfo.ArgumentList.Add("-ss");
            startInfo.ArgumentList.Add(position.ToString("0.###", CultureInfo.InvariantCulture));
        }

        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(path);
        startInfo.ArgumentList.Add("-an");
        startInfo.ArgumentList.Add("-vf");
        startInfo.ArgumentList.Add($"scale={width}:{height}");
        startInfo.ArgumentList.Add("-r");
        startInfo.ArgumentList.Add(frameRate.ToString("0.###", CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-pix_fmt");
        startInfo.ArgumentList.Add("bgra");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("rawvideo");
        startInfo.ArgumentList.Add("-");

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Starting ffmpeg failed.");
    }

    public static unsafe WriteableBitmap CreateBitmap(ReadOnlySpan<byte> bgra, int width, int height)
    {
        WriteableBitmap bitmap = new(new PixelSize(width, height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Opaque);

        try
        {
            using ILockedFramebuffer framebuffer = bitmap.Lock();

            int rowBytes = width * 4;
            byte* destination = (byte*)framebuffer.Address;

            for (int y = 0; y < height; y++)
            {
                bgra.Slice(y * rowBytes, rowBytes)
                    .CopyTo(new Span<byte>(destination + (y * framebuffer.RowBytes), rowBytes));
            }

            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    /// <summary>Scales a frame size down to fit a box, keeping both dimensions even for the encoders.</summary>
    public static (int Width, int Height) FitInside(int width, int height, int maxWidth, int maxHeight)
    {
        double scale = Math.Min(Math.Min(maxWidth / (double)width, maxHeight / (double)height), 1);

        return (Math.Max(2, (int)(width * scale) & ~1), Math.Max(2, (int)(height * scale) & ~1));
    }

    public static string ResolveFFprobePath(string ffmpegPath)
    {
        string directory = Path.GetDirectoryName(ffmpegPath) ?? "";
        string sibling = Path.Combine(directory, "ffprobe");

        if (File.Exists(sibling)) return sibling;

        return LinuxEnvironment.FindExecutable("ffprobe") ?? sibling;
    }

    private static ProcessStartInfo CreateStartInfo(string ffmpegPath) => new(ffmpegPath)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };

    private static string RunToString(string executable, IEnumerable<string> arguments)
    {
        ProcessStartInfo startInfo = CreateStartInfo(executable);

        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Starting \"{executable}\" failed.");

        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();

        if (!process.WaitForExit(30000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw new TimeoutException($"\"{executable}\" did not finish in time.");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"\"{executable}\" failed: {Tail(error)}");
        }

        return output;
    }

    /// <summary>
    /// Pulls the output frame size out of ffmpeg's own report of the stream it wrote, which is
    /// the only place rawvideo dimensions are stated.
    /// </summary>
    private static (int, int) ReadScaledSize(string diagnostics)
    {
        int marker = diagnostics.LastIndexOf("rawvideo", StringComparison.Ordinal);

        if (marker < 0) return (0, 0);

        foreach (string token in diagnostics[marker..].Split([',', ' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            int x = token.IndexOf('x');

            if (x <= 0 || x == token.Length - 1) continue;

            if (int.TryParse(token[..x], CultureInfo.InvariantCulture, out int width) &&
                int.TryParse(token[(x + 1)..], CultureInfo.InvariantCulture, out int height) &&
                width > 0 && height > 0)
            {
                return (width, height);
            }
        }

        return (0, 0);
    }

    private static double ParseRational(string value)
    {
        int separator = value.IndexOf('/');

        if (separator < 0)
        {
            return double.TryParse(value, CultureInfo.InvariantCulture, out double plain) ? plain : 0;
        }

        return double.TryParse(value[..separator], CultureInfo.InvariantCulture, out double numerator) &&
               double.TryParse(value[(separator + 1)..], CultureInfo.InvariantCulture, out double denominator) &&
               denominator > 0
            ? numerator / denominator
            : 0;
    }

    /// <summary>ffmpeg's diagnostics are verbose; the last few lines carry the actual error.</summary>
    private static string Tail(string text)
    {
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return lines.Length == 0 ? "no output" : string.Join(" | ", lines[^Math.Min(3, lines.Length)..]);
    }
}
