using System.Net;
using System.Text;

namespace BH3.Launcher;

internal sealed record WireRequest(string Method, string Target, Dictionary<string, string> Headers, byte[] Body)
{
    internal string Path => Uri.TryCreate(Target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.PathAndQuery : Target;
}
internal static class HttpWire
{
    internal const int MaxBody = 8 * 1024 * 1024;
    private static async Task<string?> Line(Stream stream, CancellationToken token)
    {
        using var bytes = new MemoryStream(); byte[] one = new byte[1];
        while (await stream.ReadAsync(one, token) != 0)
        {
            if (one[0] == 10) return Encoding.Latin1.GetString(bytes.ToArray()).TrimEnd('\r');
            bytes.WriteByte(one[0]); if (bytes.Length > 16384) throw new InvalidDataException("HTTP 行过长。");
        }
        return bytes.Length == 0 ? null : throw new EndOfStreamException("HTTP 头未完整接收。");
    }
    internal static async Task<WireRequest?> Read(Stream stream, CancellationToken token)
    {
        string? first = await Line(stream, token); if (first is null) return null;
        var parts = first.Split(' ', 3); if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.")) throw new InvalidDataException("HTTP 请求格式错误。");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); int total = first.Length;
        while (true)
        {
            var line = await Line(stream, token) ?? throw new EndOfStreamException(); if (line.Length == 0) break;
            total += line.Length; if (total > 65536) throw new InvalidDataException("HTTP 头过长。");
            int colon = line.IndexOf(':'); if (colon <= 0) throw new InvalidDataException("无效 HTTP 头。");
            var name = line[..colon]; if (headers.ContainsKey(name) && name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("重复 Content-Length。");
            headers[name] = line[(colon + 1)..].Trim();
        }
        if (headers.TryGetValue("Expect", out var expect) && expect.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
        { await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), token); await stream.FlushAsync(token); }
        byte[] body;
        if (headers.TryGetValue("Transfer-Encoding", out var transfer))
        {
            if (!transfer.Equals("chunked", StringComparison.OrdinalIgnoreCase) || headers.ContainsKey("Content-Length")) throw new InvalidDataException("不支持的 HTTP 传输编码。");
            using var ms = new MemoryStream();
            while (true)
            {
                var line = await Line(stream, token) ?? throw new EndOfStreamException();
                if (!int.TryParse(line.Split(';')[0], System.Globalization.NumberStyles.HexNumber, null, out int size) || size < 0 || size + ms.Length > MaxBody) throw new InvalidDataException("HTTP 请求体过大。");
                if (size == 0) { while (!string.IsNullOrEmpty(await Line(stream, token))) { if ((total += 256) > 65536) throw new InvalidDataException(); } break; }
                var chunk = new byte[size]; await stream.ReadExactlyAsync(chunk, token); ms.Write(chunk);
                if (await Line(stream, token) != "") throw new InvalidDataException("无效 chunk 结尾。");
            }
            body = ms.ToArray();
        }
        else
        {
            int size = 0;
            if (headers.TryGetValue("Content-Length", out var raw) && (!int.TryParse(raw, out size) || size < 0 || size > MaxBody)) throw new InvalidDataException("无效 Content-Length。");
            body = new byte[size]; await stream.ReadExactlyAsync(body, token);
        }
        return new(parts[0].ToUpperInvariant(), parts[1], headers, body);
    }
    internal static async Task Reply(Stream stream, int status, byte[] body, string type, CancellationToken token, bool head = false)
    {
        var header = $"HTTP/1.1 {status} {(HttpStatusCode)status}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), token); if (!head) await stream.WriteAsync(body, token); await stream.FlushAsync(token);
    }
}
