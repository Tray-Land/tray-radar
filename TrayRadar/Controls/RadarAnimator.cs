using Microsoft.UI.Dispatching;
using TrayRadar.Services;
using WinUIEx.Maps;

namespace TrayRadar.Controls;

/// <summary>
/// Drives the radar overlay on a <see cref="MapControl"/>: one <see cref="TileLayer"/> per scan
/// time, with play/pause looping and periodic refresh of the frame list.
/// </summary>
/// <remarks>
/// Every frame layer stays visible at a near-zero opacity so its tiles load in the background
/// (WinUIEx.Maps skips tile acquisition for layers at opacity 0); only the current frame shows at
/// the user's opacity. The map exposes no tile-loaded signal, so playback holds on the current
/// frame until the next one has been prefetching for <see cref="WarmUp"/>, rather than flashing
/// to an empty map.
/// </remarks>
internal sealed class RadarAnimator
{
    // Low enough to round to nothing in the 8-bit render target, so hidden frames add no ghosting.
    private const double PrefetchOpacity = 0.001;
    private static readonly TimeSpan StepInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan LastFrameHold = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan BufferingRetry = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan WarmUp = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(2);
    private static readonly TileLayerBounds ConusBounds = new(-130, 20, -60, 55);

    private readonly MapControl _map;
    private readonly DispatcherQueueTimer _stepTimer;
    private readonly DispatcherQueueTimer _refreshTimer;
    private readonly List<(DateTime? Time, TileLayer Layer, DateTime AddedUtc)> _frames = [];
    private int _current = -1;
    private double _opacity = SettingsService.RadarOpacity;
    private bool _refreshing;

    public RadarAnimator(MapControl map)
    {
        _map = map;

        // Credit NOAA from one constant, empty layer: moving the credit between frame layers makes
        // the map rebuild its attribution text on every step, which visibly blinks.
        _map.Layers.Add(new MapElementsLayer
        {
            Attribution = RadarService.Attribution,
            AttributionLink = RadarService.AttributionLink,
        });
        DispatcherQueue queue = DispatcherQueue.GetForCurrentThread();

        _stepTimer = queue.CreateTimer();
        _stepTimer.IsRepeating = false;
        _stepTimer.Tick += (_, _) => Step();

        _refreshTimer = queue.CreateTimer();
        _refreshTimer.Interval = RefreshInterval;
        _refreshTimer.IsRepeating = true;
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
    }

    /// <summary>Raised with the scan time (UTC; null if unknown) of the frame being displayed.</summary>
    public event EventHandler<DateTime?>? FrameChanged;

    public bool IsPlaying { get; private set; }

    public int FrameCount => _frames.Count;

    /// <summary>Index of the displayed frame (oldest first), or -1 when there are no frames.</summary>
    public int CurrentIndex => _current;

    /// <summary>True while playback is waiting for the next frame's tiles to load.</summary>
    public bool IsBuffering { get; private set; }

    public double Opacity
    {
        get => _opacity;
        set
        {
            _opacity = Math.Clamp(value, 0.1, 1.0);
            ApplyVisibility();
        }
    }

    /// <summary>Starts periodic refresh (call when the flyout opens) and loads the latest frames.</summary>
    public async Task ActivateAsync()
    {
        _refreshTimer.Start();
        await RefreshAsync();
        if (IsPlaying)
        {
            ScheduleStep(StepInterval);
        }
    }

    /// <summary>Stops all timers (call when the flyout hides).</summary>
    public void Deactivate()
    {
        _refreshTimer.Stop();
        _stepTimer.Stop();
    }

    public void Play()
    {
        if (IsPlaying || _frames.Count < 2)
        {
            return;
        }

        // Continue from the frame on screen; the first step wraps to the oldest frame.
        IsPlaying = true;
        ApplyVisibility();
        ScheduleStep(StepInterval);
    }

    public void Pause()
    {
        IsPlaying = false;
        IsBuffering = false;
        _stepTimer.Stop();
        _current = _frames.Count - 1;
        ApplyVisibility();
    }

    /// <summary>Stops playback and shows the frame at <paramref name="index"/> (scrubbing).</summary>
    public void ShowFrame(int index)
    {
        if (_frames.Count == 0)
        {
            return;
        }

        IsPlaying = false;
        IsBuffering = false;
        _stepTimer.Stop();
        _current = Math.Clamp(index, 0, _frames.Count - 1);
        ApplyVisibility();
    }

    /// <summary>Re-reads the available scan times and reconciles the frame layers.</summary>
    public async Task RefreshAsync()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            IReadOnlyList<DateTime> times;
            try
            {
                times = await RadarService.GetFrameTimesAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Radar capabilities failed: {ex.Message}");
                times = [];
            }

            if (times.Count == 0)
            {
                // Couldn't read the time dimension: fall back to the service's default (latest) scan.
                if (_frames.Count == 0)
                {
                    ReplaceFrames([null]);
                }

                return;
            }

            ReplaceFrames(times.Select(t => (DateTime?)t).ToList());
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ReplaceFrames(IReadOnlyList<DateTime?> times)
    {
        HashSet<DateTime?> wanted = [.. times];

        // Drop frames that aged out; keep existing layers (and their loaded tiles) that remain.
        for (int i = _frames.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(_frames[i].Time))
            {
                _map.Layers.Remove(_frames[i].Layer);
                _frames.RemoveAt(i);
            }
        }

        foreach (DateTime? time in times)
        {
            if (_frames.Any(f => f.Time == time))
            {
                continue;
            }

            TileLayer layer = new(new TileLayerOptions
            {
                TileUrl = RadarService.BuildTileUrl(time),
                TileSize = 256,
                MaxSourceZoom = 12,
                Bounds = ConusBounds,
            })
            {
                FadeDuration = TimeSpan.Zero,
                Opacity = PrefetchOpacity,
            };

            // Radar frames sit at the bottom of the public layers so pins stay on top.
            _map.Layers.Insert(0, layer);
            _frames.Add((time, layer, DateTime.UtcNow));
        }

        _frames.Sort((a, b) => Nullable.Compare(a.Time, b.Time));
        if (!IsPlaying || _current >= _frames.Count || _current < 0)
        {
            _current = _frames.Count - 1;
        }

        if (_frames.Count < 2 && IsPlaying)
        {
            Pause();
            return;
        }

        ApplyVisibility();
    }

    private void Step()
    {
        if (!IsPlaying || _frames.Count == 0)
        {
            return;
        }

        int next = (_current + 1) % _frames.Count;
        bool buffering = DateTime.UtcNow - _frames[next].AddedUtc < WarmUp;
        if (buffering)
        {
            if (!IsBuffering)
            {
                IsBuffering = true;
                ApplyVisibility();
            }

            ScheduleStep(BufferingRetry);
            return;
        }

        IsBuffering = false;
        _current = next;
        ApplyVisibility();
        ScheduleStep(_current == _frames.Count - 1 ? LastFrameHold : StepInterval);
    }

    private void ScheduleStep(TimeSpan interval)
    {
        _stepTimer.Interval = interval;
        _stepTimer.Start();
    }

    private void ApplyVisibility()
    {
        // Each opacity change republishes the map's render plan, and the map renders on its own
        // thread. Raise the new frame before lowering the old one so no rendered plan lacks radar.
        if (_current >= 0 && _current < _frames.Count)
        {
            _frames[_current].Layer.Opacity = _opacity;
        }

        for (int i = 0; i < _frames.Count; i++)
        {
            if (i != _current)
            {
                _frames[i].Layer.Opacity = PrefetchOpacity;
            }
        }

        FrameChanged?.Invoke(this, _current >= 0 && _current < _frames.Count ? _frames[_current].Time : null);
    }
}
