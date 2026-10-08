using System.Net;
using BH3.Persistence;
using BH3.Server.Configuration;
using BH3.Server.Diagnostics;
using BH3.Server.Transport;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace BH3.Server.Hosting;

public sealed class ServerRuntime : IAsyncDisposable
{
    private readonly ServerConfig config;
    private readonly TextWriter? output;
    private ServerLog? log;
    private WebApplication? api;
    private FileStream? lease;
    private UdpGameHost? udp;
    private bool ready;
    public ServerComposition Composition { get; }
    public int GamePort => udp!.Port;
    public string HealthAddress { get; private set; } = "";
    public bool Ready => ready && udp?.Running == true;
    public ServerRuntime(ServerConfig config, TextWriter? output = null, byte[]? authenticationKey = null)
    { this.config = config; this.output = output; Composition = new(config, authenticationKey); }

    public async Task StartAsync(CancellationToken token = default, bool allowEphemeralPorts = false)
    {
        if (api is not null || udp is not null) throw new InvalidOperationException("Server is already started.");
        config.Validate(allowEphemeralPorts);
        try
        {
            string database = config.ResolveDatabase(); Directory.CreateDirectory(Path.GetDirectoryName(database)!);
            // Two server instances must not own one player database at the same time.
            lease = new FileStream(database + ".host.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            log = new(config.ResolveLogs(), output); Composition.Initialize();
            udp = new(Composition.Sessions, config.TransportMode == "kcp" ? Composition.Dispatcher : null, (name, data) => log.Write("info", name, data));
            udp.Start(IPAddress.Parse(config.BindAddress), config.GamePort);
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = config.BaseDirectory });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Parse(config.BindAddress), config.HealthPort));
            api = builder.Build();
            api.MapGet("/health/live", () => Results.Json(new { status = "live" }));
            api.MapGet("/health/ready", () => Results.Json(Status(), statusCode: Ready ? 200 : 503));
            api.MapGet("/api/status", () => Results.Json(Status()));
            await api.StartAsync(token);
            HealthAddress = api.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            ready = true;
            log.Write("info", "server.ready", new { gamePort = GamePort, healthAddress = HealthAddress, schema = SchemaMigrator.CurrentVersion, transport = config.TransportMode, gameplayReady = false });
        }
        catch { await DisposeAsync(); throw; }
    }
    public object Status() => new
    {
        service = "BH3.Server", version = "1.2.4", clientVersion = config.ClientVersion,
        processId = Environment.ProcessId, gamePort = udp?.Port,
        hostReady = Ready, gameplayReady = false, transport = config.TransportMode,
        schemaVersion = SchemaMigrator.CurrentVersion, sessions = Composition.Sessions.Count,
        registeredCommands = Composition.Dispatcher.RegisteredCommands, udp = udp?.Counters
    };
    public async ValueTask DisposeAsync()
    {
        ready = false;
        try
        {
            if (api is not null)
            {
                try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await api.StopAsync(timeout.Token); }
                finally { await api.DisposeAsync(); api = null; }
            }
        }
        finally
        {
            try { if (udp is not null) await udp.DisposeAsync(); }
            finally
            {
                udp = null; lease?.Dispose(); lease = null;
                if (log is not null) { try { log.Write("info", "server.stopped", new { sessions = Composition.Sessions.Count }); } finally { log.Dispose(); log = null; } }
            }
        }
    }
}
