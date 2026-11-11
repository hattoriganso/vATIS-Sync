using System.Runtime.InteropServices;
using VatisSync.Core;

namespace VatisSync.Services;

/// <summary>A monitor the controller can pick, as the settings box shows it. <see cref="Value"/> is what is saved.</summary>
public sealed record ScreenChoice(string Value, string Label);

/// <summary>The monitors that are connected right now. Only asks Windows for the monitor list; it looks at no other program.</summary>
public static class Screens
{
    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    private const uint PrimaryFlag = 1;

    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    /// <summary>Every connected monitor. Empty if Windows says nothing (the caller then keeps its own default).</summary>
    public static IReadOnlyList<ScreenArea> Connected()
    {
        var found = new List<ScreenArea>();
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
            {
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfo(monitor, ref info))
                    found.Add(new ScreenArea(info.Device, info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom,
                        (info.Flags & PrimaryFlag) != 0));
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            found.Clear();
        }

        return found.OrderBy(s => s.DeviceName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// What the settings box offers: the main screen, then each connected monitor. A saved screen that is not connected right
    /// now stays in the list (so the box does not go blank) and says it is not connected.
    /// </summary>
    public static IReadOnlyList<ScreenChoice> Choices(string saved) => Choices(Connected(), saved);

    internal static IReadOnlyList<ScreenChoice> Choices(IReadOnlyList<ScreenArea> connected, string saved)
    {
        var choices = new List<ScreenChoice> { new("", "Main screen") };
        choices.AddRange(connected.Select((s, i) => new ScreenChoice(s.DeviceName,
            $"Screen {i + 1},{s.Width} × {s.Height}{(s.IsPrimary ? " (main)" : "")}")));
        if (!string.IsNullOrEmpty(saved) && connected.All(s => !string.Equals(s.DeviceName, saved, StringComparison.OrdinalIgnoreCase)))
            choices.Add(new ScreenChoice(saved, "A screen that is not connected (using the main screen)"));
        return choices;
    }
}
