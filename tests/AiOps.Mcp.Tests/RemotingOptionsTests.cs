using AiOps.Mcp.Remoting;

namespace AiOps.Mcp.Tests;

public class RemotingOptionsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Non_positive_config_values_clamp_to_one(int bad)
    {
        // Configuration binds raw values; 0 would mean SemaphoreSlim(0) (nobody ever admitted),
        // a zero timeout, or a pool that evicts every slot it creates.
        var options = new RemotingOptions
        {
            MaxConcurrency = bad,
            PerServerTimeoutSeconds = bad,
            MaxSessions = bad,
            OpenTimeoutSeconds = bad,
        };

        Assert.Equal(1, options.MaxConcurrency);
        Assert.Equal(1, options.PerServerTimeoutSeconds);
        Assert.Equal(1, options.MaxSessions);
        Assert.Equal(1, options.OpenTimeoutSeconds);
    }

    [Fact]
    public void Defaults_match_the_documented_performance_rules()
    {
        var options = new RemotingOptions();

        Assert.Equal(20, options.MaxConcurrency);
        Assert.Equal(30, options.PerServerTimeoutSeconds);
        Assert.Equal(100, options.MaxSessions);
    }
}
