using System.Text.Json;
using KClevoPanel.Core;

namespace KClevoPanel.PresetWorker;

internal sealed record PendingJournal(string Path, PresetSnapshot? Baseline)
{
    public static string DirectoryPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KClevoPanel", "Presets");
    public static PendingJournal? Find(string? testDirectory = null)
    {
        string directory = testDirectory ?? DirectoryPath;
        if (!Directory.Exists(directory)) return null;
        foreach (string path in Directory.EnumerateFiles(directory, "*.jsonl").OrderDescending())
        {
            PresetSnapshot? before = null, roundtrip = null;
            string? last = null;
            bool malformed = false, roundtripFinished = false;
            if (new FileInfo(path).Length > 1024 * 1024) return new(path, null);
            using var readStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(readStream);
            foreach (string line in reader.ReadToEnd().Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var row = JsonDocument.Parse(line);
                    last = row.RootElement.GetProperty("Stage").GetString();
                    var data = row.RootElement.GetProperty("Data");
                    if (last == "Prepared") before = data.GetProperty("Before").Deserialize<PresetSnapshot>();
                    if (last == "RoundtripStarted") roundtrip = data.Deserialize<PresetSnapshot>();
                    if (last is "RoundtripCompleted" or "Recovered") roundtripFinished = true;
                    if (last == "Failed" && data.TryGetProperty("RestoreSucceeded", out var restored) && restored.ValueKind == JsonValueKind.True)
                        last = "Recovered";
                }
                catch { malformed = true; }
            }
            if (roundtrip is not null && !roundtripFinished) return new(path, roundtrip);
            if ((malformed && last != "Recovered") || last is "Prepared" or "FirmwareAcknowledged" or "RecoveryStarted" or "RecoveryFailed" or "Failed")
                return new(path, before);
        }
        return null;
    }
    public void Recover(NativePresets native)
    {
        if (Baseline is null) throw new InvalidDataException("恢复日志不完整，需要人工核对，未执行写入");
        var plan = DirectPresetPlan.Create(Baseline, Baseline.Mode);
        var current = native.Read();
        if (!plan.CanApply || current.Dtt != Baseline.Dtt || current.TurboFan != Baseline.TurboFan
            || current.FanMode != Baseline.FanMode || current.FanOffset != Baseline.FanOffset)
            throw new InvalidOperationException("恢复前置状态不符，未执行写入");
        _ = native.ReadThermals();
        void Record(string stage, object data)
        {
            using var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            writer.WriteLine();
            writer.WriteLine(JsonSerializer.Serialize(new { Time = DateTimeOffset.Now, Stage = stage, Data = data }));
            writer.Flush(); stream.Flush(true);
        }
        Record("RecoveryStarted", new { Baseline });
        try
        {
            // Recompute a fixed allowed payload; never execute bytes or commands from a file.
            if (native.SetFirmwareMode(plan.Payload) != 121 || native.SaveSelection(Baseline.Mode) != 1 || native.Read().Token != Baseline.Token)
                throw new IOException("恢复应答或设置记录未确认");
            Record("Recovered", new { After = native.Read(), HardwareEffectVerified = false });
        }
        catch (Exception error) { Record("RecoveryFailed", new { Error = error.Message }); throw; }
    }
}
