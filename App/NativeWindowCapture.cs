using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace KClevoPanel.App;
// Smoke-only capture, restricted to this process's visible window bounds.
internal static class NativeWindowCapture
{
    internal static async Task SaveVisibleWindowAsync(IntPtr window, string directory, string name, bool checkWhiteEdge = true)
    {
        GetWindowThreadProcessId(window, out uint owner);
        if (owner != Environment.ProcessId || !IsWindowVisible(window))
            throw new InvalidOperationException("只能验证本程序的可见窗口");
        if (DwmGetWindowAttribute(window, 9, out var rect, (uint)Marshal.SizeOf<NativeRect>()) != 0)
            throw new InvalidOperationException("无法获得窗口边缘范围");
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0 || width > 6000 || height > 6000) throw new InvalidOperationException("无效窗口范围");
        var header = new BitmapHeader { Size = 40, Width = width, Height = -height, Planes = 1, Bits = 32 };
        var screen = GetDC(IntPtr.Zero); var dc = CreateCompatibleDC(screen);
        var bitmap = CreateDIBSection(screen, ref header, 0, out var pixels, IntPtr.Zero, 0);
        var old = SelectObject(dc, bitmap);
        byte[] bytes = new byte[checked(width * height * 4)];
        try
        {
            if (bitmap == IntPtr.Zero || !BitBlt(dc, 0, 0, width, height, screen, rect.Left, rect.Top, 0x40CC0020))
                throw new InvalidOperationException("窗口边缘截图失败");
            Marshal.Copy(pixels, bytes, 0, bytes.Length);
            foreach (var point in checkWhiteEdge ? new[] { (width / 2, 0), (0, height / 2), (width - 1, height / 2) } : Array.Empty<(int, int)>())
            {
                int index = (point.Item2 * width + point.Item1) * 4;
                if (bytes[index] > 225 && bytes[index + 1] > 225 && bytes[index + 2] > 225)
                    throw new InvalidOperationException("实际窗口边缘仍有白色轮廓");
            }
        }
        finally { SelectObject(dc, old); DeleteObject(bitmap); DeleteDC(dc); ReleaseDC(IntPtr.Zero, screen); }
        var folder = await StorageFolder.GetFolderFromPathAsync(directory);
        var file = await folder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, bytes);
        await encoder.FlushAsync();
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapHeader
    {
        public uint Size; public int Width, Height; public ushort Planes, Bits; public uint Compression, ImageSize;
        public int X, Y; public uint Used, Important;
    }
    [DllImport("dwmapi.dll", ExactSpelling = true)] private static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attr, out NativeRect value, uint size);
    [DllImport("user32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll", ExactSpelling = true)] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", ExactSpelling = true)] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapHeader info, uint usage, out IntPtr pixels, IntPtr section, uint offset);
    [DllImport("gdi32.dll", ExactSpelling = true)] private static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
    [DllImport("gdi32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr item);
    [DllImport("gdi32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BitBlt(IntPtr dst, int x, int y, int width, int height, IntPtr src, int srcX, int srcY, uint mode);
}
