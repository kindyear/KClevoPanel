using System.Text.Json;
using KClevoPanel.Core;

namespace KClevoPanel.PresetWorker;
internal static class JournalTests
{
    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "KClevoPanel-journal-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "fixture.jsonl");
        var before = new PresetSnapshot(DateTimeOffset.Now, 2, 0, 0, 0, 0);
        string Row(string stage, object data) => JsonSerializer.Serialize(new { Stage = stage, Data = data });
        string prepared = Row("Prepared", new { Before = before });
        void Check(string name, string[] rows, bool pending, int? baseline = null)
        {
            File.WriteAllLines(path, rows);
            var found = PendingJournal.Find(directory);
            if ((found is not null) != pending || (baseline.HasValue && found?.Baseline?.Mode != baseline)) throw new Exception(name);
            Console.WriteLine("PASS " + name);
        }
        try
        {
            Check("Empty journal cannot imply a write", [], false);
            Check("Prepared write survives process death", [prepared], true, 2);
            Check("Firmware ACK alone remains recoverable", [prepared, Row("FirmwareAcknowledged", new { Return = 121 })], true, 2);
            Check("Completed transaction is resolved", [prepared, Row("Completed", new { })], false);
            Check("Failed compensation remains blocked", [prepared, Row("Failed", new { RestoreSucceeded = false })], true, 2);
            Check("Acknowledged compensation resolves normal transaction", [prepared, Row("Failed", new { RestoreSucceeded = true })], false);
            Check("Roundtrip keeps its initial baseline until final restore", [Row("RoundtripStarted", before), prepared, Row("Completed", new { })], true, 2);
            Check("Completed roundtrip releases recovery block", [Row("RoundtripStarted", before), prepared, Row("RoundtripCompleted", new { })], false);
            Check("Truncated journal cannot be treated as success", [prepared, "{"], true, 2);
            Check("Explicit verified recovery resolves truncated tail", [prepared, "{", Row("Recovered", new { })], false);
        }
        finally { File.Delete(path); Directory.Delete(directory); }
    }
}
