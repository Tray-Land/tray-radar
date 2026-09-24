using Windows.Devices.Geolocation;

namespace TrayRadar.Services;

internal static class LocationService
{
    // Contiguous US (lower 48) bounding box. Keep in sync with tools/Build-ZipData.ps1.
    private const double MinLat = 24.4;
    private const double MaxLat = 49.5;
    private const double MinLon = -125.0;
    private const double MaxLon = -66.9;

    public static bool IsInConus(double lat, double lon) =>
        lat is >= MinLat and <= MaxLat && lon is >= MinLon and <= MaxLon;

    /// <summary>
    /// Requests the device's current location. Returns null (with a user-facing reason) when
    /// access is denied, the position is unavailable, or the lookup times out.
    /// </summary>
    public static async Task<(double Lat, double Lon)?> TryGetDeviceLocationAsync(TimeSpan timeout, Action<string>? onError = null)
    {
        try
        {
            GeolocationAccessStatus access = await Geolocator.RequestAccessAsync();
            if (access != GeolocationAccessStatus.Allowed)
            {
                onError?.Invoke("Location access is turned off. Enable it in Windows Settings > Privacy & security > Location, or enter a ZIP code.");
                return null;
            }

            Geolocator locator = new() { DesiredAccuracy = PositionAccuracy.Default };
            Geoposition position = await locator
                .GetGeopositionAsync(maximumAge: TimeSpan.FromMinutes(10), timeout: timeout);
            BasicGeoposition p = position.Coordinate.Point.Position;
            return (p.Latitude, p.Longitude);
        }
        catch (Exception)
        {
            onError?.Invoke("Couldn't determine your location. Try again or enter a ZIP code.");
            return null;
        }
    }
}
