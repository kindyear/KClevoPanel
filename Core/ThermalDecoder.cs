namespace KClevoPanel.Core;

public static class ThermalDecoder
{
    public static ThermalReading Decode(byte[] b12, byte[] b13, int rc12, int rc13, double duration)
    {
        if (rc12 != 12 || rc13 != 13 || b12.Length != 256 || b13.Length != 256)
            throw new InvalidDataException("固件返回值或数据长度不符合只读协议");
        if (b12.All(b => b == 0) || b12.All(b => b == 255) || b13.All(b => b == 0))
            throw new InvalidDataException("固件没有返回有效数据");
        int count = b13[12];
        if (count != 2 || b12[18] is < 1 or > 125)
            throw new InvalidDataException("风扇数量或温度字段不符合本机布局");
        var names = new[] { "CPU", "GPU", "GPU 2" };
        var fans = new List<FanReading>();
        for (int i = 0; i < count; i++)
        {
            int raw = b12[2 + i * 2] * 256 + b12[3 + i * 2];
            int? rpm = FanRpm(raw);
            fans.Add(new(names[i], rpm, b12[16 + i * 3] * 100.0 / 255, raw));
        }
        return new(DateTimeOffset.Now, b12[18], b12[21] is > 0 and <= 125 ? b12[21] : null,
            fans.ToArray(), "只读通道已连接", rc12, rc13, b12, b13, true, duration);
    }
    public static int? FanRpm(int raw)
    {
        if (raw is < 0 or >= 65535) return null;
        if (raw == 0) return 0;
        int rpm = (int)Math.Round(60.0 / (raw * 5.565217391304348e-5) * 2.0);
        return rpm <= 10000 ? rpm : null;
    }
}
