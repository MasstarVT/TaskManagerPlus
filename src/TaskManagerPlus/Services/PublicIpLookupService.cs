using System.Net.Http;
using System.Text.Json;

namespace TaskManagerPlus.Services;

/// <summary>Public IP + ISP lookup result (#48).</summary>
public sealed record PublicIpInfo(string Ip, string Isp, string City, string Region, string Country);

/// <summary>
/// Looks up the machine's public IP and ISP via ipinfo.io's free JSON endpoint (#48) - useful for
/// confirming NAT/VPN state, but a real outbound network call, so this is deliberately never
/// called automatically: it only runs when the user clicks the Network tab's "Look up public IP"
/// button.
/// </summary>
public static class PublicIpLookupService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public static async Task<PublicIpInfo?> LookupAsync()
    {
        try
        {
            using var response = await Http.GetAsync("https://ipinfo.io/json");
            if (!response.IsSuccessStatusCode) return null;

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var root = doc.RootElement;

            string ip = StringOrEmpty(root, "ip");
            if (ip.Length == 0) return null;

            return new PublicIpInfo(ip, StringOrEmpty(root, "org"), StringOrEmpty(root, "city"),
                StringOrEmpty(root, "region"), StringOrEmpty(root, "country"));
        }
        catch
        {
            // No internet, DNS failure, service unavailable, etc. - the caller shows "Failed".
            return null;
        }
    }

    private static string StringOrEmpty(JsonElement root, string name)
        => root.TryGetProperty(name, out var el) ? el.GetString() ?? string.Empty : string.Empty;
}
