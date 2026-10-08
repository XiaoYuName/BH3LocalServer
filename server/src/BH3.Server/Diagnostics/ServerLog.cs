using System.Text.Json;

namespace BH3.Server.Diagnostics;

public sealed class ServerLog : IDisposable
{
    private readonly Lock gate = new();
    private readonly StreamWriter file;
    private readonly TextWriter console;
    public ServerLog(string directory, TextWriter? console = null)
    {
        Directory.CreateDirectory(directory); this.console = console ?? Console.Out;
        string path = Path.Combine(directory, $"server-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}.jsonl");
        file = new(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
    }
    public void Write(string level, string name, object details)
    {
        string line = JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, level, name, details });
        lock (gate) { file.WriteLine(line); console.WriteLine(line); }
    }
    public void Dispose() { lock (gate) file.Dispose(); }
}
