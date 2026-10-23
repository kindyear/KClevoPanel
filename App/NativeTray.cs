using System.Runtime.InteropServices;
using Windows.Graphics;

namespace KClevoPanel.App;
internal sealed class NativeTray : IDisposable
{
    internal const uint CallbackMessage = 0x8000 + 73;
    private const uint IconId = 17;
    private readonly IntPtr _window, _icon;
    private readonly SubclassProc _callback;
    private readonly uint _taskbarCreated;
    private NotifyIconData _data;
    private bool _disposed;
    public bool IsAdded { get; private set; }
    public Action? PrimaryClick, ContextClick, OpenMain, TogglePause, Quit, MinimizeRequested, PowerTransition;
    public Func<bool>? IsPaused;

    public NativeTray(IntPtr window)
    {
        _window = window;
        _icon = LoadImageW(IntPtr.Zero, Path.Combine(AppContext.BaseDirectory, "Assets", "Monitor.ico"), 1, 32, 32, 0x10);
        if (_icon == IntPtr.Zero) throw new InvalidOperationException("无法读取托盘图标");
        _callback = WindowProc;
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        if (!SetWindowSubclass(_window, _callback, new UIntPtr(73), UIntPtr.Zero))
        { DestroyIcon(_icon); throw new InvalidOperationException("无法初始化托盘消息"); }
        _data = new NotifyIconData { Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = window, Id = IconId,
            Flags = 1 | 2 | 4 | 0x80, Callback = CallbackMessage, Icon = _icon,
            Tip = "KClevoPanel · 点击查看实时状态", Info = "", InfoTitle = "" };
        Add();
    }
    private void Add()
    {
        IsAdded = Shell_NotifyIconW(0, ref _data);
        if (IsAdded) { _data.Version = 4; Shell_NotifyIconW(4, ref _data); }
    }
    public void UpdateTip(string text)
    {
        if (!IsAdded || _disposed) return;
        _data.Tip = text.Length > 127 ? text[..127] : text;
        Shell_NotifyIconW(1, ref _data);
    }
    public RectInt32 Anchor()
    {
        var id = new NotifyIconIdentifier { Size = (uint)Marshal.SizeOf<NotifyIconIdentifier>(), Window = _window, Id = IconId };
        if (Shell_NotifyIconGetRect(ref id, out var rect) == 0)
            return new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        GetCursorPos(out var point);
        return new(point.X, point.Y, 1, 1);
    }
    public void ShowMenu(IntPtr owner)
    {
        var menu = CreatePopupMenu();
        try
        {
            AppendMenuW(menu, 0, new UIntPtr(1), "打开主窗口");
            AppendMenuW(menu, 0, new UIntPtr(2), IsPaused?.Invoke() == true ? "继续采样" : "暂停采样");
            AppendMenuW(menu, 0x800, UIntPtr.Zero, null);
            AppendMenuW(menu, 0, new UIntPtr(3), "退出 KClevoPanel");
            GetCursorPos(out var point);
            SetForegroundWindow(owner);
            uint command = TrackPopupMenuEx(menu, 0x182, point.X, point.Y, owner, IntPtr.Zero);
            PostMessageW(owner, 0, IntPtr.Zero, IntPtr.Zero);
            if (command == 1) OpenMain?.Invoke();
            else if (command == 2) TogglePause?.Invoke();
            else if (command == 3) Quit?.Invoke();
        }
        finally { DestroyMenu(menu); }
    }
    private IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        if (message == 0x218 && wParam.ToInt64() is 4 or 7 or 18) PowerTransition?.Invoke();
        if (IsAdded && message == 0x112 && (wParam.ToInt64() & 0xfff0) == 0xF020)
        { MinimizeRequested?.Invoke(); return IntPtr.Zero; }
        if (IsAdded && message == 5 && wParam.ToInt64() == 1) MinimizeRequested?.Invoke();
        if (_taskbarCreated != 0 && message == _taskbarCreated) { Add(); return IntPtr.Zero; }
        if (message == CallbackMessage)
        {
            uint code = (uint)(lParam.ToInt64() & 0xffff);
            if (code is 0x400 or 0x401) PrimaryClick?.Invoke(); // NIN_SELECT / NIN_KEYSELECT
            else if (code == 0x7B) ContextClick?.Invoke(); // WM_CONTEXTMENU
            return IntPtr.Zero;
        }
        return DefSubclassProc(window, message, wParam, lParam);
    }
    internal void SimulatePrimaryClickForSmoke() => SendMessageW(_window, CallbackMessage, IntPtr.Zero, new IntPtr((IconId << 16) | 0x400));
    internal void RequestCloseForSmoke() => SendMessageW(_window, 0x10, IntPtr.Zero, IntPtr.Zero);
    internal void RequestMinimizeForSmoke() => SendMessageW(_window, 0x112, new IntPtr(0xF020), IntPtr.Zero);
    internal bool CaptionDragHitForSmoke()
    {
        GetWindowRect(_window, out var rect);
        double scale = GetDpiForWindow(_window) / 96.0;
        int x = rect.Left + (int)(240 * scale), y = rect.Top + (int)(28 * scale);
        long coordinates = (ushort)x | ((long)(ushort)y << 16);
        return SendMessageW(_window, 0x84, IntPtr.Zero, new IntPtr(coordinates)).ToInt64() == 2;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Shell_NotifyIconW(2, ref _data); IsAdded = false;
        RemoveWindowSubclass(_window, _callback, new UIntPtr(73));
        DestroyIcon(_icon);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size; public IntPtr Window; public uint Id, Flags, Callback; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public IntPtr BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NotifyIconIdentifier { public uint Size; public IntPtr Window; public uint Id; public Guid Guid; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp, UIntPtr id, UIntPtr data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Shell_NotifyIconW(uint command, ref NotifyIconData data);
    [DllImport("shell32.dll", ExactSpelling = true)] private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier id, out NativeRect rect);
    [DllImport("comctl32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc callback, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc callback, UIntPtr id);
    [DllImport("comctl32.dll", ExactSpelling = true)] private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern IntPtr LoadImageW(IntPtr instance, string path, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr parameters);
    [DllImport("user32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessageW(IntPtr hwnd, uint message, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern IntPtr SendMessageW(IntPtr hwnd, uint message, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern uint GetDpiForWindow(IntPtr hwnd);
}
