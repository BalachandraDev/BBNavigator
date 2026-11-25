using System.Net;
using System.Net.Sockets;

namespace FlowMind.Infrastructure.Storage;

/// <summary>
/// A company admin types the storage address, and the server then calls it. That is a server-side request forgery risk, so by default only
/// public https addresses are accepted: no localhost, private/LAN ranges, link-local (cloud metadata) or addresses that resolve to them.
/// The server operator can allow private addresses (for a MinIO server on the same network) with Storage:AllowPrivateEndpoints.
/// </summary>
public static class EndpointGuard
{
    /// <returns>null when the address may be used, otherwise the reason in plain words.</returns>
    public static string? Check(string endpoint, bool allowPrivate, Func<string, IPAddress[]>? resolve = null)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http") || string.IsNullOrEmpty(u.Host))
            return "The address must be a full web address such as https://storage.example.com.";
        if (!string.IsNullOrEmpty(u.UserInfo)) return "The address must not contain a user name or password. Use the access key fields instead.";
        if (u.Scheme == "http" && !allowPrivate) return "The address must start with https://.";
        if (allowPrivate) return null;

        var host = u.Host.ToLowerInvariant();
        if (host == "localhost" || host.EndsWith(".localhost") || host.EndsWith(".local") || host.EndsWith(".internal") || host.EndsWith(".lan") || !host.Contains('.') && !IPAddress.TryParse(host, out _))
            return "That address points to this server or a private network. Ask the server operator to allow private storage addresses, or use a public one.";
        IPAddress[] addresses;
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal)) addresses = new[] { literal };
        else
        {
            try { addresses = (resolve ?? (h => Dns.GetHostAddresses(h)))(host); }
            catch (Exception ex) when (ex is SocketException or ArgumentException) { return "That address could not be found. Check the spelling."; }
        }
        return addresses.Any(IsPrivate) ? "That address points to this server or a private network. Ask the server operator to allow private storage addresses, or use a public one." : null;
    }

    public static bool IsPrivate(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6None)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;   // fc00::/7 unique local
        var b = ip.GetAddressBytes();
        return b[0] == 10 || b[0] == 127 || b[0] == 0
            || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)                       // link-local, includes the cloud metadata address
            || (b[0] == 100 && b[1] is >= 64 and <= 127)          // carrier-grade NAT
            || b[0] >= 224;                                       // multicast and reserved
    }
}