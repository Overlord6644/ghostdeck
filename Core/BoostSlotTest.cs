using System.Text;
using System.Text.Json;

namespace GhostDeck;

/// <summary>
/// (#354 test build, GS66 Stealth 10UE / 10UH only) Does MSI_System[14] switch Cooler Boost?
/// In the owner's two diagnostic packages (Cooler Boost off / on in Dragon Center) that slot was
/// the only value outside the tachometers that changed: 143 (0x8F) off, 73 (0x49) on. A read
/// cannot tell a switch from a reading that follows the fans, so this check writes it once:
///   1. READ     - the slot and both tachometers for 15 s. A slot already at 0x49 (Cooler Boost
///                 probably on) ends the check with nothing written.
///   2. WRITE    - 0x49 to the slot, then 30 s of readings: the slot itself (does the controller
///                 keep the value or overwrite it?) and both fans.
///   3. RESTORE  - the original value back, verified by read-back and retried twice, then 20 s
///                 of readings.
/// The original value is saved to a file before the write, so a check that dies half-way is
/// undone at the next start (<see cref="RecoverPending"/>); a restart of the laptop resets the
/// controller as well. Writes go through Ec.BackupWrite with exactly the two values on the list.
/// </summary>
public static class BoostSlotTest
{
    public const string Prefix = "16V3EMS1";
    public static readonly BlockRef Slot = new("MSI_System", 14);
    public const byte BoostValue = 0x49;
    private const int RiseRpm = 1500;   // Cooler Boost took the fans from ~3000 to ~8000 rpm

    public enum Verdict { FansUp, NoReaction, AlreadyOn, RestoreUnconfirmed, Cancelled, Error }

    public sealed record Sample(double Sec, int Slot, int CpuRpm, int GpuRpm, int CpuTemp);

    public sealed record Result(Verdict Verdict, string Detail, int Original, string AppVersion, string Firmware,
                                DateTime Started, Sample[] Before, Sample[] During, Sample[] After, string[] Log);

    /// <summary>The check is offered only on this one board, on the backup path, once its path test has passed.</summary>
    public static bool AvailableFor(DeviceProfile? dev, string firmware) =>
        dev != null && Ec.OnBackupPath && Ec.BackupWritesOpen && Ec.BackupSpec != null
        && firmware.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    public static Task<Result> RunAsync(DeviceProfile dev, string appVersion, string firmware,
                                        IProgress<string> pr, CancellationToken ct) =>
        Task.Run(() => Run(dev, appVersion, firmware, pr, ct));

    private static Result Run(DeviceProfile dev, string appVersion, string firmware, IProgress<string> pr, CancellationToken ct)
    {
        var started = DateTime.Now;
        var log = new List<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        void Log(string s) => log.Add($"[{sw.Elapsed.TotalSeconds,6:0.0} s] {s}");
        var before = new List<Sample>(); var during = new List<Sample>(); var after = new List<Sample>();
        int original = -1;
        bool written = false;

        Result End(Verdict v, string detail) =>
            new(v, detail, original, appVersion, firmware, started, before.ToArray(), during.ToArray(), after.ToArray(), log.ToArray());

        try
        {
            original = EcBlocks.ReadFresh(Slot);
            Log($"read {Slot} = 0x{original:X2} ({original})");
            if (original == BoostValue)
                return End(Verdict.AlreadyOn, $"{Slot} already reads 0x{BoostValue:X2}, the value it had with Cooler Boost on; nothing written");

            Watch(dev, "before", 15, before, sw, pr, ct);

            try { File.WriteAllText(RestorePath, JsonSerializer.Serialize(new Saved(firmware, original))); } catch { }
            Ec.BackupAllow(Slot, BoostValue, (byte)original);
            Ec.BackupWrite(Slot, BoostValue);
            written = true;
            Log($"write {Slot} = 0x{BoostValue:X2} -> read 0x{EcBlocks.ReadFresh(Slot):X2}");

            Watch(dev, "written", 30, during, sw, pr, ct);
        }
        catch (OperationCanceledException) { Log("cancelled"); }
        catch (Exception ex) { Log("error: " + ex.Message); if (!written) return End(Verdict.Error, ex.Message); }
        if (!written && ct.IsCancellationRequested) return End(Verdict.Cancelled, "cancelled by the owner; nothing written");

        bool restored = !written || Restore((byte)original, Log);
        if (restored) { try { File.Delete(RestorePath); } catch { } }
        if (!restored) return End(Verdict.RestoreUnconfirmed, $"{Slot} did not read 0x{original:X2} back after three tries");

        if (!ct.IsCancellationRequested)
            try { Watch(dev, "restored", 20, after, sw, pr, ct); } catch (Exception ex) { Log("error: " + ex.Message); }

        if (ct.IsCancellationRequested) return End(Verdict.Cancelled, "cancelled by the owner");
        if (before.Count == 0 || during.Count == 0) return End(Verdict.Error, "no readings");
        int baseCpu = (int)before.TakeLast(3).Average(s => s.CpuRpm), baseGpu = (int)before.TakeLast(3).Average(s => s.GpuRpm);
        int topCpu = during.Max(s => s.CpuRpm), topGpu = during.Max(s => s.GpuRpm);
        string note = $"CPU fan {baseCpu} -> {topCpu} RPM, GPU fan {baseGpu} -> {topGpu} RPM (before -> highest while written)";
        return End(topCpu - baseCpu >= RiseRpm || topGpu - baseGpu >= RiseRpm ? Verdict.FansUp : Verdict.NoReaction, note);
    }

    private static void Watch(DeviceProfile dev, string name, int seconds, List<Sample> into,
                              System.Diagnostics.Stopwatch sw, IProgress<string> pr, CancellationToken ct)
    {
        for (int s = 1; s <= seconds; s++)
        {
            if (ct.WaitHandle.WaitOne(1000)) ct.ThrowIfCancellationRequested();
            var x = Take(dev, sw.Elapsed.TotalSeconds);
            into.Add(x);
            pr.Report($"{name}  ·  {x.CpuRpm} / {x.GpuRpm} RPM  ·  {x.CpuTemp} °C");
        }
    }

    private static Sample Take(DeviceProfile dev, double sec)
    {
        int Rpm(BlockRef? r)
        {
            if (r == null) return 0;
            try { int raw = EcBlocks.ReadPair(r); return raw > 0 ? dev.RpmConst / raw : 0; } catch { return 0; }
        }
        var spec = Ec.BackupSpec!;
        int slot = -1, temp = 0;
        try { slot = EcBlocks.ReadFresh(Slot); } catch { }
        try { temp = EcBlocks.Read(spec.CpuTemp); } catch { }
        return new Sample(sec, slot, Rpm(spec.CpuRpm), Rpm(spec.GpuRpm), temp);
    }

    private static bool Restore(byte original, Action<string> log)
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                Ec.BackupWrite(Slot, original);
                int back = EcBlocks.ReadFresh(Slot);
                if (back == original) { log($"restore {Slot} = 0x{original:X2}: confirmed"); return true; }
                log($"restore {Slot} = 0x{original:X2}: read 0x{back:X2}");
            }
            catch (Exception ex) { log("restore error: " + ex.Message); }
            Thread.Sleep(500);
        }
        return false;
    }

    // ---------------- interrupted check ----------------

    private sealed record Saved(string Firmware, int Value);
    private static string RestorePath => Path.Combine(AppSettings.Dir, "boost-slot-restore.json");

    /// <summary>Undo a check that never reached its own restore. Returns a change-log line, or null.</summary>
    public static string? RecoverPending(string firmware)
    {
        try
        {
            if (!File.Exists(RestorePath)) return null;
            var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(RestorePath));
            if (saved == null || saved.Value is < 0 or > 255 || !Ec.OnBackupPath
                || !string.Equals(saved.Firmware, firmware, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(RestorePath);
                return null;
            }
            bool gateWas = Ec.BackupWritesOpen;
            Ec.OpenBackupWrites(true);
            bool ok;
            try { Ec.BackupAllow(Slot, (byte)saved.Value); ok = Restore((byte)saved.Value, _ => { }); }
            finally { Ec.OpenBackupWrites(gateWas); }
            if (ok) File.Delete(RestorePath);
            return ok ? "Fan Boost check: the value from before an interrupted check was put back"
                      : "Fan Boost check: the value from before an interrupted check could NOT be confirmed";
        }
        catch (Exception ex)
        {
            AppLifecycle.Report(ex, "boost-slot-recover");
            return null;
        }
    }

    // ---------------- report ----------------

    public static string BuildReport(Result r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== GhostDeck - Fan Boost check (test build for issue #354) ===");
        sb.AppendLine($"Started: {r.Started:yyyy-MM-dd HH:mm}   App version: {r.AppVersion}   Firmware: {r.Firmware}");
        sb.AppendLine($"Slot: {Slot}   value found: {(r.Original >= 0 ? $"0x{r.Original:X2} ({r.Original})" : "not read")}   value written: 0x{BoostValue:X2} ({BoostValue})");
        sb.AppendLine();
        sb.AppendLine("VERDICT: " + r.Verdict switch
        {
            Verdict.FansUp => "the fans sped up while the value was written",
            Verdict.NoReaction => "no fan reaction above " + RiseRpm + " RPM while the value was written",
            Verdict.AlreadyOn => "not run - the slot already held the Cooler Boost value",
            Verdict.RestoreUnconfirmed => "the original value could NOT be confirmed afterwards - restart the laptop",
            Verdict.Cancelled => "cancelled",
            _ => "error",
        });
        if (r.Detail.Length > 0) sb.AppendLine(r.Detail);
        void Phase(string title, Sample[] s)
        {
            sb.AppendLine();
            sb.AppendLine($"--- {title} ---");
            if (s.Length == 0) { sb.AppendLine("(no readings)"); return; }
            sb.AppendLine("  s:     " + string.Join(' ', s.Select(x => $"{x.Sec,5:0}")));
            sb.AppendLine("  slot:  " + string.Join(' ', s.Select(x => $"{x.Slot,5}")));
            sb.AppendLine("  CPU:   " + string.Join(' ', s.Select(x => $"{x.CpuRpm,5}")));
            sb.AppendLine("  GPU:   " + string.Join(' ', s.Select(x => $"{x.GpuRpm,5}")));
            sb.AppendLine("  °C:    " + string.Join(' ', s.Select(x => $"{x.CpuTemp,5}")));
        }
        Phase("Before the write (15 s)", r.Before);
        Phase($"Slot written to 0x{BoostValue:X2} (30 s)", r.During);
        Phase("After the restore (20 s)", r.After);
        sb.AppendLine();
        sb.AppendLine("--- Log ---");
        foreach (var l in r.Log) sb.AppendLine(l);
        return sb.ToString();
    }

    /// <summary>Write the report to the desktop (documents when there is none). Returns the path.</summary>
    public static string? SaveReport(Result r)
    {
        try
        {
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) dir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string path = Path.Combine(dir, $"ghostdeck-fan-boost-check-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, BuildReport(r));
            return path;
        }
        catch { return null; }
    }
}
