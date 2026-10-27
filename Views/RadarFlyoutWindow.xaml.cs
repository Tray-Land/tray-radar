using System.Drawing.Imaging;
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
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Win32;
using Windows.Storage.Streams;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;
using WinRT.Interop;
using WinUIEx;
using WinUIEx.Maps;
using MapControl = WinUIEx.Maps.MapControl;
using MapElementsLayer = WinUIEx.Maps.MapElementsLayer;
using MapIcon = WinUIEx.Maps.MapIcon;

namespace TrayRadar.Views;

/// <summary>
/// Borderless radar popup shown next to the tray icon. Dismissing only hides it, so reopening soon
/// after is instant; after <see cref="CloseDelay"/> hidden it closes for real, releasing the map and
/// its tiles, and the next open builds a new window. It light-dismisses when focus leaves.
/// </summary>
public sealed partial class RadarFlyoutWindow : WindowEx
{
    private const int PopupWidth = 420;
    private const int PopupHeight = 520;
    private static readonly TimeSpan RecentDismissWindow = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan DeviceLocationRefresh = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan CloseDelay = TimeSpan.FromMinutes(1);

    // The first open keeps the map covered until its first tiles are drawn (see WaitForFirstMapFrameAsync).
    private static readonly TimeSpan MapRevealTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MapRevealFallbackDelay = TimeSpan.FromMilliseconds(2500);

    // Static so rebuilding the window doesn't re-query the device location on every open.
    private static DateTime s_lastDeviceLocationUtc;

    private readonly HWND _hwnd;
    private readonly DispatcherQueueTimer _hideTimer;
    private readonly DispatcherQueueTimer _closeTimer;
    private readonly DispatcherQueueTimer _savedTickTimer;
    private readonly DispatcherQueueTimer _copiedTickTimer;
    private readonly RadarAnimator _radar;
    private readonly MapElementsLayer _pinLayer = new();
    private MapIcon? _homePin;
    private SettingsPage? _settingsPage;
    private bool _isPopupVisible;
    private bool _isShowing;
    private bool _allowClose;
    private bool _isClosed;
    private bool _syncingFrameSlider;
    private bool _scrubberOpen;
    private bool _mapRevealStarted;
    private DateTime _lastDismissedAtUtc;

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

        _closeTimer = DispatcherQueue.CreateTimer();
        _closeTimer.Interval = CloseDelay;
        _closeTimer.IsRepeating = false;
        _closeTimer.Tick += (_, _) => CloseIfStillHidden();

        _savedTickTimer = DispatcherQueue.CreateTimer();
        _savedTickTimer.Interval = TimeSpan.FromSeconds(1.5);
        _savedTickTimer.IsRepeating = false;
        _savedTickTimer.Tick += (_, _) => ResetSaveViewIcon();

        _copiedTickTimer = DispatcherQueue.CreateTimer();
        _copiedTickTimer.Interval = TimeSpan.FromSeconds(1.5);
        _copiedTickTimer.IsRepeating = false;
        _copiedTickTimer.Tick += (_, _) => SetCopyConfirmed(false);

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
        _radar.StateChanged += (_, _) => UpdateStatus();
        ConnectivityService.NetworkStatusChanged += OnNetworkStatusChanged;
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

        ShowMainPage();
        ShowPopup();
    }

    public bool IsPopupVisible => _isPopupVisible;

    public bool IsShowingSettings => _settingsPage is { Visibility: Visibility.Visible };

    /// <summary>Covers the map with the settings page; the radar stops refreshing while it is up.</summary>
    public void ShowSettingsPage()
    {
        if (_settingsPage is null)
        {
            _settingsPage = new SettingsPage(ShowMainPage);
            Root.Children.Add(_settingsPage);
        }

        _settingsPage.Visibility = Visibility.Visible;
        _settingsPage.OnShown();
        _radar.Deactivate();
    }

    public void ShowMainPage()
    {
        if (!IsShowingSettings)
        {
            return;
        }

        _settingsPage!.Visibility = Visibility.Collapsed;
        if (_isPopupVisible)
        {
            Map.Focus(FocusState.Programmatic);
            _ = _radar.ActivateAsync();
        }
    }

    public async void ShowPopup()
    {
        WindowPlacementService.PositionNearTray(this, PopupWidth, PopupHeight);
        _hideTimer.Stop();
        _closeTimer.Stop();
        _isShowing = true;
        _isPopupVisible = true;

        // Every open starts at the user's default view.
        _ = GoToDefaultViewAsync(MapAnimationKind.None);

        AppWindow.Show(true);
        Activate();
        PInvoke.SetForegroundWindow(_hwnd);
        RevealMapOnFirstShow();

        if (IsShowingSettings)
        {
            _settingsPage!.OnShown();
            return;
        }

        Map.Focus(FocusState.Programmatic);
        await _radar.ActivateAsync();
        if (!_isClosed)
        {
            RefreshDeviceLocationIfStale();
        }
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
        _savedTickTimer.Stop();
        ResetSaveViewIcon();
        _copiedTickTimer.Stop();
        SetCopyConfirmed(false);
        _radar.Deactivate();
        AppWindow.Hide();

        _closeTimer.Stop();
        _closeTimer.Start();
    }

    /// <summary>
    /// Closes the window for real: stops its timers, abandons async work in flight, and
    /// unsubscribes from app-lifetime events so nothing keeps the window or its map alive.
    /// </summary>
    public void CloseWindow()
    {
        if (_isClosed)
        {
            return;
        }

        HidePopup();
        _isClosed = true;
        _closeTimer.Stop();
        _radar.Clear();

        if (_settingsPage is not null)
        {
            Root.Children.Remove(_settingsPage);
            _settingsPage = null;
        }

        SettingsService.MapSettingsChanged -= OnMapSettingsChanged;
        ConnectivityService.NetworkStatusChanged -= OnNetworkStatusChanged;
        Activated -= OnWindowActivated;
        AppWindow.Closing -= OnAppWindowClosing;

        _allowClose = true;
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

    private void CloseIfStillHidden()
    {
        if (!_isPopupVisible)
        {
            CloseWindow();
        }
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

    private void ResetSaveViewIcon() => SaveViewIcon.Glyph = "";

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
            MapCover.RequestedTheme = ElementTheme.Light;
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

        // Match the cover to the basemap's overall tone so the reveal doesn't flash either way.
        bool darkMap = Map.MapStyle is MapStyle.Night or MapStyle.GrayscaleDark or MapStyle.SatelliteWithRoads;
        MapCover.RequestedTheme = darkMap ? ElementTheme.Dark : ElementTheme.Light;

        // A style change makes the map clear to light gray again, so cover it until tiles redraw.
        if (_mapRevealStarted)
        {
            _mapRevealStarted = false;
            MapCoverFadeOut.Stop();
            MapCover.Opacity = 1;
            MapCover.Visibility = Visibility.Visible;
            RevealMapOnFirstShow();
        }
    }

    private async void RevealMapOnFirstShow()
    {
        if (_mapRevealStarted)
        {
            return;
        }

        _mapRevealStarted = true;
        await WaitForFirstMapFrameAsync();
        if (!_isClosed)
        {
            MapCoverFadeOut.Begin();
        }
    }

    // WinUIEx.Maps clears to light gray (#F0F0F0) until tiles arrive, so wait for the first frame.
    private Task WaitForFirstMapFrameAsync() =>
        RadarAnimator.WaitForTilesAsync(Map, MapRevealTimeout, MapRevealFallbackDelay);

    private void MapCoverFadeOut_Completed(object sender, object e) => MapCover.Visibility = Visibility.Collapsed;

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
            || DateTime.UtcNow - s_lastDeviceLocationUtc < DeviceLocationRefresh)
        {
            return;
        }

        s_lastDeviceLocationUtc = DateTime.UtcNow;
        (double Lat, double Lon)? point = await LocationService.TryGetDeviceLocationAsync(TimeSpan.FromSeconds(10));
        if (point is { } p && LocationService.IsInConus(p.Lat, p.Lon))
        {
            // Only the pin moves; the saved default view is the user's choice.
            SettingsService.HomeLocation = p;
            if (!_isClosed)
            {
                UpdateHomePin();
            }
        }
    }

    private void OnNetworkStatusChanged(object? sender, EventArgs e)
    {
        // Raised on a background thread; recheck right away rather than waiting for the next refresh.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isPopupVisible)
            {
                _ = _radar.RefreshAsync();
            }
        });
    }

    private void UpdateStatus()
    {
        LoadingRing.IsActive = _radar.IsLoading;
        if (_radar.LastRefreshFailed)
        {
            bool online = ConnectivityService.IsInternetAvailable;
            ConnectionBar.Title = online ? "Can't reach the radar service" : "No internet connection";
            ConnectionBar.Message = online
                ? "NOAA radar data isn't responding. Retrying shortly."
                : "Can't connect to the internet. Radar will update when you're back online.";
        }

        ConnectionBar.IsOpen = _radar.LastRefreshFailed;
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
        bool show = count > 1 && _scrubberOpen;
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
        // Only this button closes the scrubber; it also returns to the newest frame.
        _scrubberOpen = false;
        _radar.Stop();
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
            _scrubberOpen = true;
            _radar.Play();
        }
    }

    private async void RecenterButton_Click(object sender, RoutedEventArgs e) =>
        await GoToDefaultViewAsync(MapAnimationKind.Default);

    private void SaveViewButton_Click(object sender, RoutedEventArgs e) => SaveCurrentViewAsDefault();

    private async void CopyImageButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await CopyMapImageToClipboardAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Copy as image failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Copies what the user sees (map + radar, without the toolbar) to the clipboard as a PNG.
    /// The map is GPU-rendered and invisible to RenderTargetBitmap, so the window is read from the screen.
    /// </summary>
    private async Task CopyMapImageToClipboardAsync()
    {
        // Hide the toolbar overlay and let the compositor present a frame without it.
        Overlay.Opacity = 0;
        try
        {
            await Task.Delay(120);

            System.Drawing.Point origin = default;
            PInvoke.ClientToScreen(_hwnd, ref origin);
            Windows.Graphics.SizeInt32 size = AppWindow.ClientSize;

            using System.Drawing.Bitmap bitmap = new(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(bitmap))
            {
                g.CopyFromScreen(origin.X, origin.Y, 0, 0, new System.Drawing.Size(size.Width, size.Height));
            }

            InMemoryRandomAccessStream stream = new();
            using (MemoryStream png = new())
            {
                bitmap.Save(png, ImageFormat.Png);
                Stream output = stream.AsStreamForWrite();
                await output.WriteAsync(png.ToArray());
                await output.FlushAsync();
            }

            DataPackage package = new();
            package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
            Clipboard.SetContent(package);
            Clipboard.Flush();
            SetCopyConfirmed(true);
            _copiedTickTimer.Stop();
            _copiedTickTimer.Start();
        }
        finally
        {
            Overlay.Opacity = 1;
        }
    }

    private void SetCopyConfirmed(bool confirmed)
    {
        CopyIcon.Opacity = confirmed ? 0 : 1;
        CopiedIcon.Opacity = confirmed ? 1 : 0;
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => App.Current.ShowSettings();

    private void Escape_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (IsShowingSettings)
        {
            ShowMainPage();
        }
        else
        {
            HidePopup();
        }
    }
}
