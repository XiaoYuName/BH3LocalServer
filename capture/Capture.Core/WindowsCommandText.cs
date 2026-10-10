using System.Runtime.InteropServices;
using System.Text;
namespace Bh3Capture;
public static class WindowsCommandText
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    [DllImport("kernel32.dll")] private static extern uint GetOEMCP();
    public static string Decode(byte[] bytes, int? fallbackCodePage = null)
    {
        if (bytes.Length == 0) return "";
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) return StrictUtf8.GetString(bytes, 3, bytes.Length - 3);
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var codePage = fallbackCodePage ?? (OperatingSystem.IsWindows() ? (int)GetOEMCP() : 936);
            return Encoding.GetEncoding(codePage).GetString(bytes);
        }
    }
}
public static class CollectorStatus
{
    // logman exposes the PLA HRESULT directly as a signed process exit code on this Windows version.
    // PLA_E_DCS_NOT_FOUND (Winerror.h) means there is no collector, not that elevation failed.
    public const int NotFound = unchecked((int)0x80300002);
    public static void RequireIdle(CommandResult state)
    {
        if (state.ExitCode == 0) throw new InvalidOperationException("已有 pktmon 采集正在运行。请先结束原采集；本工具不会停止它。");
        if (state.ExitCode == NotFound) return;
        // Some builds return a generic failure code. Accept only an explicit missing-collector message.
        if (state.ExitCode == 1 && (state.Output.Contains("Data Collector Set was not found", StringComparison.OrdinalIgnoreCase) ||
            state.Output.Contains("找不到数据收集器集", StringComparison.Ordinal) || state.Output.Contains("找不到資料收集器集合", StringComparison.Ordinal))) return;
        var reason = state.ExitCode is 5 or unchecked((int)0x80070005)
            ? "访问被拒绝，请以管理员身份运行。"
            : "系统未能查询当前采集状态，未更改任何抓包配置。";
        throw new IOException($"{reason}\n退出码：{state.ExitCode}（0x{unchecked((uint)state.ExitCode):X8}）\n{state.Output}");
    }
}
