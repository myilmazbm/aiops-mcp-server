using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Remoting;
using Microsoft.Extensions.Options;

namespace AiOps.Mcp.Remoting;

public sealed class JeaRemoteExecutor(
    PsSessionPool pool,
    IOptions<RemotingOptions> options,
    ILogger<JeaRemoteExecutor> logger) : IRemoteExecutor
{
    private readonly RemotingOptions _options = options.Value;

    public async Task<ServerCallResult> InvokeAsync(string server, RemoteCommand command, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        // The pool arms the per-server timeout only after the session is acquired and passes
        // it as workToken, so queue time doesn't count against it.
        var endpoint = command.Endpoint ?? _options.JeaEndpointName;
        try
        {
            var result = await pool.UseSessionAsync(server, endpoint, async (runspace, workToken) =>
            {
                using var ps = PowerShell.Create();
                ps.Runspace = runspace;
                for (var stage = command; stage is not null; stage = stage.Pipe)
                {
                    ps.AddCommand(stage.CommandName);
                    foreach (var (name, value) in stage.Parameters)
                    {
                        if (value is not null)
                            ps.AddParameter(name, value);
                    }
                }

                await using var registration = workToken.Register(() => ps.BeginStop(null, null));
                List<PSObject> output;
                try
                {
                    output = (await ps.InvokeAsync().ConfigureAwait(false)).ToList();
                }
                catch (PipelineStoppedException)
                {
                    // Our registration is the only stop source: either the caller cancelled
                    // or the per-server timeout fired.
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new TimeoutException(TimeoutMessage());
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (workToken.IsCancellationRequested)
                    throw new TimeoutException(TimeoutMessage());

                if (ps.HadErrors && ps.Streams.Error.Count > 0)
                {
                    var first = ps.Streams.Error[0];
                    return new ServerCallResult
                    {
                        Server = server,
                        Status = ServerCallStatus.Error,
                        Output = output,
                        Error = first.ToString(),
                        ErrorId = first.FullyQualifiedErrorId,
                    };
                }

                return new ServerCallResult { Server = server, Status = ServerCallStatus.Ok, Output = output };
            }, TimeSpan.FromSeconds(_options.PerServerTimeoutSeconds), cancellationToken).ConfigureAwait(false);

            LogCall(server, command, stopwatch, result);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // TimeoutException (work) or an OCE from the pool's connect-budget both mean the
            // per-server window elapsed, not a caller cancellation.
            var timedOut = ex is TimeoutException || ex is OperationCanceledException;
            var result = Classify(server, ex, timedOut);
            LogCall(server, command, stopwatch, result);
            return result;
        }

        string TimeoutMessage() => $"No response within {_options.PerServerTimeoutSeconds} s";
    }

    public Task<IReadOnlyList<ServerCallResult>> InvokeManyAsync(
        IReadOnlyList<string> servers, RemoteCommand command, CancellationToken cancellationToken)
        => FanOut.RunAsync(
            servers, _options.MaxConcurrency,
            server => InvokeAsync(server, command, cancellationToken),
            cancellationToken);

    private static ServerCallResult Classify(string server, Exception ex, bool timedOut)
    {
        var unreachable = timedOut
            || ex is TimeoutException
            || ex is PSRemotingTransportException
            || ex is OperationCanceledException;

        return new ServerCallResult
        {
            Server = server,
            Status = unreachable ? ServerCallStatus.Unreachable : ServerCallStatus.Error,
            Error = ex.Message,
            ErrorId = (ex as RuntimeException)?.ErrorRecord?.FullyQualifiedErrorId,
        };
    }

    private void LogCall(string server, RemoteCommand command, Stopwatch stopwatch, ServerCallResult result)
    {
        logger.LogInformation(
            "Remote call {Command} on {Server}: {Status} in {ElapsedMs} ms, {OutputCount} objects",
            command.CommandName, server, result.Status, stopwatch.ElapsedMilliseconds, result.Output.Count);
    }
}
