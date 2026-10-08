using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace BH3.Launcher;

internal sealed record ProxySnapshot(int? Enabled, string? Server, string? Bypass, string? AutoConfig);
internal interface IProxyStore { ProxySnapshot Read(); void Write(ProxySnapshot state); }
internal sealed class RegistryProxyStore : IProxyStore
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    public ProxySnapshot Read() { using var key = Registry.CurrentUser.OpenSubKey(Key); return new(key?.GetValue("ProxyEnable") as int?, key?.GetValue("ProxyServer") as string, key?.GetValue("ProxyOverride") as string, key?.GetValue("AutoConfigURL") as string); }
    public void Write(ProxySnapshot s)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        Set(key, "ProxyEnable", s.Enabled, RegistryValueKind.DWord); Set(key, "ProxyServer", s.Server, RegistryValueKind.String);
        Set(key, "ProxyOverride", s.Bypass, RegistryValueKind.String); Set(key, "AutoConfigURL", s.AutoConfig, RegistryValueKind.String);
        InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0); InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
    }
    private static void Set(RegistryKey key, string name, object? value, RegistryValueKind kind) { if (value is null) key.DeleteValue(name, false); else key.SetValue(name, value, kind); }
    [DllImport("wininet.dll", SetLastError = true)] private static extern bool InternetSetOption(IntPtr internet, int option, IntPtr buffer, int size);
}
internal sealed record ProxyBackup(ProxySnapshot Original, ProxySnapshot Owned, WinHttpState? OriginalWinHttp, WinHttpState? OwnedWinHttp);
internal sealed record WinHttpState(int AccessType, string? Server, string? Bypass)
{
    [StructLayout(LayoutKind.Sequential)] private struct Info { public int Access; public IntPtr Proxy; public IntPtr Bypass; }
    [DllImport("winhttp.dll", SetLastError = true)] private static extern bool WinHttpGetDefaultProxyConfiguration(out Info info);
    [DllImport("winhttp.dll", SetLastError = true)] private static extern bool WinHttpSetDefaultProxyConfiguration(ref Info info);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
    internal static WinHttpState Read()
    {
        if (!WinHttpGetDefaultProxyConfiguration(out var info)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try { return new(info.Access, Marshal.PtrToStringUni(info.Proxy), Marshal.PtrToStringUni(info.Bypass)); }
        finally { GlobalFree(info.Proxy); GlobalFree(info.Bypass); }
    }
    internal void Write()
    {
        var info = new Info { Access = AccessType, Proxy = Server is null ? IntPtr.Zero : Marshal.StringToHGlobalUni(Server), Bypass = Bypass is null ? IntPtr.Zero : Marshal.StringToHGlobalUni(Bypass) };
        try { if (!WinHttpSetDefaultProxyConfiguration(ref info)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "WinHTTP 设置失败。此选项需要管理员权限。"); }
        finally { Marshal.FreeHGlobal(info.Proxy); Marshal.FreeHGlobal(info.Bypass); }
    }
}

internal sealed class ProxyLease(IProxyStore store, string backupPath, Action<string> log)
{
    internal bool Active { get; private set; }
    internal bool Pending => File.Exists(backupPath);
    internal void Enable(int port, bool winHttp = false)
    {
        if (Active) return;
        if (Pending) Restore();
        var original = store.Read(); var owned = new ProxySnapshot(1, $"127.0.0.1:{port}", "localhost;127.0.0.1;<local>", null);
        var backup = new ProxyBackup(original, owned, winHttp ? WinHttpState.Read() : null, winHttp ? new(3, owned.Server, owned.Bypass) : null);
        AppPaths.AtomicWrite(backupPath, JsonSerializer.Serialize(backup, LauncherConfig.Json));
        try { store.Write(owned); backup.OwnedWinHttp?.Write(); Active = true; log("已接管系统代理，退出时恢复原配置。"); }
        catch
        {
            store.Write(original);
            // The WinHTTP call can fail without changing anything; preserve recovery evidence on restoration failure.
            if (backup.OriginalWinHttp is { } w && WinHttpState.Read() != w) w.Write();
            File.Delete(backupPath); throw;
        }
    }
    internal void Restore()
    {
        if (!Pending) { Active = false; return; }
        var backup = JsonSerializer.Deserialize<ProxyBackup>(File.ReadAllText(backupPath)) ?? throw new InvalidDataException("代理恢复文件为空。");
        if (store.Read() == backup.Owned) { store.Write(backup.Original); log("原系统代理已恢复。"); }
        else log("系统代理已由其他程序改变，保留当前配置。");
        if (backup.OwnedWinHttp is { } owned && WinHttpState.Read() == owned) backup.OriginalWinHttp!.Write();
        File.Delete(backupPath); Active = false;
    }
}
