using System.Runtime.InteropServices;

namespace KClevoPanel.App;
internal static class WindowChrome
{
    private static readonly Dictionary<IntPtr, int> Results = [];
    private static readonly HashSet<IntPtr> PopupHooks = [];
    private static readonly SubclassProc PopupPolicy = PopupWindowProc;
    internal static bool RemoveSystemBorder(IntPtr window)
    {
        uint noBorder = 0xFFFFFFFE; // DWMWA_COLOR_NONE
        int dark = 1, rounded = 2;
        DwmSetWindowAttribute(window, 20, ref dark, 4);
        DwmSetWindowAttribute(window, 33, ref rounded, 4);
        int result = DwmSetWindowAttribute(window, 34, ref noBorder, 4);
        Results[window] = result;
        return result == 0;
    }
    // BORDER_COLOR is a setter-only attribute; DwmGetWindowAttribute cannot read it back.
    internal static bool BorderRequestSucceeded(IntPtr window) => Results.TryGetValue(window, out int result) && result == 0;
    internal static void ConfigurePopup(IntPtr window)
    {
        if (!PopupHooks.Contains(window) && SetWindowSubclass(window, PopupPolicy, new UIntPtr(93), UIntPtr.Zero)) PopupHooks.Add(window);
        // OverlappedPresenter can retain a classic dialog frame even without a title bar.
        long style = GetWindowLongPtrW(window, -16).ToInt64();
        long extended = GetWindowLongPtrW(window, -20).ToInt64();
        SetWindowLongPtrW(window, -16, new IntPtr((style & ~0x00CF0000L) | 0x80000000L));
        SetWindowLongPtrW(window, -20, new IntPtr(extended & ~0x00020301L));
        SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0, 0x37); // refresh frame, keep placement and focus
        RemoveSystemBorder(window);
    }
    internal static bool PopupFrameRemoved(IntPtr window) =>
        (GetWindowLongPtrW(window, -16).ToInt64() & 0x00C40000L) == 0 &&
        (GetWindowLongPtrW(window, -20).ToInt64() & 0x00020301L) == 0;
    internal static string PopupStyleSummary(IntPtr window) =>
        $"style=0x{GetWindowLongPtrW(window,-16).ToInt64():X}, ex=0x{GetWindowLongPtrW(window,-20).ToInt64():X}";
    private static IntPtr PopupWindowProc(IntPtr window, uint message, IntPtr wp, IntPtr lp, UIntPtr id, UIntPtr data)
    {
        var result = DefSubclassProc(window, message, wp, lp);
        // Let WinUI handle the message first, then suppress any frame it reinserts.
        if (message == 0x7C && lp != IntPtr.Zero)
        {
            var styles = Marshal.PtrToStructure<StyleStruct>(lp);
            int index = unchecked((int)wp.ToInt64());
            if (index == -16) styles.New = (styles.New & ~0x00C40000U) | 0x80000000U;
            else if (index == -20) styles.New &= ~0x00020301U;
            Marshal.StructureToPtr(styles, lp, false);
        }
        if (message == 0x82)
        { RemoveWindowSubclass(window, PopupPolicy, new UIntPtr(93)); PopupHooks.Remove(window); Results.Remove(window); }
        return result;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StyleStruct { public uint Old, New; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp, UIntPtr id, UIntPtr data);
    [DllImport("dwmapi.dll", ExactSpelling = true)] private static extern int DwmSetWindowAttribute(IntPtr window, uint attribute, ref uint value, uint size);
    [DllImport("dwmapi.dll", ExactSpelling = true)] private static extern int DwmSetWindowAttribute(IntPtr window, uint attribute, ref int value, uint size);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("comctl32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id);
    [DllImport("comctl32.dll", ExactSpelling = true)] private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wp, IntPtr lp);
}
