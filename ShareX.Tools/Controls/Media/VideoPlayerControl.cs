#nullable enable

using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ShareX.Linux;

namespace ShareX.Tools;

public sealed record VideoPlaybackInfo(double Duration, int Width, int Height);

/// <summary>
/// A video surface backed by ffmpeg.
/// </summary>
/// <remarks>
/// ffmpeg decodes into raw BGRA on a worker thread and this control blits the frames, which
/// keeps playback inside Avalonia's own rendering instead of embedding a native video window -
/// something Wayland does not allow a client to do across process boundaries anyway.
/// Playback is video only, matching the Windows player ShareX used before, so the volume and
/// mute properties exist for the view models and do nothing.
/// </remarks>
public sealed class VideoPlayerControl : Control, IDisposable
{
    /// <summary>Frames are scaled into this box so a 4K clip does not swamp the UI thread.</summary>
    private const int MaximumPlaybackWidth = 1280;
    private const int MaximumPlaybackHeight = 720;

    /// <summary>Enough buffering to ride out decoder jitter without adding visible latency.</summary>
    private const int FrameQueueCapacity = 4;

    private readonly DispatcherTimer _frameTimer;

    private string? _path;
    private string _ffmpegPath = "ffmpeg";
    private WriteableBitmap? _surface;
    private Process? _decoder;
    private Thread? _readerThread;
    private BlockingCollection<byte[]>? _frames;
    private CancellationTokenSource? _streamCancellation;

    private double _duration;
    private double _position;
    private double _frameRate = 30;
    private double _aspectRatio;
    private double _streamStartPosition;
    private long _framesPresented;
    private int _frameWidth;
    private int _frameHeight;
    private bool _playing;
    private bool _disposed;

    public event Action<double>? PositionChanged;
    public event Action<bool>? IsPlayingChanged;
    public event Action<Exception>? PlaybackFailed;
    public event Action? VideoClicked;

    public VideoPlayerControl()
    {
        Focusable = false;
        ClipToBounds = true;

        _frameTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000.0 / 30) };
        _frameTimer.Tick += (_, _) => PresentNextFrame();
    }

    /// <summary>True while playback is running or resuming after a seek.</summary>
    public bool IsPlaybackRequested => _playing;

    /// <summary>Accepted for interface parity with the previous player; playback carries no audio.</summary>
    public double Volume { get; set; } = 1;

    public bool IsMuted { get; set; }

    /// <summary>
    /// Where to find ffmpeg. Set by the hosting tool from the user's FFmpeg settings before
    /// loading a file; falls back to whatever is on PATH.
    /// </summary>
    public string FFmpegPath
    {
        get => _ffmpegPath;
        set => _ffmpegPath = string.IsNullOrEmpty(value) ? "ffmpeg" : value;
    }

    public async Task<VideoPlaybackInfo> LoadAsync(string path, CancellationToken token = default)
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);

        Unload();

        string ffmpeg = ResolveFFmpeg();
        string ffprobe = FFmpegVideoDecoder.ResolveFFprobePath(ffmpeg);

        VideoProbeResult probe = await Task.Run(() => FFmpegVideoDecoder.Probe(ffprobe, path), token)
            .ConfigureAwait(true);

        _path = path;
        _duration = probe.Duration;
        _frameRate = probe.FrameRate;
        _aspectRatio = probe.Height > 0 ? probe.Width / (double)probe.Height : 0;

        (_frameWidth, _frameHeight) = FFmpegVideoDecoder.FitInside(probe.Width, probe.Height,
            MaximumPlaybackWidth, MaximumPlaybackHeight);

        _frameTimer.Interval = TimeSpan.FromSeconds(1 / Math.Clamp(_frameRate, 1, 240));

        SetPosition(0);
        await ShowStillFrameAsync(0, token).ConfigureAwait(true);

        InvalidateMeasure();

        return new VideoPlaybackInfo(_duration, probe.Width, probe.Height);
    }

    public void Unload()
    {
        Dispatcher.UIThread.VerifyAccess();

        StopStream();

        _path = null;
        _duration = 0;
        _aspectRatio = 0;
        _surface?.Dispose();
        _surface = null;

        SetPlaying(false);
        SetPosition(0);
        InvalidateVisual();
    }

    public void Play()
    {
        Dispatcher.UIThread.VerifyAccess();

        if (_path == null || _playing) return;

        // Restarting at the end would hang on an empty stream.
        if (_duration > 0 && _position >= _duration - (1 / Math.Max(_frameRate, 1))) return;

        StartStream(_position);
        SetPlaying(true);
    }

    public void Pause()
    {
        Dispatcher.UIThread.VerifyAccess();

        if (!_playing) return;

        StopStream();
        SetPlaying(false);
    }

    public void Seek(double seconds)
    {
        Dispatcher.UIThread.VerifyAccess();

        if (_path == null) return;

        double target = Math.Clamp(seconds, 0, _duration > 0 ? _duration : seconds);
        bool wasPlaying = _playing;

        StopStream();
        SetPosition(target);

        if (wasPlaying)
        {
            StartStream(target);
        }
        else
        {
            _ = ShowStillFrameAsync(target, CancellationToken.None);
        }
    }

    public void StepFrame(bool forward)
    {
        Dispatcher.UIThread.VerifyAccess();

        if (_path == null) return;

        Pause();

        double step = 1 / Math.Clamp(_frameRate, 1, 240);
        Seek(_position + (forward ? step : -step));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_aspectRatio <= 0) return base.MeasureOverride(availableSize);

        double width = Math.Min(availableSize.Width, availableSize.Height * _aspectRatio);

        return double.IsFinite(width) && width > 0
            ? new Size(width, width / _aspectRatio)
            : base.MeasureOverride(availableSize);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) VideoClicked?.Invoke();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (_surface == null) return;

        Rect destination = new(Bounds.Size);
        context.DrawImage(_surface, new Rect(_surface.Size), destination);
    }

    private void StartStream(double from)
    {
        if (_path == null) return;

        try
        {
            _streamCancellation = new CancellationTokenSource();
            _frames = new BlockingCollection<byte[]>(FrameQueueCapacity);
            _decoder = FFmpegVideoDecoder.StartFrameStream(ResolveFFmpeg(), _path, from,
                _frameWidth, _frameHeight, _frameRate);

            _streamStartPosition = from;
            _framesPresented = 0;

            Process decoder = _decoder;
            BlockingCollection<byte[]> frames = _frames;
            CancellationToken token = _streamCancellation.Token;

            _readerThread = new Thread(() => ReadFrames(decoder, frames, token))
            {
                IsBackground = true,
                Name = "ShareX video decoder"
            };

            _readerThread.Start();
            _frameTimer.Start();
        }
        catch (Exception exception)
        {
            StopStream();
            SetPlaying(false);
            PlaybackFailed?.Invoke(exception);
        }
    }

    private void ReadFrames(Process decoder, BlockingCollection<byte[]> frames, CancellationToken token)
    {
        int frameBytes = _frameWidth * _frameHeight * 4;

        try
        {
            Stream output = decoder.StandardOutput.BaseStream;

            while (!token.IsCancellationRequested)
            {
                byte[] frame = new byte[frameBytes];
                int read = output.ReadAtLeast(frame, frameBytes, throwOnEndOfStream: false);

                if (read < frameBytes) break;

                frames.Add(frame, token);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or IOException
            or InvalidOperationException)
        {
            // Expected when playback is stopped while a frame is in flight.
        }
        catch (Exception exception)
        {
            Dispatcher.UIThread.Post(() => PlaybackFailed?.Invoke(exception));
        }
        finally
        {
            try { frames.CompleteAdding(); } catch { /* already completed */ }
        }
    }

    private void PresentNextFrame()
    {
        BlockingCollection<byte[]>? frames = _frames;

        if (frames == null) return;

        if (!frames.TryTake(out byte[]? frame))
        {
            if (!frames.IsAddingCompleted) return;

            // The decoder ran out: playback has reached the end of the file.
            StopStream();
            SetPlaying(false);
            if (_duration > 0) SetPosition(_duration);
            return;
        }

        ShowFrame(frame, _frameWidth, _frameHeight);

        _framesPresented++;
        SetPosition(_streamStartPosition + (_framesPresented / Math.Max(_frameRate, 1)));
    }

    private async Task ShowStillFrameAsync(double position, CancellationToken token)
    {
        if (_path == null) return;

        string path = _path;
        string ffmpeg = ResolveFFmpeg();
        int width = _frameWidth, height = _frameHeight;

        try
        {
            WriteableBitmap frame = await Task.Run(
                () => FFmpegVideoDecoder.DecodeFrame(ffmpeg, path, position, width, height, token), token)
                .ConfigureAwait(true);

            if (_disposed || _path != path)
            {
                frame.Dispose();
                return;
            }

            _surface?.Dispose();
            _surface = frame;
            InvalidateVisual();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer seek.
        }
        catch (Exception exception)
        {
            PlaybackFailed?.Invoke(exception);
        }
    }

    private unsafe void ShowFrame(byte[] bgra, int width, int height)
    {
        if (_surface == null || _surface.PixelSize.Width != width || _surface.PixelSize.Height != height)
        {
            _surface?.Dispose();
            _surface = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque);
        }

        using (Avalonia.Platform.ILockedFramebuffer framebuffer = _surface.Lock())
        {
            int rowBytes = width * 4;
            byte* destination = (byte*)framebuffer.Address;

            for (int y = 0; y < height; y++)
            {
                bgra.AsSpan(y * rowBytes, rowBytes)
                    .CopyTo(new Span<byte>(destination + (y * framebuffer.RowBytes), rowBytes));
            }
        }

        InvalidateVisual();
    }

    private void StopStream()
    {
        _frameTimer.Stop();

        try { _streamCancellation?.Cancel(); } catch { /* already disposed */ }

        Process? decoder = _decoder;
        _decoder = null;

        if (decoder != null)
        {
            try
            {
                if (!decoder.HasExited) decoder.Kill(entireProcessTree: true);
            }
            catch
            {
                // The decoder already exited on its own.
            }

            decoder.Dispose();
        }

        // The reader can be parked on a full queue; draining lets it observe cancellation.
        BlockingCollection<byte[]>? frames = _frames;
        _frames = null;

        if (frames != null)
        {
            while (frames.TryTake(out _)) { }

            _readerThread?.Join(TimeSpan.FromSeconds(1));
            frames.Dispose();
        }

        _readerThread = null;
        _streamCancellation?.Dispose();
        _streamCancellation = null;
    }

    private string ResolveFFmpeg() =>
        File.Exists(_ffmpegPath) ? _ffmpegPath : LinuxEnvironment.FindExecutable("ffmpeg") ?? _ffmpegPath;

    private void SetPosition(double value)
    {
        _position = _duration > 0 ? Math.Clamp(value, 0, _duration) : Math.Max(0, value);
        PositionChanged?.Invoke(_position);
    }

    private void SetPlaying(bool value)
    {
        if (_playing == value) return;

        _playing = value;
        IsPlayingChanged?.Invoke(value);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopStream();
        _surface?.Dispose();
        _surface = null;
    }
}
