using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TrayRadar.Services;
using TrayRadar.Views;
using WinUIEx;

namespace TrayRadar;

/// <summary>
/// Tray-only application: no window at launch, just a notification-area icon that toggles the
/// radar flyout (pattern from github.com/TheJoeFin/Traydio).
/// </summary>
public partial class App : Application
{
    private const string MutexName = "Local\\TrayRadar_SingleInstance";
    private const string ShowEventName = "Local\\TrayRadar_ShowFlyout";
    private static readonly TimeSpan IdleCollectDelay = TimeSpan.FromSeconds(5);

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showEvent;
    private TrayIcon? _trayIcon;
    private RadarFlyoutWindow? _flyout;
    private OnboardingWindow? _onboarding;
    private DispatcherQueue? _dispatcher;
    private bool _isExiting;

    public App()
    {
        InitializeComponent();
        // A tray app lives on after its windows close; only the Exit menu ends it.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        UnhandledException += (_, e) => LogCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);
    }

    private static void LogCrash(Exception? ex)
    {
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "TrayRadar-crash.log");
            File.AppendAllText(path, $"[{DateTime.Now:O}] {ex}{Environment.NewLine}");
        }
        catch
        {
            // Never throw from the crash logger.
        }
    }

    public static new App Current => (App)Application.Current;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _singleInstanceMutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // Another instance owns the tray icon: ask it to open its flyout, then quit.
            try
            {
                // Let the running instance take the foreground when it shows its flyout.
                Windows.Win32.PInvoke.AllowSetForegroundWindow(unchecked((uint)-1)); // ASFW_ANY
                using EventWaitHandle existing = EventWaitHandle.OpenExisting(ShowEventName);
                existing.Set();
            }
            catch
            {
                // The other instance may be shutting down; nothing to do.
            }

            Exit();
            return;
        }

        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        ThreadPool.RegisterWaitForSingleObject(_showEvent,
            (_, _) => _dispatcher.TryEnqueue(ShowFlyoutOrOnboarding), null, Timeout.Infinite, executeOnlyOnce: false);

        InitializeTrayIcon();

        if (!SettingsService.IsOnboarded || SettingsService.HomeLocation is null)
        {
            ShowOnboarding();
        }
    }

    public void ShowSettings()
    {
        RadarFlyoutWindow flyout = EnsureFlyout();
        flyout.ShowSettingsPage();
        if (!flyout.IsPopupVisible)
        {
            flyout.ShowPopup();
        }
    }

    private void InitializeTrayIcon()
    {
        _trayIcon = new TrayIcon(1, TrayIconPath, "Tray Radar");
        _trayIcon.Selected += (_, _) => ToggleFlyout();
        _trayIcon.ContextMenu += TrayIcon_ContextMenu;
        _trayIcon.IsVisible = true;
        WindowPlacementService.SetTrayIcon(_trayIcon);
        SystemThemeService.Changed += (_, _) => _dispatcher?.TryEnqueue(() => _trayIcon?.SetIcon(TrayIconPath));
    }

    // The white glyph disappears on a light taskbar, which follows the Windows theme, not the app theme.
    private static string TrayIconPath => SystemThemeService.IsLight ? "Assets/AppIcon-dark.ico" : "Assets/AppIcon.ico";

    private void TrayIcon_ContextMenu(TrayIcon sender, TrayIconEventArgs args)
    {
        MenuFlyout menu = new();
        MenuFlyoutItem open = new() { Text = "Open radar", Icon = new FontIcon { Glyph = "" } };
        open.Click += (_, _) => ShowFlyoutOrOnboarding();
        MenuFlyoutItem settings = new() { Text = "Settings", Icon = new FontIcon { Glyph = "" } };
        settings.Click += (_, _) => ShowSettings();
        MenuFlyoutItem exit = new() { Text = "Exit", Icon = new FontIcon { Glyph = "" } };
        exit.Click += (_, _) => ExitApp();

        menu.Items.Add(open);
        menu.Items.Add(settings);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(exit);
        args.Flyout = menu;
    }

    private void ToggleFlyout()
    {
        if (!SettingsService.IsOnboarded)
        {
            ShowOnboarding();
            return;
        }

        EnsureFlyout().Toggle();
    }

    private void ShowFlyoutOrOnboarding()
    {
        if (!SettingsService.IsOnboarded)
        {
            ShowOnboarding();
            return;
        }

        RadarFlyoutWindow flyout = EnsureFlyout();
        flyout.HidePopup();
        flyout.ShowMainPage();
        flyout.ShowPopup();
    }

    private RadarFlyoutWindow EnsureFlyout()
    {
        if (_flyout is null)
        {
            // The flyout closes itself after staying hidden for a while; the next open builds a new one.
            _flyout = new RadarFlyoutWindow();
            _flyout.Closed += (_, _) =>
            {
                _flyout = null;
                ReleaseIdleResourcesIfNoWindows();
            };
        }

        return _flyout;
    }

    private void ShowOnboarding()
    {
        if (_onboarding is null)
        {
            _onboarding = new OnboardingWindow();
            _onboarding.Completed += (_, _) => _dispatcher?.TryEnqueue(ShowFlyoutOrOnboarding);
            _onboarding.Closed += (_, _) =>
            {
                _onboarding = null;
                ReleaseIdleResourcesIfNoWindows();
            };
        }

        _onboarding.Activate();
        _onboarding.BringToFront();
    }

    private void ReleaseIdleResourcesIfNoWindows()
    {
        if (_flyout is null && _onboarding is null && !_isExiting)
        {
            ReleaseIdleResources();
        }
    }

    private static void ReleaseIdleResources()
    {
        ZipLookupService.Unload();

        // An idle tray app allocates almost nothing, so the GC won't run on its own and the closed
        // window's memory never goes back to Windows. Wait for WinUI to finish tearing the window
        // down first, or its objects are still referenced and just get promoted. Then run finalizers
        // (the map renderer frees its Direct3D device in one) and collect what they released. Off
        // the UI thread because some finalizers may need to reach it.
        _ = Task.Run(async () =>
        {
            await Task.Delay(IdleCollectDelay);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        });
    }

    private void ExitApp()
    {
        _isExiting = true;
        _flyout?.CloseWindow();
        _onboarding?.Close();

        if (_trayIcon is not null)
        {
            _trayIcon.IsVisible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _showEvent?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        Exit();
    }
}
