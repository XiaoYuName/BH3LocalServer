using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BH3.Server.Configuration;

public sealed record ServerConfig
{
    public string ClientVersion { get; init; } = "9.1.0";
    public string BindAddress { get; init; } = "127.0.0.1";
    public int GamePort { get; init; } = 21000;
    public int HealthPort { get; init; } = 21080;
    public int MaxSessions { get; init; } = 256;
    public int SessionIdleSeconds { get; init; } = 30;
    public string DatabasePath { get; init; } = "../data/bh3.db";
    public string LogDirectory { get; init; } = "../logs";
    public string TransportMode { get; init; } = "kcp";
    [JsonIgnore] public string BaseDirectory { get; init; } = AppContext.BaseDirectory;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public string ResolveDatabase() => Path.GetFullPath(DatabasePath, BaseDirectory);
    public string ResolveLogs() => Path.GetFullPath(LogDirectory, BaseDirectory);
    public static ServerConfig Load(string path)
    {
        string full = Path.GetFullPath(path);
        var config = JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(full), Json) ?? throw new InvalidDataException("Empty configuration.");
        config = config with { BaseDirectory = Path.GetDirectoryName(full)! }; config.Validate(); return config;
    }
    public void Validate(bool allowEphemeralPorts = false)
    {
        if (ClientVersion != "9.1.0") throw new ArgumentException("Only client profile 9.1.0 is defined.");
        if (!IPAddress.TryParse(BindAddress, out var address) || !IPAddress.IsLoopback(address))
            throw new ArgumentException("The skeleton requires a loopback bindAddress.");
        foreach (int port in new[] { GamePort, HealthPort })
            if (!(allowEphemeralPorts && port == 0) && port is < 1024 or > 65535) throw new ArgumentException("Ports must be 1024–65535.");
        if (MaxSessions is < 1 or > 10000 || SessionIdleSeconds is < 1 or > 3600) throw new ArgumentException("Invalid session limits.");
        if (TransportMode is not ("handshake-only" or "kcp")) throw new ArgumentException("Supported transportMode: kcp or handshake-only.");
        if (string.IsNullOrWhiteSpace(DatabasePath) || string.IsNullOrWhiteSpace(LogDirectory)) throw new ArgumentException("Data and log paths are required.");
        if (ResolveDatabase() == ResolveLogs()) throw new ArgumentException("Database and log directory must differ.");
    }
}
