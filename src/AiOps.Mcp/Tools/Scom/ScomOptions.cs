namespace AiOps.Mcp.Tools.Scom;

public sealed class ScomOptions
{
    public const string SectionName = "Scom";

    /// <summary>FQDN of the SCOM management server hosting the AIOpsScomReader JEA endpoint. Empty disables the SCOM tools.</summary>
    public string? ManagementServer { get; set; }

    /// <summary>JEA session configuration name on the management server.</summary>
    public string JeaEndpointName { get; set; } = "AIOpsScomReader";

    public bool Configured => !string.IsNullOrWhiteSpace(ManagementServer);
}
