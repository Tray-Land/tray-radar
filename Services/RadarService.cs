using System.Globalization;
using System.Xml.Linq;

namespace TrayRadar.Services;

/// <summary>
/// Access to the NWS/NCEP base reflectivity mosaic (the data behind radar.weather.gov),
/// served as WMS from opengeo.ncep.noaa.gov.
/// </summary>
internal static class RadarService
{
    private const string Layer = "conus_bref_qcd";
    private const string Endpoint = "https://opengeo.ncep.noaa.gov/geoserver/conus/" + Layer + "/ows";
    private const string CapabilitiesUrl = Endpoint + "?service=WMS&version=1.3.0&request=GetCapabilities";

    /// <summary>Frames in the animation loop.</summary>
    public const int FrameCount = 10;

    /// <summary>Minimum spacing between animation frames (the source publishes every ~2 minutes).</summary>
    private static readonly TimeSpan FrameSpacing = TimeSpan.FromMinutes(6);

    private static readonly HttpClient s_http = CreateClient();

    public const string Attribution = "Radar: NOAA / National Weather Service";
    public static readonly Uri AttributionLink = new("https://radar.weather.gov/");

    /// <summary>
    /// Returns up to <see cref="FrameCount"/> recent frame timestamps (UTC, oldest first) taken
    /// from the WMS time dimension. Only listed timestamps are used because the service does not
    /// interpolate between scans.
    /// </summary>
    public static async Task<IReadOnlyList<DateTime>> GetFrameTimesAsync(CancellationToken cancellationToken = default)
    {
        string xml = await s_http.GetStringAsync(CapabilitiesUrl, cancellationToken);
        XDocument doc = XDocument.Parse(xml);

        string? values = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "Dimension" && (string?)e.Attribute("name") == "time")
            ?.Value;
        if (string.IsNullOrWhiteSpace(values))
        {
            return [];
        }

        List<DateTime> all = values
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => DateTime.TryParse(v, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime t) ? t : (DateTime?)null)
            .OfType<DateTime>()
            .Order()
            .ToList();

        // Walk back from the newest scan, keeping frames at least FrameSpacing apart.
        List<DateTime> picked = [];
        for (int i = all.Count - 1; i >= 0 && picked.Count < FrameCount; i--)
        {
            if (picked.Count == 0 || picked[^1] - all[i] >= FrameSpacing - TimeSpan.FromSeconds(30))
            {
                picked.Add(all[i]);
            }
        }

        picked.Reverse();
        return picked;
    }

    /// <summary>
    /// Builds a WinUIEx.Maps tile URL template for the radar at a specific scan time
    /// (or the latest scan when <paramref name="time"/> is null).
    /// </summary>
    public static string BuildTileUrl(DateTime? time)
    {
        string url = Endpoint
            + "?service=WMS&version=1.3.0&request=GetMap"
            + "&layers=" + Layer
            + "&styles=&crs=EPSG:3857&bbox={bbox-epsg-3857}"
            + "&width=256&height=256&format=image/png&transparent=true";

        if (time is DateTime t)
        {
            url += "&time=" + t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }

        return url;
    }

    private static HttpClient CreateClient()
    {
        HttpClient client = new() { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TrayRadar/1.0");
        return client;
    }
}
