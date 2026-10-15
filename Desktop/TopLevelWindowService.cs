using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using DeepSeekFishPet.Behavior;
using Forms = System.Windows.Forms;

namespace DeepSeekFishPet.Desktop;

/// <summary>
/// A restored application window. ScreenBounds and OccluderBounds are optional
/// so read-only gameplay simulations can still construct one with three args.
/// </summary>
public readonly record struct PeekWindow(
    IntPtr Handle, Rect Bounds, string Title,
    Rect ScreenBounds = default,
    IReadOnlyList<Rect>? OccluderBounds = null);

/// <summary>Reads visible window bounds without controlling any other application.</summary>
public static class TopLevelWindowService
{
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int DWMWA_CLOAKED = 14;
    private const uint GW_OWNER = 4;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOPMOST = 0x00000008;

    /// <summary>
    /// Snapshot windows in screen pixels, front to back. Restored ordinary
    /// windows offer top-edge platforms; maximized windows and popups still
    /// occlude icons and platforms beneath them.
    /// </summary>
    public static IReadOnlyList<WindowPlatform> GetPlatforms(IntPtr ownWindow)
    {
        var platforms = new List<WindowPlatform>();
        var upperWindows = new List<Rect>();
        uint ownProcessId = (uint)Environment.ProcessId;
        EnumWindows((window, _) =>
        {
            if (window == ownWindow || !IsWindowVisible(window) || IsIconic(window))
                return true;
            GetWindowThreadProcessId(window, out uint processId);
            if (processId == ownProcessId)
                return true;
            if (DwmGetWindowAttribute(window, DWMWA_CLOAKED, out int cloaked,
                    sizeof(int)) == 0 && cloaked != 0)
                return true;

            var className = new StringBuilder(128);
            GetClassNameW(window, className, className.Capacity);
            string windowClass = className.ToString();
            if (windowClass is "Progman" or "WorkerW")
                return true;
            if (DwmGetWindowAttribute(window, DWMWA_EXTENDED_FRAME_BOUNDS,
                    out RECT frame, Marshal.SizeOf<RECT>()) != 0 &&
                !GetWindowRect(window, out frame))
                return true;
            int width = frame.Right - frame.Left;
            int height = frame.Bottom - frame.Top;
            if (width <= 0 || height <= 0)
                return true;
            Rect bounds = new(frame.Left, frame.Top, width, height);
            if (windowClass is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            {
                upperWindows.Add(bounds);
                return true;
            }

            Forms.Screen screen = Forms.Screen.FromHandle(window);
            var screenBounds = screen.Bounds;
            bool coversMonitor = frame.Left <= screenBounds.Left + 4 &&
                frame.Top <= screenBounds.Top + 4 &&
                frame.Right >= screenBounds.Right - 4 &&
                frame.Bottom >= screenBounds.Bottom - 4;
            bool canStand = width >= 120 && height >= 90 &&
                !IsZoomed(window) && !coversMonitor &&
                GetWindow(window, GW_OWNER) == IntPtr.Zero &&
                (GetWindowLongW(window, GWL_EXSTYLE) & WS_EX_TOPMOST) == 0 &&
                bounds.Top >= screenBounds.Top + PetBrain.PetHeight;
            Rect[] occluders = upperWindows.FindAll(rect =>
                rect.IntersectsWith(bounds)).ToArray();
            platforms.Add(new WindowPlatform(window, bounds, canStand, occluders));
            upperWindows.Add(bounds);
            return true;
        }, IntPtr.Zero);
        return platforms;
    }

    public static IReadOnlyList<PeekWindow> GetCandidates(IntPtr ownWindow)
    {
        var windows = new List<PeekWindow>();
        // EnumWindows traverses top to bottom. Each accepted rectangle below
        // can therefore be checked against windows already in front of it.
        var upperWindows = new List<Rect>();
        uint ownProcessId = (uint)Environment.ProcessId;
        EnumWindows((window, _) =>
        {
            if (window == ownWindow || !IsWindowVisible(window) || IsIconic(window))
                return true;
            GetWindowThreadProcessId(window, out uint processId);
            if (processId == ownProcessId)
                return true;
            if (DwmGetWindowAttribute(window, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true;
            var className = new StringBuilder(128);
            GetClassNameW(window, className, className.Capacity);
            string windowClass = className.ToString();
            if (windowClass is "Progman" or "WorkerW")
                return true;
            if (DwmGetWindowAttribute(window, DWMWA_EXTENDED_FRAME_BOUNDS,
                    out RECT frame, Marshal.SizeOf<RECT>()) != 0 && !GetWindowRect(window, out frame))
                return true;
            int width = frame.Right - frame.Left;
            int height = frame.Bottom - frame.Top;
            if (width <= 0 || height <= 0)
                return true;
            Rect bounds = new(frame.Left, frame.Top, width, height);

            bool mayBeHost = width >= 280 && height >= 160 &&
                !IsZoomed(window) && GetWindow(window, GW_OWNER) == IntPtr.Zero &&
                windowClass is not "Shell_TrayWnd" and not "Shell_SecondaryTrayWnd";
            if (mayBeHost)
            {
                var title = new StringBuilder(256);
                GetWindowTextW(window, title, title.Capacity);
                if (title.Length > 0)
                {
                    Forms.Screen screen = Forms.Screen.FromHandle(window);
                    var screenBounds = screen.Bounds;
                    Rect monitor = new(screenBounds.Left, screenBounds.Top,
                        screenBounds.Width, screenBounds.Height);
                    if (bounds.IntersectsWith(monitor) &&
                        !(frame.Left <= monitor.Left + 4 && frame.Top <= monitor.Top + 4 &&
                          frame.Right >= monitor.Right - 4 && frame.Bottom >= monitor.Bottom - 4))
                    {
                        Rect[] covering = upperWindows.FindAll(rect => rect.IntersectsWith(bounds)).ToArray();
                        PeekWindow candidate = new(window, bounds, title.ToString(),
                            monitor, covering);
                        if (WindowWhackGame.GetViableSides(candidate).Count > 0)
                            windows.Add(candidate);
                    }
                }
            }
            // Untitled overlays, owned popups, taskbars and maximized windows
            // still cover lower windows, even though none can host a peek.
            upperWindows.Add(bounds);
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr window);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", ExactSpelling = true)]
    private static extern int GetWindowLongW(IntPtr window, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW", ExactSpelling = true)]
    private static extern int GetClassNameW(IntPtr window, StringBuilder className, int capacity);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW", ExactSpelling = true)]
    private static extern int GetWindowTextW(IntPtr window, StringBuilder text, int capacity);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out RECT rectangle);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute,
        out RECT value, int valueSize);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute,
        out int value, int valueSize);
}
