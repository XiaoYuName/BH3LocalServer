using System.Text;
using System.Text.Json;
using BH3.Server.Configuration;
using BH3.Server.Hosting;
using BH3.Server.Preflight;

namespace BH3.Server;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            string configPath = Path.Combine(AppContext.BaseDirectory, "config", "server.json");
            bool check = false, launcher = false; int? gamePort = null; string? transport = null;
            for (int i = 0; i < args.Length; i++)
                switch (args[i])
                {
                    case "--help":
                        Console.WriteLine("BH3.Server [--config <server.json>] [--check] [--launcher-control] [--game-port <port>] [--transport kcp|handshake-only]");
                        Console.WriteLine("--check: read-only configuration/schema check; no listeners.\nCtrl+C / redirected stdin 'stop' or EOF: graceful shutdown.");
                        return 0;
                    case "--config" when i + 1 < args.Length: configPath = args[++i]; break;
                    case "--transport" when i + 1 < args.Length: transport = args[++i]; break;
                    case "--game-port" when i + 1 < args.Length:
                        if (!int.TryParse(args[++i], out int value)) throw new ArgumentException("Invalid --game-port.");
                        gamePort = value; break;
                    case "--check": check = true; break;
                    case "--launcher-control": launcher = true; break;
                    default: throw new ArgumentException("Unknown or incomplete option: " + args[i]);
                }
            var config = ServerConfig.Load(configPath);
            if (gamePort is { } selectedPort) { config = config with { GamePort = selectedPort }; config.Validate(); }
            if (transport is not null) { config = config with { TransportMode = transport }; config.Validate(); }
            if (check) { Console.WriteLine(JsonSerializer.Serialize(ServerPreflight.Check(config), ServerConfig.Json)); return 0; }
            if (launcher && !Console.IsInputRedirected) throw new ArgumentException("--launcher-control requires redirected stdin.");
            using var stop = new CancellationTokenSource();
            ConsoleCancelEventHandler interrupt = (_, e) => { e.Cancel = true; stop.Cancel(); };
            Console.CancelKeyPress += interrupt;
            try
            {
                // Auto-detection keeps the existing native launcher's stdin contract compatible.
                // Console's synchronized reader may block before yielding an async task.
                if (Console.IsInputRedirected) _ = Task.Run(() => WatchInput(stop));
                await using var server = new ServerRuntime(config);
                await server.StartAsync(stop.Token);
                while (!stop.IsCancellationRequested)
                {
                    await Task.Delay(500, stop.Token);
                    if (!server.Ready) throw new IOException("A server listener stopped unexpectedly.");
                }
                return 0;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
            finally { await stop.CancelAsync(); Console.CancelKeyPress -= interrupt; }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { error = ex.GetType().Name, message = ex.Message }));
            return ex is ArgumentException or JsonException or InvalidDataException ? 2 : 1;
        }
    }
    private static async Task WatchInput(CancellationTokenSource stop)
    {
        try
        {
            while (true)
            {
                string? line = await Console.In.ReadLineAsync(stop.Token);
                if (line is null || line.Trim().Equals("stop", StringComparison.OrdinalIgnoreCase)) { await stop.CancelAsync(); return; }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { try { await stop.CancelAsync(); } catch (ObjectDisposedException) { } }
    }
}
