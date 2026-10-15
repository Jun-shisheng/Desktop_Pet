using System;
using System.Runtime.InteropServices;

namespace DeepSeekFishPet.Desktop;

/// <summary>
/// Places the pet in the ordinary window band near its desktop or application
/// perch. Explorer can keep invisible WorkerW windows above its icon host, so
/// callers must check relative Z order rather than direct HWND adjacency.
/// </summary>
public static class DesktopWindowLayer
{
    private const int GWL_EXSTYLE = -20;
    private const int GW_HWNDNEXT = 2;
    private const int GW_HWNDPREV = 3;
    private const int GW_OWNER = 4;
    private const int GWLP_HWNDPARENT = -8;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_APPWINDOW = 0x00040000;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    /// <summary>
    /// WPF gives a ShowInTaskbar=false window an invisible owner. Owned HWNDs
    /// cannot be put beside Explorer's desktop windows. Remove that private
    /// owner and retain tool-window style so no taskbar button is created.
    /// </summary>
    internal static bool TryMakeIndependent(IntPtr window, bool noActivate = false)
    {
        if (window == IntPtr.Zero || !IsWindow(window))
            return false;

        bool changed = false;
        if (GetWindow(window, GW_OWNER) != IntPtr.Zero)
        {
            SetWindowLongPtrW(window, GWLP_HWNDPARENT, IntPtr.Zero);
            if (GetWindow(window, GW_OWNER) != IntPtr.Zero)
                return false;
            changed = true;
        }

        int style = GetWindowLongW(window, GWL_EXSTYLE);
        int wanted = (style | WS_EX_TOOLWINDOW |
            (noActivate ? WS_EX_NOACTIVATE : 0)) & ~WS_EX_APPWINDOW;
        if (wanted != style)
        {
            SetWindowLongW(window, GWL_EXSTYLE, wanted);
            if (GetWindowLongW(window, GWL_EXSTYLE) != wanted)
                return false;
            changed = true;
        }
        if (changed && !SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER |
                SWP_NOACTIVATE | SWP_FRAMECHANGED))
            return false;
        return GetWindow(window, GW_OWNER) == IntPtr.Zero;
    }

    /// <summary>
    /// Return true only if higher is actually above lower in the live top-level
    /// Z order. Invisible Explorer WorkerW and WPF helper windows may lie
    /// between them; adjacency is not required.
    /// </summary>
    internal static bool IsAbove(IntPtr higher, IntPtr lower)
    {
        if (higher == IntPtr.Zero || lower == IntPtr.Zero || higher == lower ||
            !IsWindow(higher) || !IsWindow(lower))
            return false;
        IntPtr current = higher;
        for (int i = 0; i < 4096; i++)
        {
            current = GetWindow(current, GW_HWNDNEXT);
            if (current == lower) return true;
            if (current == IntPtr.Zero) break;
        }
        return false;
    }

    internal static bool IsTopmost(IntPtr window) =>
        (GetWindowLongW(window, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;

    /// <summary>
    /// Insert a normal window immediately above a target when Windows permits
    /// it. A desktop WorkerW can be a protected bottom-band sibling, in which
    /// case Windows may keep it between the two; the final relative-order
    /// check is what determines success.
    /// </summary>
    internal static bool TryPlaceJustAbove(IntPtr window, IntPtr target)
    {
        if (window == IntPtr.Zero || target == IntPtr.Zero || window == target ||
            !IsWindow(window) || !IsWindow(target) || IsTopmost(window))
            return false;

        // SetWindowPos places the moved HWND *below* hWndInsertAfter. To land
        // above target, use whichever HWND is currently directly above it.
        IntPtr aboveTarget = GetWindow(target, GW_HWNDPREV);
        if (aboveTarget != window)
        {
            IntPtr insertAfter = aboveTarget != IntPtr.Zero &&
                !IsTopmost(aboveTarget) ? aboveTarget : IntPtr.Zero;
            if (!SetWindowPos(window, insertAfter, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE))
                return false;
        }
        return !IsTopmost(window) && IsAbove(window, target);
    }

    public static bool TryPlaceAboveDesktop(IntPtr petWindow)
    {
        if (!OperatingSystem.IsWindows() || !TryMakeIndependent(petWindow) ||
            IsTopmost(petWindow))
            return false;
        IntPtr desktopHost = DesktopIconVisualMask.FindDesktopHost();
        return TryPlaceJustAbove(petWindow, desktopHost);
    }

    public static bool TryPlaceAboveWindow(IntPtr petWindow, IntPtr hostWindow)
    {
        if (!OperatingSystem.IsWindows() || hostWindow == IntPtr.Zero ||
            petWindow == hostWindow || !IsWindow(hostWindow) ||
            !TryMakeIndependent(petWindow) || IsTopmost(petWindow) ||
            IsTopmost(hostWindow))
            return false;

        return TryPlaceJustAbove(petWindow, hostWindow);
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", ExactSpelling = true)]
    private static extern int GetWindowLongW(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", ExactSpelling = true)]
    private static extern int SetWindowLongW(IntPtr window, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", ExactSpelling = true)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr window, int index,
        IntPtr value);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetWindow(IntPtr window, int command);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter,
        int x, int y, int width, int height, uint flags);
}
