using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.System.Registry;

namespace TrayRadar.Services;

/// <summary>
/// Reports the Windows (taskbar and Start) theme, which can differ from the app theme, and raises
/// <see cref="Changed"/> when it flips.
/// </summary>
internal static class SystemThemeService
{
    private const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string SystemUsesLightThemeValue = "SystemUsesLightTheme";

    private static readonly RegistryKey? PersonalizeKey = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath);
    private static readonly AutoResetEvent KeyChanged = new(false);
    private static bool _isLight = ReadIsLight();

    static SystemThemeService()
    {
        if (PersonalizeKey is null)
        {
            return;
        }

        ThreadPool.RegisterWaitForSingleObject(KeyChanged, (_, _) => OnKeyChanged(), null, Timeout.Infinite, executeOnlyOnce: false);
        WatchKey();
    }

    /// <summary>Raised on a background thread when the Windows theme switches between light and dark.</summary>
    public static event EventHandler? Changed;

    /// <summary>True when the Windows theme is light; false when dark or unknown.</summary>
    public static bool IsLight => _isLight;

    private static void OnKeyChanged()
    {
        WatchKey();
        bool isLight = ReadIsLight();
        if (isLight != _isLight)
        {
            _isLight = isLight;
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    // One-shot: the key must be re-watched after every notification.
    private static void WatchKey() =>
        PInvoke.RegNotifyChangeKeyValue(
            PersonalizeKey!.Handle,
            false,
            REG_NOTIFY_FILTER.REG_NOTIFY_CHANGE_LAST_SET | REG_NOTIFY_FILTER.REG_NOTIFY_THREAD_AGNOSTIC,
            KeyChanged.SafeWaitHandle,
            true);

    private static bool ReadIsLight() => PersonalizeKey?.GetValue(SystemUsesLightThemeValue) is int value && value != 0;
}
