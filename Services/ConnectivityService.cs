using Windows.Networking.Connectivity;

namespace TrayRadar.Services;

/// <summary>Reports whether Windows sees an internet connection, and when network status changes.</summary>
internal static class ConnectivityService
{
    static ConnectivityService() =>
        NetworkInformation.NetworkStatusChanged += _ => NetworkStatusChanged?.Invoke(null, EventArgs.Empty);

    /// <summary>Raised on a background thread when any network connection changes.</summary>
    public static event EventHandler? NetworkStatusChanged;

    /// <summary>
    /// True when Windows reports internet access. This is a hint only (VPNs and captive portals can
    /// fool it), so use it to explain a failed request rather than to skip one.
    /// </summary>
    public static bool IsInternetAvailable
    {
        get
        {
            try
            {
                ConnectionProfile? profile = NetworkInformation.GetInternetConnectionProfile();
                return profile?.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
            }
            catch (Exception)
            {
                // Unknown: assume online and let the request itself decide.
                return true;
            }
        }
    }
}
