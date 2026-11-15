namespace AiOps.Mcp.Remoting;

public static class ServerCallStatusExtensions
{
    /// <summary>The single wire-format mapping every tool result uses: ok | unreachable | error.</summary>
    public static string ToWire(this ServerCallStatus status) => status switch
    {
        ServerCallStatus.Ok => "ok",
        ServerCallStatus.Unreachable => "unreachable",
        _ => "error",
    };
}
