using System.Diagnostics;
using System.Text.Json;
using KClevoPanel.Core;
using KClevoPanel.TelemetryWorker;

try
{
    using var reader = new GuardedDchuReader();
    ThermalReading Read()
    {
        var timer = Stopwatch.StartNew();
        var capability = reader.ReadCapability(); // Runtime curves can change; do not retain a startup table forever.
        var sample = reader.ReadTelemetry();
        return ThermalDecoder.Decode(sample.Bytes, capability.Bytes, sample.Rc, capability.Rc, timer.Elapsed.TotalMilliseconds);
    }
    if (args.Contains("--once")) Console.WriteLine(JsonSerializer.Serialize(Read()));
    else
    {
        string? command;
        while ((command = Console.ReadLine()) is not null)
        {
            if (command == "exit") break;
            if (command != "read") throw new InvalidOperationException("采样请求不在白名单中");
            Console.WriteLine(JsonSerializer.Serialize(Read()));
        }
    }
}
catch (Exception error) { Console.WriteLine(JsonSerializer.Serialize(new { Error = error.Message })); Environment.ExitCode = 1; }
