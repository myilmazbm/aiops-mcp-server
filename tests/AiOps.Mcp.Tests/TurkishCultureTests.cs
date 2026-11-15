using System.Globalization;
using System.Management.Automation;
using System.Text.Json;
using AiOps.Mcp.Inventory;
using AiOps.Mcp.Remoting;
using AiOps.Mcp.Tools.RunReadonly;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AiOps.Mcp.Tests;

/// <summary>
/// The fleet is Turkish-locale, so any culture-sensitive path (name comparison, casing,
/// number formatting) must behave identically under tr-TR. These tests pin the thread
/// culture so a Current-culture regression that passes on en-US CI fails here.
/// </summary>
public class TurkishCultureTests
{
    private static async Task InTurkishCulture(Func<Task> body)
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
        try
        {
            await body();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public async Task Counter_catalog_translates_ascii_I_names_under_turkish_culture()
    {
        await InTurkishCulture(() =>
        {
            var table = new Dictionary<int, string> { [4] = "Bellek", [1382] = "Kullanılabilir MB" };

            // 'MEMORY'/'AVAILABLE MBYTES' contain ASCII 'I'; an OrdinalIgnoreCase→Current
            // regression would fail to match 'i' under tr-TR and silently fall back.
            var translated = CounterCatalog.Translate(@"\MEMORY\AVAILABLE MBYTES", table);

            Assert.Equal(@"\Bellek\Kullanılabilir MB", translated);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Inventory_section_dispatch_survives_turkish_casing()
    {
        await InTurkishCulture(async () =>
        {
            var executor = Substitute.For<IRemoteExecutor>();
            executor.InvokeAsync(Arg.Any<string>(), Arg.Any<RemoteCommand>(), Arg.Any<CancellationToken>())
                .Returns(new ServerCallResult { Server = "srv1", Status = ServerCallStatus.Ok });

            // 'services'.ToUpper() on tr-TR is 'SERVİCES'; a ToUpper→ToUpperInvariant
            // regression would make the section switch throw ArgumentException here.
            var result = await new InventoryCollector(executor)
                .CollectAsync("srv1", ["services", "hotfix"], CancellationToken.None);

            Assert.DoesNotContain(result.Outcomes, o => o.Error is not null && o.Error.Contains("Unknown"));
        });
    }

    [Fact]
    public async Task Run_readonly_formats_decimals_invariantly_under_turkish_culture()
    {
        await InTurkishCulture(async () =>
        {
            var executor = Substitute.For<IRemoteExecutor>();
            executor.InvokeAsync(Arg.Any<string>(), Arg.Any<RemoteCommand>(), Arg.Any<CancellationToken>())
                .Returns(new ServerCallResult
                {
                    Server = "srv1",
                    Status = ServerCallStatus.Ok,
                    Output = [PSObject.AsPSObject(new { Ratio = 2.5 })],
                });

            var result = await RunReadonlyTool.RunReadonly(
                executor, NullLoggerFactory.Instance, "srv1", "Get-Thing",
                new Dictionary<string, JsonElement>());

            // tr-TR would render 2.5 as "2,5"; the LLM must always see the invariant dot.
            Assert.Equal("2.5", result.Rows[0]["Ratio"]);
        });
    }
}
