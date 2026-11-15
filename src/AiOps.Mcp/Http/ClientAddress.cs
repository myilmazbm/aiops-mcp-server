using System.Net;

namespace AiOps.Mcp.Http;

/// <summary>Shared source-address handling for the two HTTP surfaces (MCP and the SCOM webhook).</summary>
public static class ClientAddress
{
    /// <summary>
    /// Kestrel reports IPv4 clients as IPv4-mapped IPv6 (::ffff:10.0.0.5); fold back so an
    /// allow-list can be written with plain IPv4 addresses.
    /// </summary>
    public static string? Of(IPAddress? remoteAddress)
    {
        if (remoteAddress is { IsIPv4MappedToIPv6: true })
            remoteAddress = remoteAddress.MapToIPv4();
        return remoteAddress?.ToString();
    }

    /// <summary>An empty allow-list permits everything; that fail-open default is logged at startup.</summary>
    public static bool IsAllowed(string? remoteIp, string[] allowed)
        => allowed is not { Length: > 0 }
           || (remoteIp is not null && allowed.Contains(remoteIp, StringComparer.OrdinalIgnoreCase));
}
