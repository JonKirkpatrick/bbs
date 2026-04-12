using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Bbs.Client.App.Views;

public partial class SplashWindow : Window
{
    private const int DefaultVerticalCenterOffsetPx = 32;
    private const int FrameCount = 81;
    private const double FramesPerSecond = 30.0;
    private const int FrameIndexStart = 1;
    private const int FinalFrameHoldMs = 120;
    private const string FramePathPattern = "avares://Bbs.Client.App/Assets/ui/splash/{0:0000}.png";

    private readonly List<Bitmap> _frames = new();
    private CancellationTokenSource? _playbackCts;
    private bool _playbackCompleted;
    private bool _positionAdjusted;

    public event EventHandler? PlaybackCompleted;

    public SplashWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        ApplyLaunchOffset();
        _playbackCts = new CancellationTokenSource();

        try
        {
            LoadFrames();
            await PlayAsync(_playbackCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Window was closed during playback.
        }
        finally
        {
            CompletePlayback();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _playbackCts?.Cancel();
        _playbackCts?.Dispose();
        _playbackCts = null;

        foreach (var frame in _frames)
        {
            frame.Dispose();
        }

        _frames.Clear();
        CompletePlayback();
    }

    private void LoadFrames()
    {
        if (_frames.Count > 0)
        {
            return;
        }

        for (var i = 0; i < FrameCount; i++)
        {
            var frameNumber = FrameIndexStart + i;
            var uri = new Uri(string.Format(FramePathPattern, frameNumber));
            try
            {
                using var stream = AssetLoader.Open(uri);
                _frames.Add(new Bitmap(stream));
            }
            catch (FileNotFoundException)
            {
                break;
            }
            catch (Exception)
            {
                break;
            }
        }
    }

    private async Task PlayAsync(CancellationToken cancellationToken)
    {
        if (_frames.Count == 0)
        {
            return;
        }

        var splashImage = this.FindControl<Image>("SplashImage");
        if (splashImage is null)
        {
            return;
        }

        splashImage.Source = _frames[0];
        var stopwatch = Stopwatch.StartNew();

        var totalSeconds = _frames.Count / FramesPerSecond;
        var lastFrameIndex = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
            var nextFrameIndex = Math.Min((int)(elapsedSeconds * FramesPerSecond), _frames.Count - 1);

            if (nextFrameIndex != lastFrameIndex)
            {
                splashImage.Source = _frames[nextFrameIndex];
                lastFrameIndex = nextFrameIndex;
            }

            if (elapsedSeconds >= totalSeconds)
            {
                break;
            }

            await Task.Delay(8, cancellationToken);
        }

        splashImage.Source = _frames[_frames.Count - 1];
        await Task.Delay(FinalFrameHoldMs, cancellationToken);
    }

    private void CompletePlayback()
    {
        if (_playbackCompleted)
        {
            return;
        }

        _playbackCompleted = true;
        PlaybackCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyLaunchOffset()
    {
        if (_positionAdjusted)
        {
            return;
        }

        _positionAdjusted = true;

        var offset = DefaultVerticalCenterOffsetPx;
        var raw = Environment.GetEnvironmentVariable("BBS_SPLASH_Y_OFFSET_PX");
        if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out var parsed))
        {
            offset = parsed;
        }

        if (offset == 0)
        {
            return;
        }

        Position = new PixelPoint(Position.X, Position.Y + offset);
    }
}
