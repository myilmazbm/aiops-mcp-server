namespace AiOps.Mcp.Http;

public sealed class McpEndpointOptions
{
    public const string SectionName = "Mcp";

    /// <summary>
    /// Optional client allow-list (exact IPs) for the MCP endpoint itself.
    ///
    /// The endpoint has no authentication by design — the deployment relies on the network
    /// appliances to restrict who can reach port 5100, and a shared secret in config would
    /// break the no-credentials-in-config rule. That leaves nothing in the application to
    /// stop a host that can route to the port, which is why this list exists as a second
    /// control. Empty means any source may call every tool on the fleet.
    /// </summary>
    public string[] AllowedClientIps { get; set; } = [];
}
