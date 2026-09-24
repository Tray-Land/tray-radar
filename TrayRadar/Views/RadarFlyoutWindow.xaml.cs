using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TrayRadar.Controls;
using TrayRadar.Services;
using Windows.Devices.Geolocation;
using Windows.Foundation;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;
using WinRT.Interop;
using WinUIEx;
using WinUIEx.Maps;
using MapElementsLayer = WinUIEx.Maps.MapElementsLayer;
using MapIcon = WinUIEx.Maps.MapIcon;

namespace TrayRadar.Views;

/// <summary>
/// Borderless radar popup shown next to the tray icon. It is created once and then shown/hidden
/// so the map and its tile cache stay warm, and it light-dismisses when focus leaves.
/// </summary>
public sealed partial class RadarFlyoutWindow : WindowEx
{
    private const int PopupWidth = 420;
    private const int PopupHeight = 520;
    private static readonly TimeSpan RecentDismissWindow = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan DeviceLocationRefresh = TimeSpan.FromMinutes(15);

    private readonly HWND _hwnd;
    private readonly DispatcherQueueTimer _hideTimer;
    private readonly DispatcherQueueTimer _savedTickTimer;
    private readonly RadarAnimator _radar;
    private readonly MapElementsLayer _pinLayer = new();
    private MapIcon? _homePin;
    private bool _isPopupVisible;
    private bool _isShowing;
    private bool _allowClose;
    private bool _syncingFrameSlider;
    private DateTime _lastDismissedAtUtc;
    private DateTime _lastDeviceLocationUtc;

    public RadarFlyoutWindow()
    {
        InitializeComponent();
        _hwnd = (HWND)WindowNative.GetWindowHandle(this);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        ConfigurePresenter();

        Activated += OnWindowActivated;
        AppWindow.Closing += OnAppWindowClosing;

        _hideTimer = DispatcherQueue.CreateTimer();
        _hideTimer.Interval = TimeSpan.FromMilliseconds(150);
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) => HideIfFocusLeftWindow();

        _savedTickTimer = DispatcherQueue.CreateTimer();
        _savedTickTimer.Interval = TimeSpan.FromSeconds(1.5);
        _savedTickTimer.IsRepeating = false;
        _savedTickTimer.Tick += (_, _) => SaveViewIcon.Glyph = "";

        string? key = Secrets.AzureMapsKey;
        if (key is null)
        {
            System.Diagnostics.Debug.WriteLine("TrayRadar: no Azure Maps key was provided at build time; using a blank basemap.");
        }
        else
        {
            Map.MapServiceToken = key;
        }

        _radar = new RadarAnimator(Map);
        _radar.FrameChanged += (_, time) => UpdateFrameText(time);
        Map.Layers.Add(_pinLayer);

        Root.ActualThemeChanged += (_, _) => ApplyMapStyle();
        SettingsService.MapSettingsChanged += OnMapSettingsChanged;
        ApplyMapStyle();
        UpdateHomePin();
    }

    /// <summary>Shows the popup, or hides it if already visible (tray-icon click behavior).</summary>
    public void Toggle()
    {
        if (_isPopupVisible)
        {
            HidePopup();
            return;
        }

        // The click that light-dismissed the popup also arrives here; don't immediately reopen.
        if (DateTime.UtcNow - _lastDismissedAtUtc < RecentDismissWindow)
        {
            return;
        }

        ShowPopup();
    }

    public async void ShowPopup()
    {
        WindowPlacementService.PositionNearTray(this, PopupWidth, PopupHeight);
        _hideTimer.Stop();
        _isShowing = true;
        _isPopupVisible = true;

        // Every open starts at the user's default view.
        _ = GoToDefaultViewAsync(MapAnimationKind.None);

        AppWindow.Show(true);
        Activate();
        PInvoke.SetForegroundWindow(_hwnd);
        Map.Focus(FocusState.Programmatic);

        await _radar.ActivateAsync();
        RefreshDeviceLocationIfStale();
    }

    public void HidePopup()
    {
        if (!_isPopupVisible)
        {
            return;
        }

        _isPopupVisible = false;
        _lastDismissedAtUtc = DateTime.UtcNow;
        _hideTimer.Stop();
        _radar.Deactivate();
        AppWindow.Hide();
    }

    /// <summary>Closes the window for real (app exit).</summary>
    public void CloseForExit()
    {
        _allowClose = true;
        SettingsService.MapSettingsChanged -= OnMapSettingsChanged;
        _radar.Deactivate();
        Close();
    }

    private void ConfigurePresenter()
    {
        OverlappedPresenter presenter = OverlappedPresenter.Create();
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsResizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(true, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;

        unsafe
        {
            DWM_WINDOW_CORNER_PREFERENCE corner = DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND;
            _ = PInvoke.DwmSetWindowAttribute(_hwnd, DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE, &corner, sizeof(DWM_WINDOW_CORNER_PREFERENCE));
        }
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_allowClose)
        {
            args.Cancel = true;
            HidePopup();
        }
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            _isShowing = false;
            _hideTimer.Stop();
            return;
        }

        if (_isShowing || !_isPopupVisible)
        {
            return;
        }

        // Debounce: focus can bounce briefly while XAML popups (tooltips, menus) open.
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void HideIfFocusLeftWindow()
    {
        if (!ShouldRemainVisible(PInvoke.GetForegroundWindow()))
        {
            HidePopup();
        }
    }

    private bool ShouldRemainVisible(HWND foreground)
    {
        if (foreground == _hwnd || PInvoke.GetAncestor(foreground, GET_ANCESTOR_FLAGS.GA_ROOT) == _hwnd)
        {
            return true;
        }

        // Keep open while one of our own transient XAML popups or menus has focus.
        Span<char> className = stackalloc char[256];
        int length = PInvoke.GetClassName(foreground, className);
        if (length <= 0)
        {
            return false;
        }

        ReadOnlySpan<char> name = className[..length];
        return name.SequenceEqual("Xaml_WindowedPopupClass") || name.SequenceEqual("#32768");
    }

    private async Task GoToDefaultViewAsync(MapAnimationKind animation)
    {
        (double Lat, double Lon)? center = SettingsService.DefaultCenter ?? SettingsService.HomeLocation;
        if (center is not { } c)
        {
            return;
        }

        Geopoint point = new(new BasicGeoposition { Latitude = c.Lat, Longitude = c.Lon });
        await Map.TrySetViewAsync(point, SettingsService.DefaultZoom, 0, 0, animation);
    }

    private void SaveCurrentViewAsDefault()
    {
        if (Map.Center is not { } center)
        {
            return;
        }

        SettingsService.DefaultCenter = (center.Position.Latitude, center.Position.Longitude);
        SettingsService.DefaultZoom = Map.ZoomLevel;
        SaveViewIcon.Glyph = ""; // checkmark
        _savedTickTimer.Stop();
        _savedTickTimer.Start();
    }

    private void OnMapSettingsChanged(object? sender, EventArgs e)
    {
        ApplyMapStyle();
        _radar.Opacity = SettingsService.RadarOpacity;
        UpdateHomePin();
        if (_isPopupVisible)
        {
            _ = GoToDefaultViewAsync(MapAnimationKind.Default);
        }
    }

    private void ApplyMapStyle()
    {
        if (Secrets.AzureMapsKey is null)
        {
            Map.MapStyle = MapStyle.Blank;
            return;
        }

        bool dark = Root.ActualTheme == ElementTheme.Dark;
        Map.MapStyle = SettingsService.BasemapStyle switch
        {
            BasemapStyle.Road => MapStyle.Road,
            BasemapStyle.Night => MapStyle.Night,
            BasemapStyle.Grayscale => dark ? MapStyle.GrayscaleDark : MapStyle.GrayscaleLight,
            BasemapStyle.Satellite => MapStyle.SatelliteWithRoads,
            _ => dark ? MapStyle.Night : MapStyle.Road,
        };
    }

    private void UpdateHomePin()
    {
        if (!SettingsService.ShowLocationPin || SettingsService.HomeLocation is not { } home)
        {
            if (_homePin is not null)
            {
                _pinLayer.MapElements.Remove(_homePin);
                _homePin = null;
            }

            return;
        }

        Geopoint point = new(new BasicGeoposition { Latitude = home.Lat, Longitude = home.Lon });
        if (_homePin is null)
        {
            FontIcon pinVisual = new()
            {
                Glyph = "", // Location
                FontSize = 24,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.DeepSkyBlue),
            };
            _homePin = new MapIcon(pinVisual, point) { NormalizedAnchorPoint = new Point(0.5, 0.5) };
            _pinLayer.MapElements.Add(_homePin);
        }
        else
        {
            _homePin.Location = point;
        }
    }

    private async void RefreshDeviceLocationIfStale()
    {
        if (SettingsService.LocationMode != LocationMode.Device
            || DateTime.UtcNow - _lastDeviceLocationUtc < DeviceLocationRefresh)
        {
            return;
        }

        _lastDeviceLocationUtc = DateTime.UtcNow;
        (double Lat, double Lon)? point = await LocationService.TryGetDeviceLocationAsync(TimeSpan.FromSeconds(10));
        if (point is { } p && LocationService.IsInConus(p.Lat, p.Lon))
        {
            // Only the pin moves; the saved default view is the user's choice.
            SettingsService.HomeLocation = p;
            UpdateHomePin();
        }
    }

    private void UpdateFrameText(DateTime? timeUtc)
    {
        PlayIcon.Glyph = _radar.IsPlaying ? "" : "";
        ToolTipService.SetToolTip(PlayButton, _radar.IsPlaying ? "Pause loop" : "Play loop");
        AutomationProperties.SetName(PlayButton, _radar.IsPlaying ? "Pause radar loop" : "Play radar loop");
        UpdateFrameSlider();

        if (timeUtc is not DateTime t)
        {
            FrameTimeText.Text = "Radar · latest";
            LiveDot.Opacity = 1;
            return;
        }

        TimeSpan age = DateTime.UtcNow - t;
        FrameTimeText.Text = _radar.IsBuffering ? $"Radar · {t.ToLocalTime():t} · loading…" : $"Radar · {t.ToLocalTime():t}";
        // Dim the dot for older frames so the loop position is visible at a glance.
        LiveDot.Opacity = age < TimeSpan.FromMinutes(10) ? 1 : 0.35;
    }

    private void UpdateFrameSlider()
    {
        int count = _radar.FrameCount;
        bool show = count > 1 && (_radar.IsPlaying || _radar.CurrentIndex != count - 1);
        FrameScrubber.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
        {
            return;
        }

        _syncingFrameSlider = true;
        FrameSlider.Maximum = count - 1;
        FrameSlider.Value = _radar.CurrentIndex;
        _syncingFrameSlider = false;
    }

    private void FrameSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_syncingFrameSlider)
        {
            _radar.ShowFrame((int)Math.Round(e.NewValue));
        }
    }

    private void HideScrubberButton_Click(object sender, RoutedEventArgs e)
    {
        // Pausing returns to the newest frame, which hides the scrubber.
        _radar.Pause();
        Map.Focus(FocusState.Programmatic);
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_radar.IsPlaying)
        {
            _radar.Pause();
        }
        else
        {
            _radar.Play();
        }
    }

    private async void RecenterButton_Click(object sender, RoutedEventArgs e) =>
        await GoToDefaultViewAsync(MapAnimationKind.Default);

    private void SaveViewButton_Click(object sender, RoutedEventArgs e) => SaveCurrentViewAsDefault();

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => App.Current.ShowSettings();

    private void Escape_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        HidePopup();
    }
}
