using System.Management.Automation;

namespace AiOps.Mcp.Remoting;

/// <summary>
/// A cmdlet invocation sent to the JEA endpoint, optionally piped into a next command
/// (e.g. Select-Object to flatten nested objects that PSRP would truncate at depth 1).
/// The commands are passed through verbatim: the JEA role capability file decides what
/// is allowed to run, not this layer.
/// </summary>
public sealed record RemoteCommand(
    string CommandName,
    IReadOnlyDictionary<string, object?> Parameters,
    RemoteCommand? Pipe = null,
    string? Endpoint = null)
{
    public static RemoteCommand Create(string commandName, params (string Name, object? Value)[] parameters)
        => new(commandName, parameters.ToDictionary(p => p.Name, p => p.Value));

    public RemoteCommand PipeTo(string commandName, params (string Name, object? Value)[] parameters)
        => this with { Pipe = Create(commandName, parameters) };

    /// <summary>
    /// Targets a JEA session configuration other than the fleet default (AIOpsReader) —
    /// e.g. the SCOM reader role that exists only on the management server. Each
    /// (server, endpoint) pair gets its own pooled session.
    /// </summary>
    public RemoteCommand OnEndpoint(string endpointName) => this with { Endpoint = endpointName };
}

public enum ServerCallStatus
{
    Ok,
    Unreachable,
    Error,
}

public sealed record ServerCallResult
{
    public required string Server { get; init; }
    public required ServerCallStatus Status { get; init; }
    public IReadOnlyList<PSObject> Output { get; init; } = [];
    public string? Error { get; init; }
    /// <summary>PowerShell FullyQualifiedErrorId of the first error, when available.</summary>
    public string? ErrorId { get; init; }
}

public interface IRemoteExecutor
{
    /// <summary>Runs the command on one server over the pooled JEA session.</summary>
    Task<ServerCallResult> InvokeAsync(string server, RemoteCommand command, CancellationToken cancellationToken);

    /// <summary>
    /// Fans the same command out to many servers with bounded concurrency.
    /// A timed-out or failed server yields an Unreachable/Error entry instead of failing the call.
    /// </summary>
    Task<IReadOnlyList<ServerCallResult>> InvokeManyAsync(IReadOnlyList<string> servers, RemoteCommand command, CancellationToken cancellationToken);
}
