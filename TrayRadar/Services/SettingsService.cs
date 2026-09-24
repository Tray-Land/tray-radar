using Windows.Storage;

namespace TrayRadar.Services;

public enum LocationMode
{
    Device,
    Zip,
}

/// <summary>User-selectable basemap styles (mapped to WinUIEx.Maps styles in the flyout).</summary>
public enum BasemapStyle
{
    Auto,
    Road,
    Night,
    Grayscale,
    Satellite,
}

/// <summary>
/// Typed wrapper over <see cref="ApplicationData.LocalSettings"/>. Every read falls back to a
/// default so a missing or corrupt value never crashes the app.
/// </summary>
internal static class SettingsService
{
    public const double DefaultZoomLevel = 7;

    /// <summary>Raised after a setting that affects the map (style, opacity, location) changes.</summary>
    public static event EventHandler? MapSettingsChanged;

    public static bool IsOnboarded
    {
        get => Get(nameof(IsOnboarded), false);
        set => Set(nameof(IsOnboarded), value);
    }

    public static LocationMode LocationMode
    {
        get => (LocationMode)Get(nameof(LocationMode), (int)LocationMode.Zip);
        set => Set(nameof(LocationMode), (int)value);
    }

    public static string? Zip
    {
        get => Get<string?>(nameof(Zip), null);
        set => Set(nameof(Zip), value);
    }

    /// <summary>The user's home location (from the device or ZIP code), or null before onboarding.</summary>
    public static (double Lat, double Lon)? HomeLocation
    {
        get => GetPoint("HomeLat", "HomeLon");
        set => SetPoint("HomeLat", "HomeLon", value);
    }

    /// <summary>The saved default map center, or null to use <see cref="HomeLocation"/>.</summary>
    public static (double Lat, double Lon)? DefaultCenter
    {
        get => GetPoint("DefaultCenterLat", "DefaultCenterLon");
        set => SetPoint("DefaultCenterLat", "DefaultCenterLon", value);
    }

    public static double DefaultZoom
    {
        get => Get(nameof(DefaultZoom), DefaultZoomLevel);
        set => Set(nameof(DefaultZoom), value);
    }

    public static BasemapStyle BasemapStyle
    {
        get => (BasemapStyle)Get(nameof(BasemapStyle), (int)BasemapStyle.Auto);
        set { Set(nameof(BasemapStyle), (int)value); RaiseMapSettingsChanged(); }
    }

    public static double RadarOpacity
    {
        get => Math.Clamp(Get(nameof(RadarOpacity), 0.75), 0.1, 1.0);
        set { Set(nameof(RadarOpacity), Math.Clamp(value, 0.1, 1.0)); RaiseMapSettingsChanged(); }
    }

    /// <summary>Whether the flyout marks the home location on the map.</summary>
    public static bool ShowLocationPin
    {
        get => Get(nameof(ShowLocationPin), true);
        set { Set(nameof(ShowLocationPin), value); RaiseMapSettingsChanged(); }
    }

    /// <summary>Saves a new home location and resets the default view to center on it.</summary>
    public static void SetHome(LocationMode mode, double lat, double lon, string? zip)
    {
        LocationMode = mode;
        Zip = zip;
        HomeLocation = (lat, lon);
        ResetDefaultView();
    }

    public static void ResetDefaultView()
    {
        DefaultCenter = null;
        DefaultZoom = DefaultZoomLevel;
        RaiseMapSettingsChanged();
    }

    public static void RaiseMapSettingsChanged() => MapSettingsChanged?.Invoke(null, EventArgs.Empty);

    private static (double, double)? GetPoint(string latKey, string lonKey)
    {
        double lat = Get(latKey, double.NaN);
        double lon = Get(lonKey, double.NaN);
        return double.IsNaN(lat) || double.IsNaN(lon) ? null : (lat, lon);
    }

    private static void SetPoint(string latKey, string lonKey, (double Lat, double Lon)? value)
    {
        Set(latKey, value?.Lat);
        Set(lonKey, value?.Lon);
    }

    private static T Get<T>(string key, T defaultValue)
    {
        try
        {
            return ApplicationData.Current.LocalSettings.Values.TryGetValue(key, out object? value) && value is T typed
                ? typed
                : defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }

    private static void Set(string key, object? value)
    {
        try
        {
            if (value is null)
            {
                ApplicationData.Current.LocalSettings.Values.Remove(key);
            }
            else
            {
                ApplicationData.Current.LocalSettings.Values[key] = value;
            }
        }
        catch
        {
            // Settings are best-effort; ignore storage failures.
        }
    }
}
