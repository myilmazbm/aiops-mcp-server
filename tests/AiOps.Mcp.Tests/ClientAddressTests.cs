using System.Net;
using AiOps.Mcp.Http;

namespace AiOps.Mcp.Tests;

/// <summary>
/// The source allow-list is the only application-level control on either HTTP surface: the
/// MCP endpoint has no authentication, and reaching it grants read access to the whole fleet.
/// </summary>
public class ClientAddressTests
{
    [Fact]
    public void Ipv4_clients_reported_as_mapped_ipv6_fold_back_to_plain_ipv4()
    {
        // Kestrel hands us ::ffff:10.0.0.5; an operator writes 10.0.0.5 in the allow-list.
        var mapped = IPAddress.Parse("10.0.0.5").MapToIPv6();

        Assert.Equal("10.0.0.5", ClientAddress.Of(mapped));
    }

    [Fact]
    public void A_genuine_ipv6_client_is_not_mangled()
        => Assert.Equal("fe80::1", ClientAddress.Of(IPAddress.Parse("fe80::1")));

    [Fact]
    public void An_empty_allow_list_permits_everything()
    {
        // Fail-open is the shipped default so an unconfigured install still works; Program.cs
        // logs a warning at startup precisely because this branch is permissive.
        Assert.True(ClientAddress.IsAllowed("10.0.0.5", []));
        Assert.True(ClientAddress.IsAllowed(null, []));
    }

    [Fact]
    public void A_configured_allow_list_rejects_everything_else()
    {
        string[] allowed = ["10.0.0.5", "10.0.0.6"];

        Assert.True(ClientAddress.IsAllowed("10.0.0.6", allowed));
        Assert.False(ClientAddress.IsAllowed("10.0.0.7", allowed));
    }

    [Fact]
    public void An_unknown_source_is_rejected_once_a_list_is_configured()
    {
        // RemoteIpAddress is null for a connection with no remote endpoint; that must not be
        // read as "not in the deny set, therefore fine".
        Assert.False(ClientAddress.IsAllowed(null, ["10.0.0.5"]));
    }
}
