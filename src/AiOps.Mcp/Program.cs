using AiOps.Mcp.Inventory;
using AiOps.Mcp.Inventory.Search;
using AiOps.Mcp.Remoting;
using Serilog;
using Serilog.Events;

var useHttp = args.Contains("--http");

// Absolute log path: as a Windows Service the working directory is System32, so a relative
// "logs/" would land there (or be denied). Anchor to the install directory instead.
var logPath = Path.Combine(AppContext.BaseDirectory, "logs", "aiops-mcp-.log");

// In stdio mode stdout carries the MCP protocol, so console logs must go to stderr.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(standardErrorFromLevel: LogEventLevel.Verbose)
    .WriteTo.File(logPath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
    .CreateLogger();

try
{
    if (useHttp)
    {
        // ContentRoot = install directory so appsettings*.json resolve when started by the
        // Service Control Manager (whose cwd is System32).
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        // No-op when launched from a console; under the SCM it performs the service
        // start/stop handshake (without it the SCM times out with error 1053).
        builder.Host.UseWindowsService(options => options.ServiceName = "AiOpsMcp");
        builder.Services.AddSerilog();
        AddCoreServices(builder.Services, builder.Configuration);
        builder.Services
            .AddMcpServer()
            .WithHttpTransport()
            .WithToolsFromAssembly();

        // SCOM webhook (HTTP mode only): accept → normalize → queue → forward.
        builder.Services.Configure<AiOps.Mcp.Alerts.AlertsOptions>(
            builder.Configuration.GetSection(AiOps.Mcp.Alerts.AlertsOptions.SectionName));
        builder.Services.AddSingleton<AiOps.Mcp.Alerts.AlertQueue>();
        builder.Services.AddHttpClient(AiOps.Mcp.Alerts.AlertForwarderService.HttpClientName,
            client => client.Timeout = TimeSpan.FromSeconds(15));
        builder.Services.AddHostedService<AiOps.Mcp.Alerts.AlertForwarderService>();

        builder.Services.Configure<AiOps.Mcp.Http.McpEndpointOptions>(
            builder.Configuration.GetSection(AiOps.Mcp.Http.McpEndpointOptions.SectionName));

        var app = builder.Build();

        // Source check for the MCP endpoint. Every tool runs against the fleet under the
        // service identity, so reaching this port is equivalent to read access on ~500
        // servers; the endpoint itself carries no authentication. The SCOM webhook is
        // excluded because it has its own, different allow-list (management servers, not
        // LLM client hosts).
        var mcpOptions = app.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<AiOps.Mcp.Http.McpEndpointOptions>>().Value;
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/webhooks"))
            {
                var remoteIp = AiOps.Mcp.Http.ClientAddress.Of(context.Connection.RemoteIpAddress);
                if (!AiOps.Mcp.Http.ClientAddress.IsAllowed(remoteIp, mcpOptions.AllowedClientIps))
                {
                    app.Logger.LogWarning("Rejected MCP request from disallowed client {RemoteIp}", remoteIp ?? "(unknown)");
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
            }

            await next().ConfigureAwait(false);
        });

        app.MapMcp();
        app.MapPost("/webhooks/scom", AiOps.Mcp.Alerts.ScomWebhookHandler.HandleAsync);

        if (mcpOptions.AllowedClientIps.Length == 0)
            app.Logger.LogWarning(
                "MCP endpoint accepts tool calls from ANY source: Mcp:AllowedClientIps is empty. " +
                "Reaching this port grants read access to every server in the fleet, so restrict it " +
                "to the LLM client hosts, or ensure network-level access control is in place.");

        // Loudly surface the fail-open default: an empty allow-list is the shipped config, so
        // a missing entry must not silently disable the only application-level source check.
        var alerts = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AiOps.Mcp.Alerts.AlertsOptions>>().Value;
        if (alerts.AllowedSourceIps.Length == 0)
            app.Logger.LogWarning(
                "SCOM webhook /webhooks/scom accepts alerts from ANY source: Alerts:AllowedSourceIps is empty. " +
                "Restrict it to the SCOM management server IPs, or ensure network-level access control is in place.");

        app.Run();
    }
    else
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddSerilog();
        AddCoreServices(builder.Services, builder.Configuration);
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        builder.Build().Run();
    }
}
catch (Exception ex)
{
    Log.Fatal(ex, "AiOps MCP host terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

static void AddCoreServices(IServiceCollection services, IConfiguration configuration)
{
    services.Configure<RemotingOptions>(configuration.GetSection(RemotingOptions.SectionName));
    services.Configure<InventoryOptions>(configuration.GetSection(InventoryOptions.SectionName));
    services.Configure<AiOps.Mcp.Directory.DirectoryOptions>(configuration.GetSection(AiOps.Mcp.Directory.DirectoryOptions.SectionName));
    services.Configure<AiOps.Mcp.Tools.Scom.ScomOptions>(configuration.GetSection(AiOps.Mcp.Tools.Scom.ScomOptions.SectionName));
    services.AddSingleton<AiOps.Mcp.Directory.IDirectoryGateway, AiOps.Mcp.Directory.LdapDirectoryGateway>();
    services.AddSingleton<IRunspaceConnector, WsmanRunspaceConnector>();
    services.AddSingleton<PsSessionPool>();
    services.AddSingleton<IRemoteExecutor, JeaRemoteExecutor>();
    services.AddSingleton<AiOps.Mcp.Tools.GetCounters.CounterNameTableCache>();
    services.AddSingleton<InventoryCollector>();
    services.AddSingleton<IInventoryRepository, SqlInventoryRepository>();
    services.AddHostedService<NightlyInventoryService>();

    // Named client + singleton service (not a typed client): the singleton indexer would
    // otherwise capture one HttpClient/handler for the process lifetime.
    services.AddHttpClient(OpenAiCompatibleEmbeddingClient.HttpClientName, client =>
    {
        client.Timeout = TimeSpan.FromSeconds(30);
        // The endpoint is unauthenticated plain HTTP; without this cap a hostile/broken
        // response could buffer up to the 2 GiB framework default and OOM the host.
        // A bge-m3 batch is well under 16 MB.
        client.MaxResponseContentBufferSize = 16 * 1024 * 1024;
    });
    services.AddSingleton<IEmbeddingClient, OpenAiCompatibleEmbeddingClient>();
    services.AddSingleton<IVectorIndex, QdrantVectorIndex>();
    var searchConfigured = configuration.GetSection(InventoryOptions.SectionName).Get<InventoryOptions>()?.SearchConfigured == true;
    if (searchConfigured)
        services.AddSingleton<IInventoryIndexer, QdrantInventoryIndexer>();
    else
        services.AddSingleton<IInventoryIndexer, NoOpInventoryIndexer>();
}
