namespace KClevoPanel.Core;

public static class FanControlLifetime
{
    public static string? StopReason(DateTimeOffset now, DateTimeOffset started, bool continuous,
        DateTimeOffset? heartbeat, bool appAlive, bool stopRequested, DateTimeOffset lastSample)
    {
        if (stopRequested) return "已请求恢复";
        if (!appAlive) return "应用已退出";
        if (now < started || now < lastSample || now - lastSample > TimeSpan.FromSeconds(10)) return "采样中断、时钟变化或睡眠恢复";
        if (!continuous && now - started >= TimeSpan.FromSeconds(30)) return "30 秒试用结束";
        if (continuous && (!heartbeat.HasValue || now - heartbeat.Value > TimeSpan.FromSeconds(10) || heartbeat.Value > now.AddSeconds(2)))
            return "应用心跳已中断";
        return null;
    }
}
public sealed record FanInspection(FanCurveSnapshot Snapshot, ThermalReading Thermal, DllInfo Library,
    long FirmwareWriteCalls, long MirrorWriteCalls);
