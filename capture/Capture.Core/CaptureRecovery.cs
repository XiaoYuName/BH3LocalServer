namespace Bh3Capture;
public static class CaptureRecovery
{
    public static bool StatusBelongsTo(string output, CaptureSession session) =>
        System.Text.RegularExpressions.Regex.IsMatch(output,
            System.Text.RegularExpressions.Regex.Escape(Path.Combine(Path.GetFullPath(session.DirectoryPath), "capture")) + @"[^\\/\r\n""<>]*\.etl(?=$|[\s""])",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    // Called only after the owning UI process has exited. It does not touch other collectors.
    public static async Task<bool> StopOrphan(CaptureSession session, ICommandRunner runner)
    {
        if (session.State != "Capturing") return false;
        var live = await runner.Run("logman.exe", ["query", "PktMon", "-ets"]);
        if (live.ExitCode != 0) return false;
        var status = await runner.Run("pktmon.exe", ["status"]);
        if (status.ExitCode != 0 || !StatusBelongsTo(status.Output, session)) return false;
        var stopped = await runner.Run("pktmon.exe", ["stop"]);
        try { await File.AppendAllTextAsync(Path.Combine(session.DirectoryPath, "operations.log"), "[watchdog] " + stopped.Output + "\n"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Stop ownership must survive log failure. */ }
        if (stopped.ExitCode != 0) return false;
        session.State = "Stopped";
        session.StopReason = "Interrupted";
        session.EndedUtc = DateTimeOffset.UtcNow;
        session.Save();
        return true;
    }
}
