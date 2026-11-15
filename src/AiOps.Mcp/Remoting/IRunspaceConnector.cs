using System.Management.Automation.Runspaces;
using Microsoft.Extensions.Options;

namespace AiOps.Mcp.Remoting;

/// <summary>Opens runspaces to fleet servers. Abstracted so pool tests can use local runspaces.</summary>
public interface IRunspaceConnector
{
    /// <param name="endpointName">JEA session configuration name on the target.</param>
    Runspace Connect(string server, string endpointName);
}

public sealed class WsmanRunspaceConnector(IOptions<RemotingOptions> options) : IRunspaceConnector
{
    private readonly RemotingOptions _options = options.Value;

    public Runspace Connect(string server, string endpointName)
    {
        var shellUri = "http://schemas.microsoft.com/powershell/" + endpointName;
        var uri = new Uri($"http://{server}:{_options.WinRmPort}/wsman");

        // No credential: the connection runs as the service identity (gMSA) via Kerberos.
        var connectionInfo = new WSManConnectionInfo(uri, shellUri, credential: null!)
        {
            OpenTimeout = _options.OpenTimeoutSeconds * 1000,
            OperationTimeout = _options.PerServerTimeoutSeconds * 1000,
        };

        var runspace = RunspaceFactory.CreateRunspace(connectionInfo);
        runspace.Open();
        return runspace;
    }
}
