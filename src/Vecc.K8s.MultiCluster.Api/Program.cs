using KubeOps.Operator;
using KubeOps.Operator.Leadership;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Serilog;
using StackExchange.Redis;
using System.Net;
using Vecc.Dns.Server;
using Vecc.K8s.MultiCluster.Api.Services;
using Vecc.K8s.MultiCluster.Api.Services.Default;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog((context, configuration) =>
{
    configuration.MinimumLevel.Information()
                 .MinimumLevel.Override("Microsoft.AspNetCore.Hosting.Diagnostics", Serilog.Events.LogEventLevel.Warning)
                 .MinimumLevel.Override("Vecc", Serilog.Events.LogEventLevel.Verbose);
    configuration.WriteTo.Console(new Serilog.Formatting.Json.JsonFormatter());
});
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddKubernetesOperator((operatorSettings) =>
{
    operatorSettings.EnableLeaderElection = false;
    operatorSettings.OnlyWatchEventsWhenLeader = true;
});

if (args.Contains("--orchestrator") ||
    args.Contains("--dns-server") ||
    args.Contains("--frontend"))
{
    builder.Services.AddSingleton<LeaderStatus>();
    builder.Services.AddSingleton<DefaultLeaderStateChangeObserver>();
    builder.Services.AddSingleton<DefaultDnsResolver>();
    builder.Services.AddSingleton<IIngressManager, DefaultIngressManager>();
    builder.Services.AddSingleton<INamespaceManager, DefaultNamespaceManager>();
    builder.Services.AddSingleton<IServiceManager, DefaultServiceManager>();
    builder.Services.AddSingleton<IHostnameSynchronizer, DefaultHostnameSynchronizer>();
    builder.Services.AddSingleton<ICache, RedisCache>();
    builder.Services.AddSingleton<IDnsHost, DefaultDnsHost>();
    builder.Services.AddSingleton<IDateTimeProvider, DefaultDateTimeProvider>();
    builder.Services.AddSingleton<IRandom, DefaultRandom>();
    builder.Services.AddSingleton<IDnsServer, DnsServer>();
    builder.Services.AddSingleton<Vecc.Dns.ILogger, DefaultDnsLogging>();
    builder.Services.AddSingleton<IDnsResolver>(sp => sp.GetRequiredService<DefaultDnsResolver>());

    builder.Services.Configure<DnsServerOptions>(builder.Configuration.GetSection("DnsServer"));
    builder.Services.AddSingleton<DnsServerOptions>(sp => sp.GetRequiredService<IOptions<DnsServerOptions>>().Value);

    builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
    {
        //TODO: make this configurable
        var endpoints = new EndPointCollection
                        {
                            new IPEndPoint(IPAddress.Loopback, 6379)
                        };

        var multiplexer = ConnectionMultiplexer.Connect(new ConfigurationOptions
        {
            EndPoints = endpoints
        });
        return multiplexer;
    });

    builder.Services.AddSingleton<IDatabase>(sp =>
    {
        var multiplexer = sp.GetRequiredService<IConnectionMultiplexer>();
        var database = multiplexer.GetDatabase();
        return database;
    });

    builder.Services.AddSingleton<ISubscriber>(sp =>
    {
        var multiplexer = sp.GetRequiredService<IConnectionMultiplexer>();
        var subscriber = multiplexer.GetSubscriber();
        return subscriber;
    });

    builder.Services.AddSingleton<IQueue, RedisQueue>();
}

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseSwagger();
app.UseSwaggerUI();

app.UseAuthorization();

app.MapControllers();
app.UseKubernetesOperator();

var logger = app.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("Starting");

var processTasks = new List<Task>();
var addedOperator = false;

//watches cluster events and keeps the local cluster config in sync and sends updates to other nodes
//also keeps track of remote clusters and whether they are up or not
if (args.Contains("--orchestrator"))
{
    logger.LogInformation("Running the orchestrator");
    var defaultLeaderStateChangeObserver = app.Services.GetRequiredService<DefaultLeaderStateChangeObserver>();
    app.Services.GetRequiredService<ILeaderElection>().LeadershipChange.Subscribe(defaultLeaderStateChangeObserver);
    var hostnameSynchronizer = app.Services.GetRequiredService<IHostnameSynchronizer>();

    processTasks.Add(app.RunOperatorAsync(Array.Empty<string>()));
    processTasks.Add(hostnameSynchronizer.ClusterHeartbeatAsync());
    processTasks.Add(hostnameSynchronizer.WatchClusterHeartbeatsAsync());

    addedOperator = true;
}

//starts the dns server to respond to dns queries for the respective hosts
if (args.Contains("--dns-server"))
{
    logger.LogInformation("Running the dns server");
    var dnsHost = app.Services.GetRequiredService<IDnsHost>();
    var dnsResolver = app.Services.GetRequiredService<DefaultDnsResolver>();
    var queue = app.Services.GetRequiredService<IQueue>();

    queue.OnHostChangedAsync = dnsResolver.OnHostChangedAsync;

    logger.LogInformation("Starting the dns server");
    processTasks.Add(dnsHost.StartAsync());
    if (!addedOperator)
    {
        processTasks.Add(app.RunOperatorAsync(Array.Empty<string>()));
        addedOperator = true;
    }
}

//starts the api server
if (args.Contains("--front-end"))
{
    logger.LogInformation("Running the front end");
 
    if (!addedOperator)
    {
        processTasks.Add(app.RunOperatorAsync(Array.Empty<string>()));
        addedOperator = true;
    }
}


if (!addedOperator)
{
    logger.LogInformation("Running underlying KubeOps");

    processTasks.Add(app.RunOperatorAsync(args));
}

logger.LogInformation("Waiting on process tasks");
await Task.WhenAll(processTasks);
logger.LogInformation("Terminated");