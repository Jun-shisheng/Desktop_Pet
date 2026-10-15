using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeepSeekFishPet.Behavior;

namespace DeepSeekFishPet.Desktop;

/// <summary>
/// Reads the icons actually present in Explorer's desktop view. The snapshot
/// includes files, shortcuts, and virtual Shell items such as Recycle Bin.
/// All coordinates are physical screen pixels, including negative coordinates
/// on monitors to the left or above the primary display.
/// </summary>
public sealed class DesktopIconLayoutService
{
    private const int CSIDL_DESKTOP = 0;
    private const int SWC_DESKTOP = 0x8;
    private const int SWFO_NEEDDISPATCH = 0x1;
    private const uint SVGIO_ALLVIEW = 0x2;
    private const uint SIGDN_NORMALDISPLAY = 0;
    private const uint SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;
    private const uint SSF_HIDEICONS = 0x00004000;
    // SHELLFLAGSTATE is a 32-bit bitfield; fHideIcons is its thirteenth bit.
    // This bit is distinct from the SSF_HIDEICONS *request mask* above.
    private const uint SHELLFLAGSTATE_HIDE_ICONS = 1u << 12;
    private const uint SHGFI_ICON = 0x00000100;
    private const uint SHGFI_ADDOVERLAYS = 0x00000020;
    private const uint SHGFI_OVERLAYINDEX = 0x00000040;
    private const uint SHGFI_PIDL = 0x00000008;
    private const uint SHGFI_SYSICONINDEX = 0x00004000;
    private const int SHIL_EXTRALARGE = 2;
    private const uint LVM_FIRST = 0x1000;
    private const uint LVM_GETITEMCOUNT = LVM_FIRST + 4;
    private const uint LVM_GETITEMRECT = LVM_FIRST + 14;
    private const uint LVM_GETITEMPOSITION = LVM_FIRST + 16;
    private const uint LVM_GETEDITCONTROL = LVM_FIRST + 24;
    private const uint SMTO_BLOCK = 0x0001;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const uint PROCESS_VM_OPERATION = 0x0008;
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint PROCESS_VM_WRITE = 0x0020;
    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_RESERVE = 0x2000;
    private const uint MEM_RELEASE = 0x8000;
    private const uint PAGE_READWRITE = 0x04;
    private const int MaxVisualBoundsPixels = 320;
    private const uint ShellMessageTimeoutMs = 180;

    private static readonly Guid ShellWindowsClsid = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid TopLevelBrowserSid = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid ShellBrowserIid = typeof(IShellBrowser).GUID;
    private static readonly Guid ShellFolderIid = typeof(IShellFolder).GUID;
    private static readonly Guid EnumIdListIid = typeof(IEnumIDList).GUID;
    private static readonly Guid ShellItemIid = typeof(IShellItem).GUID;
    private static readonly Guid ImageListIid = new("46EB5926-582E-4017-9FDF-E8998DAA0950");

    private readonly object _snapshotLock = new();
    private Dictionary<string, byte[]> _absolutePidls = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, ImageSource> _iconImages = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns a frozen copy of an icon from the latest platform snapshot.
    /// The image is suitable for an animated clone; no Shell item is moved.
    /// </summary>
    public ImageSource? GetIcon(string platformId)
    {
        if (string.IsNullOrWhiteSpace(platformId))
        {
            IconSnackDiagnostics.Failure("image.invalid-id");
            return null;
        }

        byte[] absolutePidl;
        lock (_snapshotLock)
        {
            if (_iconImages.TryGetValue(platformId, out ImageSource? cached))
                return cached;
            if (!_absolutePidls.TryGetValue(platformId, out absolutePidl!))
            {
                IconSnackDiagnostics.Failure("image.pidl-not-in-snapshot");
                return null;
            }
        }

        IntPtr pidl = IntPtr.Zero;
        IntPtr icon = IntPtr.Zero;
        try
        {
            pidl = Marshal.AllocCoTaskMem(absolutePidl.Length);
            Marshal.Copy(absolutePidl, 0, pidl, absolutePidl.Length);
            // Explorer's desktop uses its extra-large Shell image list (48px
            // at 96 DPI). A plain SHGFI_ICON is usually only 32px and becomes
            // visibly blurred when the snack animation draws it at 48px.
            icon = TryReadDesktopSizedIcon(pidl);
            if (icon == IntPtr.Zero)
            {
                IntPtr result = SHGetFileInfoW(pidl, 0, out SHFILEINFO info,
                    (uint)Marshal.SizeOf<SHFILEINFO>(),
                    SHGFI_PIDL | SHGFI_ICON | SHGFI_ADDOVERLAYS);
                icon = info.hIcon;
                if (result == IntPtr.Zero || icon == IntPtr.Zero)
                {
                    IconSnackDiagnostics.Failure("image.shell-icon-unavailable");
                    return null;
                }
            }

            BitmapSource image = Imaging.CreateBitmapSourceFromHIcon(
                icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            lock (_snapshotLock)
            {
                // An icon rearrangement may have replaced the cached snapshot.
                if (_absolutePidls.TryGetValue(platformId, out byte[]? current) &&
                    ReferenceEquals(current, absolutePidl))
                    _iconImages[platformId] = image;
            }
            return image;
        }
        catch (Exception ex) when (ex is ExternalException or ArgumentException or
                                   DllNotFoundException or EntryPointNotFoundException)
        {
            IconSnackDiagnostics.Failure("image.exception", errorCode: ex.HResult);
            return null;
        }
        finally
        {
            if (icon != IntPtr.Zero)
                DestroyIcon(icon);
            if (pidl != IntPtr.Zero)
                Marshal.FreeCoTaskMem(pidl);
        }
    }

    private static IntPtr TryReadDesktopSizedIcon(IntPtr pidl)
    {
        IntPtr imageList = IntPtr.Zero;
        IntPtr smallIcon = IntPtr.Zero;
        try
        {
            // The high byte of iIcon is the Shell overlay slot. Pass that slot
            // to ImageList_GetIcon so a shortcut arrow or sync badge travels
            // with the same desktop item image instead of becoming generic art.
            IntPtr result = SHGetFileInfoW(pidl, 0, out SHFILEINFO info,
                (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_PIDL | SHGFI_ICON |
                SHGFI_SYSICONINDEX | SHGFI_OVERLAYINDEX | SHGFI_ADDOVERLAYS);
            smallIcon = info.hIcon;
            if (result == IntPtr.Zero)
                return IntPtr.Zero;

            Guid iid = ImageListIid;
            if (SHGetImageList(SHIL_EXTRALARGE, ref iid, out imageList) != 0 ||
                imageList == IntPtr.Zero)
                return IntPtr.Zero;

            int iconIndex = info.iIcon & 0x00FFFFFF;
            int overlaySlot = (int)((uint)info.iIcon >> 24);
            uint style = (uint)(overlaySlot << 8); // INDEXTOOVERLAYMASK.
            return ImageList_GetIcon(imageList, iconIndex, style);
        }
        catch (Exception ex) when (ex is ExternalException or ArgumentException or
                                   DllNotFoundException or EntryPointNotFoundException)
        {
            return IntPtr.Zero;
        }
        finally
        {
            if (smallIcon != IntPtr.Zero)
                DestroyIcon(smallIcon);
            if (imageList != IntPtr.Zero)
                Marshal.Release(imageList);
        }
    }

    /// <summary>
    /// Reads the current, visible icon-and-label rectangle directly from
    /// Explorer's desktop ListView. The result is in physical screen pixels.
    /// Its view index is checked against the Shell item's stable ID and
    /// position before it can be used to cover an icon. No Shell item changes.
    /// Returns false when Explorer does not expose a conventional ListView,
    /// while an item label is being edited, or when geometry is in flux.
    /// Call from the WPF STA thread immediately before showing a cover.
    /// </summary>
    public bool TryGetVisualBounds(string platformId, out Rect screenBounds)
    {
        screenBounds = Rect.Empty;
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(platformId))
        {
            IconSnackDiagnostics.Failure("visual.invalid-input");
            return false;
        }

        object? shellWindows = null;
        object? dispatch = null;
        IShellBrowser? browser = null;
        IShellView? shellView = null;
        IShellFolder? folder = null;

        try
        {
            SHGetSettings(out uint shellFlags, SSF_HIDEICONS);
            if ((shellFlags & SHELLFLAGSTATE_HIDE_ICONS) != 0)
            {
                IconSnackDiagnostics.Failure("visual.desktop-icons-hidden");
                return false;
            }

            Type? type = Type.GetTypeFromCLSID(ShellWindowsClsid);
            if (type is null || Activator.CreateInstance(type) is not IShellWindows windows)
            {
                IconSnackDiagnostics.Failure("visual.shell-windows-unavailable");
                return false;
            }
            shellWindows = windows;

            object location = CSIDL_DESKTOP;
            object empty = null!;
            if (windows.FindWindowSW(ref location, ref empty, SWC_DESKTOP,
                    out _, SWFO_NEEDDISPATCH, out object desktopDispatch) != 0)
            {
                IconSnackDiagnostics.Failure("visual.desktop-dispatch-unavailable");
                return false;
            }
            dispatch = desktopDispatch;
            if (desktopDispatch is not IServiceProvider serviceProvider)
            {
                IconSnackDiagnostics.Failure("visual.desktop-service-unavailable");
                return false;
            }

            Guid browserIid = ShellBrowserIid;
            Guid browserSid = TopLevelBrowserSid;
            if (serviceProvider.QueryService(ref browserSid, ref browserIid,
                    out browser) != 0 || browser is null ||
                browser.QueryActiveShellView(out shellView) != 0 || shellView is null ||
                shellView.GetWindow(out IntPtr viewHwnd) != 0 ||
                viewHwnd == IntPtr.Zero || !IsWindowVisible(viewHwnd))
            {
                IconSnackDiagnostics.Failure("visual.shell-view-unavailable");
                return false;
            }

            // Other Shell views, or versions that no longer use SysListView32,
            // cannot safely be patched with a guessed rectangle.
            IntPtr listView = FindWindowExW(viewHwnd, IntPtr.Zero, "SysListView32", null);
            if (listView == IntPtr.Zero || !IsWindowVisible(listView) ||
                GetWindowThreadProcessId(viewHwnd, out uint viewProcessId) == 0 ||
                GetWindowThreadProcessId(listView, out uint listProcessId) == 0 ||
                viewProcessId == 0 || viewProcessId != listProcessId ||
                !GetClientRect(listView, out RECT client) ||
                client.Right <= client.Left || client.Bottom <= client.Top)
            {
                IconSnackDiagnostics.Failure("visual.list-view-unavailable", listView);
                return false;
            }

            IFolderView view = (IFolderView)shellView;
            Guid folderIid = ShellFolderIid;
            if (view.GetFolder(ref folderIid, out folder) != 0 || folder is null)
            {
                IconSnackDiagnostics.Failure("visual.folder-unavailable", listView);
                return false;
            }
            if (view.ItemCount(SVGIO_ALLVIEW, out int count) != 0 ||
                count is <= 0 or > 16384)
            {
                IconSnackDiagnostics.Failure("visual.item-count-unavailable", listView);
                return false;
            }
            if (!TrySendListView(listView, LVM_GETITEMCOUNT, IntPtr.Zero,
                    IntPtr.Zero, out IntPtr nativeCount))
            {
                IconSnackDiagnostics.Failure("visual.native-count-query-failed",
                    listView);
                return false;
            }
            if (nativeCount.ToInt64() != count)
            {
                IconSnackDiagnostics.Failure("visual.item-count-mismatch", listView);
                return false;
            }
            if (!TrySendListView(listView, LVM_GETEDITCONTROL, IntPtr.Zero,
                    IntPtr.Zero, out IntPtr editControl))
            {
                IconSnackDiagnostics.Failure("visual.edit-query-failed", listView);
                return false;
            }
            if (editControl != IntPtr.Zero)
            {
                IconSnackDiagnostics.Failure("visual.label-editing", listView);
                return false;
            }

            POINT viewOrigin = default;
            POINT listOrigin = default;
            if (!ClientToScreen(viewHwnd, ref viewOrigin) ||
                !ClientToScreen(listView, ref listOrigin))
            {
                IconSnackDiagnostics.Failure("visual.screen-origin-failed",
                    listView);
                return false;
            }

            for (int index = 0; index < count; index++)
            {
                IntPtr pidl = IntPtr.Zero;
                try
                {
                    if (view.Item(index, out pidl) != 0 || pidl == IntPtr.Zero)
                    {
                        IconSnackDiagnostics.Failure("visual.item-index-unavailable",
                            listView);
                        return false;
                    }
                    string id = ReadItemIdentity(folder, pidl).Id;
                    if (!string.Equals(id, platformId, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (view.GetItemPosition(pidl, out POINT shellPosition) != 0)
                    {
                        IconSnackDiagnostics.Failure("visual.shell-position-failed",
                            listView);
                        return false;
                    }
                    if (!TryReadListViewGeometry(listView, listProcessId, index,
                            out POINT nativePosition, out RECT nativeBounds))
                    {
                        IconSnackDiagnostics.Failure("visual.native-geometry-failed",
                            listView);
                        return false;
                    }
                    if (!PositionsMatch(shellPosition, viewOrigin,
                            nativePosition, listOrigin))
                    {
                        IconSnackDiagnostics.Failure("visual.position-mismatch",
                            listView, new Rect(listOrigin.X + nativePosition.X,
                                listOrigin.Y + nativePosition.Y, 0, 0));
                        return false;
                    }
                    if (!IsSaneItemRect(nativeBounds, nativePosition))
                    {
                        IconSnackDiagnostics.Failure("visual.native-bounds-invalid",
                            listView);
                        return false;
                    }
                    if (!TrySendListView(listView, LVM_GETEDITCONTROL, IntPtr.Zero,
                            IntPtr.Zero, out editControl) || editControl != IntPtr.Zero)
                    {
                        IconSnackDiagnostics.Failure("visual.edit-state-changed",
                            listView);
                        return false;
                    }
                    if (!TrySendListView(listView, LVM_GETITEMCOUNT, IntPtr.Zero,
                            IntPtr.Zero, out nativeCount) || nativeCount.ToInt64() != count)
                    {
                        IconSnackDiagnostics.Failure("visual.item-count-changed",
                            listView);
                        return false;
                    }

                    // The desktop can change during two cross-process reads.
                    // Verify that the same Shell item still occupies the index.
                    IntPtr verifyPidl = IntPtr.Zero;
                    try
                    {
                        if (view.Item(index, out verifyPidl) != 0 ||
                            verifyPidl == IntPtr.Zero ||
                            !string.Equals(ReadItemIdentity(folder, verifyPidl).Id,
                                platformId, StringComparison.OrdinalIgnoreCase) ||
                            view.GetItemPosition(verifyPidl, out POINT latestPosition) != 0 ||
                            !PositionsMatch(latestPosition, viewOrigin,
                                nativePosition, listOrigin))
                        {
                            IconSnackDiagnostics.Failure("visual.item-changed-during-read",
                                listView);
                            return false;
                        }
                    }
                    finally
                    {
                        if (verifyPidl != IntPtr.Zero)
                            Marshal.FreeCoTaskMem(verifyPidl);
                    }

                    double left = listOrigin.X + nativeBounds.Left;
                    double top = listOrigin.Y + nativeBounds.Top;
                    double right = listOrigin.X + nativeBounds.Right;
                    double bottom = listOrigin.Y + nativeBounds.Bottom;
                    Rect clipped = Rect.Intersect(new Rect(left, top,
                            right - left, bottom - top),
                        new Rect(listOrigin.X + client.Left,
                            listOrigin.Y + client.Top,
                            client.Right - client.Left,
                            client.Bottom - client.Top));
                    if (clipped.IsEmpty || clipped.Width < 8 || clipped.Height < 8)
                    {
                        IconSnackDiagnostics.Failure("visual.item-bounds-invalid",
                            listView, clipped);
                        return false;
                    }

                    screenBounds = clipped;
                    return true;
                }
                finally
                {
                    if (pidl != IntPtr.Zero)
                        Marshal.FreeCoTaskMem(pidl);
                }
            }
            IconSnackDiagnostics.Failure("visual.item-not-found", listView);
            return false;
        }
        catch (Exception ex) when (ex is COMException or ExternalException or
                                   InvalidCastException or InvalidComObjectException or
                                   TypeLoadException or ArgumentException or
                                   DllNotFoundException or EntryPointNotFoundException)
        {
            IconSnackDiagnostics.Failure("visual.exception", errorCode: ex.HResult);
            return false;
        }
        finally
        {
            ReleaseCom(folder);
            ReleaseCom(shellView);
            ReleaseCom(browser);
            ReleaseCom(dispatch);
            ReleaseCom(shellWindows);
        }
    }

    private static bool PositionsMatch(POINT shellPosition, POINT shellOrigin,
        POINT listPosition, POINT listOrigin)
        => Math.Abs((shellPosition.X + shellOrigin.X) -
                    (listPosition.X + listOrigin.X)) <= 4 &&
           Math.Abs((shellPosition.Y + shellOrigin.Y) -
                    (listPosition.Y + listOrigin.Y)) <= 4;

    private static bool IsSaneItemRect(RECT bounds, POINT position)
    {
        long width = (long)bounds.Right - bounds.Left;
        long height = (long)bounds.Bottom - bounds.Top;
        return width is >= 8 and <= MaxVisualBoundsPixels &&
               height is >= 8 and <= MaxVisualBoundsPixels &&
               position.X >= bounds.Left - 8 && position.X <= bounds.Right + 8 &&
               position.Y >= bounds.Top - 8 && position.Y <= bounds.Bottom + 8;
    }

    private static bool TrySendListView(IntPtr listView, uint message,
        IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        return SendMessageTimeoutW(listView, message, wParam, lParam,
            SMTO_ABORTIFHUNG | SMTO_BLOCK, ShellMessageTimeoutMs,
            out result) != IntPtr.Zero;
    }

    private static bool TryReadListViewGeometry(IntPtr listView,
        uint expectedProcessId, int index, out POINT position, out RECT bounds)
    {
        position = default;
        bounds = default;
        if (GetWindowThreadProcessId(listView, out uint processId) == 0 ||
            processId != expectedProcessId)
            return false;

        IntPtr process = OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ |
            PROCESS_VM_WRITE, false, processId);
        if (process == IntPtr.Zero)
            return false;
        IntPtr remote = IntPtr.Zero;
        bool remoteMayStillBeUsed = false;
        try
        {
            remote = VirtualAllocEx(process, IntPtr.Zero,
                (UIntPtr)Marshal.SizeOf<RECT>(), MEM_COMMIT | MEM_RESERVE,
                PAGE_READWRITE);
            if (remote == IntPtr.Zero)
                return false;
            if (!TrySendListView(listView, LVM_GETITEMPOSITION,
                    (IntPtr)index, remote, out IntPtr positionResult))
            {
                // SendMessageTimeout may return before Explorer finishes the
                // message. Keep the tiny remote buffer alive until Explorer
                // exits rather than let its delayed write touch freed memory.
                remoteMayStillBeUsed = true;
                return false;
            }
            if (positionResult == IntPtr.Zero ||
                !ReadProcessMemory(process, remote, out position,
                    (UIntPtr)Marshal.SizeOf<POINT>(), out UIntPtr positionBytes) ||
                positionBytes.ToUInt64() != (ulong)Marshal.SizeOf<POINT>())
                return false;

            RECT requested = default; // LVIR_BOUNDS = 0 in RECT.Left.
            if (!WriteProcessMemory(process, remote, ref requested,
                    (UIntPtr)Marshal.SizeOf<RECT>(), out UIntPtr written) ||
                written.ToUInt64() != (ulong)Marshal.SizeOf<RECT>())
                return false;
            if (!TrySendListView(listView, LVM_GETITEMRECT,
                    (IntPtr)index, remote, out IntPtr rectResult))
            {
                remoteMayStillBeUsed = true;
                return false;
            }
            if (rectResult == IntPtr.Zero ||
                !ReadProcessMemory(process, remote, out bounds,
                    (UIntPtr)Marshal.SizeOf<RECT>(), out UIntPtr read) ||
                read.ToUInt64() != (ulong)Marshal.SizeOf<RECT>() ||
                GetWindowThreadProcessId(listView, out processId) == 0 ||
                processId != expectedProcessId)
                return false;
            return true;
        }
        finally
        {
            if (remote != IntPtr.Zero && !remoteMayStillBeUsed)
                VirtualFreeEx(process, remote, UIntPtr.Zero, MEM_RELEASE);
            CloseHandle(process);
        }
    }

    /// <summary>
    /// Returns an empty snapshot if Explorer is unavailable, desktop icons are
    /// hidden, or its Shell view cannot be queried. Never changes any icon.
    /// Calls should be made from an STA thread, as for the WPF UI dispatcher.
    /// </summary>
    public IReadOnlyList<IconPlatform> GetPlatforms()
    {
        if (!OperatingSystem.IsWindows())
            return Array.Empty<IconPlatform>();

        object? shellWindows = null;
        object? dispatch = null;
        IShellBrowser? browser = null;
        IShellView? shellView = null;
        IShellFolder? folder = null;
        IEnumIDList? enumerator = null;

        try
        {
            // SHGetSettings is read-only. The view HWND can remain visible
            // even after Explorer's "Show desktop icons" option is turned off.
            SHGetSettings(out uint shellFlags, SSF_HIDEICONS);
            if ((shellFlags & SHELLFLAGSTATE_HIDE_ICONS) != 0)
                return Array.Empty<IconPlatform>();

            Type? type = Type.GetTypeFromCLSID(ShellWindowsClsid);
            if (type is null || Activator.CreateInstance(type) is not IShellWindows windows)
                return Array.Empty<IconPlatform>();

            shellWindows = windows;
            object location = CSIDL_DESKTOP;
            object empty = null!; // VT_EMPTY for FindWindowSW's reserved argument.
            if (windows.FindWindowSW(ref location, ref empty, SWC_DESKTOP,
                    out _, SWFO_NEEDDISPATCH, out object desktopDispatch) != 0 ||
                desktopDispatch is not IServiceProvider serviceProvider)
                return Array.Empty<IconPlatform>();

            dispatch = desktopDispatch;
            Guid browserIid = ShellBrowserIid;
            Guid browserSid = TopLevelBrowserSid;
            if (serviceProvider.QueryService(ref browserSid, ref browserIid,
                    out browser) != 0 || browser is null ||
                browser.QueryActiveShellView(out shellView) != 0 || shellView is null ||
                shellView.GetWindow(out IntPtr viewHwnd) != 0 || viewHwnd == IntPtr.Zero ||
                !IsWindowVisible(viewHwnd))
                return Array.Empty<IconPlatform>();

            // GetItemPosition is relative to the view's client area. Its window
            // origin can be negative when the virtual desktop spans monitors.
            POINT origin = default;
            if (!ClientToScreen(viewHwnd, ref origin) ||
                !GetClientRect(viewHwnd, out RECT client) ||
                client.Right <= client.Left || client.Bottom <= client.Top)
                return Array.Empty<IconPlatform>();

            IFolderView view = (IFolderView)shellView;
            POINT spacing = default;
            if (view.GetSpacing(ref spacing) != 0 || spacing.X <= 0 || spacing.Y <= 0)
            {
                if (view.GetDefaultSpacing(out spacing) != 0 || spacing.X <= 0 || spacing.Y <= 0)
                    return Array.Empty<IconPlatform>();
            }

            Guid folderIid = ShellFolderIid;
            Guid enumIid = EnumIdListIid;
            if (view.GetFolder(ref folderIid, out folder) != 0 || folder is null ||
                view.Items(SVGIO_ALLVIEW, ref enumIid, out enumerator) != 0 ||
                enumerator is null)
                return Array.Empty<IconPlatform>();

            Rect visibleView = new(origin.X + client.Left, origin.Y + client.Top,
                client.Right - client.Left, client.Bottom - client.Top);
            var platforms = new List<IconPlatform>();
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var absolutePidls = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

            while (true)
            {
                IntPtr pidl = IntPtr.Zero;
                int next = enumerator.Next(1, out pidl, out uint fetched);
                if (next < 0)
                {
                    if (pidl != IntPtr.Zero)
                        Marshal.FreeCoTaskMem(pidl);
                    return Array.Empty<IconPlatform>();
                }
                if (next != 0 || fetched != 1)
                {
                    if (pidl != IntPtr.Zero)
                        Marshal.FreeCoTaskMem(pidl);
                    break;
                }

                try
                {
                    if (pidl == IntPtr.Zero || view.GetItemPosition(pidl, out POINT position) != 0)
                        continue;

                    Rect bounds = new(origin.X + position.X, origin.Y + position.Y,
                        spacing.X, spacing.Y);
                    if (!bounds.IntersectsWith(visibleView))
                        continue;

                    (string id, string name, byte[]? absolutePidl) = ReadItemIdentity(folder, pidl);
                    if (!string.IsNullOrWhiteSpace(id) && seenIds.Add(id))
                    {
                        platforms.Add(new IconPlatform(id, name, bounds));
                        if (absolutePidl is not null)
                            absolutePidls[id] = absolutePidl;
                    }
                }
                finally
                {
                    if (pidl != IntPtr.Zero)
                        Marshal.FreeCoTaskMem(pidl);
                }
            }

            lock (_snapshotLock)
            {
                _absolutePidls = absolutePidls;
                _iconImages = new(StringComparer.OrdinalIgnoreCase);
            }
            return platforms;
        }
        catch (Exception ex) when (ex is COMException or ExternalException or
                                   InvalidCastException or InvalidComObjectException or
                                   TypeLoadException or ArgumentException or
                                   DllNotFoundException or EntryPointNotFoundException)
        {
            return Array.Empty<IconPlatform>();
        }
        finally
        {
            ReleaseCom(enumerator);
            ReleaseCom(folder);
            // IFolderView is a cast of this same runtime callable wrapper.
            ReleaseCom(shellView);
            ReleaseCom(browser);
            // IServiceProvider is a cast of the returned IDispatch wrapper.
            ReleaseCom(dispatch);
            ReleaseCom(shellWindows);
        }
    }

    private static (string Id, string Name, byte[]? AbsolutePidl) ReadItemIdentity(
        IShellFolder folder, IntPtr pidl)
    {
        IShellItem? item = null;
        byte[]? absolutePidl = null;
        try
        {
            Guid iid = ShellItemIid;
            if (SHCreateItemWithParent(IntPtr.Zero, folder, pidl, ref iid, out item) == 0 &&
                item is not null)
            {
                string? parsingName = ReadDisplayName(item, SIGDN_DESKTOPABSOLUTEPARSING);
                string? displayName = ReadDisplayName(item, SIGDN_NORMALDISPLAY);
                absolutePidl = ReadAbsolutePidl(item);
                if (!string.IsNullOrWhiteSpace(parsingName))
                    return (parsingName, string.IsNullOrWhiteSpace(displayName)
                        ? parsingName : displayName, absolutePidl);
            }
        }
        catch (ExternalException) { /* A Shell item may disappear during enumeration. */ }
        finally
        {
            ReleaseCom(item);
        }

        // The PIDL describes the item, not its screen position, so this key is
        // still stable when the user drags an icon to bridge a platform gap.
        uint size = ILGetSize(pidl);
        if (size is > 0 and <= 16384)
        {
            var bytes = new byte[size];
            Marshal.Copy(pidl, bytes, 0, (int)size);
            return ($"pidl:{Convert.ToHexString(SHA256.HashData(bytes))}", "桌面图标", absolutePidl);
        }

        return (string.Empty, "桌面图标", null);
    }

    private static byte[]? ReadAbsolutePidl(IShellItem item)
    {
        IntPtr pidl = IntPtr.Zero;
        try
        {
            if (SHGetIDListFromObject(item, out pidl) != 0 || pidl == IntPtr.Zero)
                return null;
            uint size = ILGetSize(pidl);
            if (size is 0 or > 16384)
                return null;
            var bytes = new byte[size];
            Marshal.Copy(pidl, bytes, 0, (int)size);
            return bytes;
        }
        finally
        {
            if (pidl != IntPtr.Zero)
                Marshal.FreeCoTaskMem(pidl);
        }
    }

    private static string? ReadDisplayName(IShellItem item, uint kind)
    {
        IntPtr text = IntPtr.Zero;
        try
        {
            return item.GetDisplayName(kind, out text) == 0 && text != IntPtr.Zero
                ? Marshal.PtrToStringUni(text)
                : null;
        }
        finally
        {
            if (text != IntPtr.Zero)
                Marshal.FreeCoTaskMem(text);
        }
    }

    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try { Marshal.ReleaseComObject(value); }
            catch (InvalidComObjectException) { }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string? szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string? szTypeName;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "FindWindowExW", CharSet = CharSet.Unicode,
        ExactSpelling = true)]
    private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after,
        string className, string? windowName);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd,
        out uint processId);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW",
        ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr SendMessageTimeoutW(IntPtr hwnd, uint message,
        IntPtr wParam, IntPtr lParam, uint flags, uint timeoutMs,
        out IntPtr result);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address,
        UIntPtr size, uint allocationType, uint protect);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFreeEx(IntPtr process, IntPtr address,
        UIntPtr size, uint freeType);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address,
        out POINT buffer, UIntPtr size, out UIntPtr read);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address,
        out RECT buffer, UIntPtr size, out UIntPtr read);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemory(IntPtr process, IntPtr address,
        ref RECT buffer, UIntPtr size, out UIntPtr written);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern uint ILGetSize(IntPtr pidl);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern void SHGetSettings(out uint shellFlagState, uint mask);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetIDListFromObject(
        [MarshalAs(UnmanagedType.Interface)] IShellItem item, out IntPtr pidl);

    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode,
        ExactSpelling = true)]
    private static extern IntPtr SHGetFileInfoW(IntPtr pidl, uint attributes,
        out SHFILEINFO info, uint infoSize, uint flags);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetImageList(int imageListKind, ref Guid iid,
        out IntPtr imageList);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern IntPtr ImageList_GetIcon(IntPtr imageList, int index,
        uint style);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHCreateItemWithParent(IntPtr parentPidl,
        [MarshalAs(UnmanagedType.Interface)] IShellFolder parentFolder,
        IntPtr childPidl, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    // Only the read methods below are called. Unused slots exist to preserve
    // the documented native COM vtable order.
    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellWindows
    {
        [PreserveSig] int DispatchSlot1();
        [PreserveSig] int DispatchSlot2();
        [PreserveSig] int DispatchSlot3();
        [PreserveSig] int DispatchSlot4();
        [PreserveSig] int SlotCount();
        [PreserveSig] int SlotItem();
        [PreserveSig] int SlotNewEnum();
        [PreserveSig] int SlotRegister();
        [PreserveSig] int SlotRegisterPending();
        [PreserveSig] int SlotRevoke();
        [PreserveSig] int SlotOnNavigate();
        [PreserveSig] int SlotOnActivated();
        [PreserveSig] int FindWindowSW(
            [In, MarshalAs(UnmanagedType.Struct)] ref object location,
            [In, MarshalAs(UnmanagedType.Struct)] ref object locationRoot,
            int windowClass, out int hwnd, int options,
            [MarshalAs(UnmanagedType.IDispatch)] out object dispatch);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid iid,
            [MarshalAs(UnmanagedType.Interface)] out IShellBrowser browser);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        [PreserveSig] int GetWindow(out IntPtr hwnd);
        [PreserveSig] int ContextSensitiveHelp();
        [PreserveSig] int SlotInsertMenus();
        [PreserveSig] int SlotSetMenu();
        [PreserveSig] int SlotRemoveMenus();
        [PreserveSig] int SlotSetStatusText();
        [PreserveSig] int SlotEnableModeless();
        [PreserveSig] int SlotTranslateAccelerator();
        [PreserveSig] int SlotBrowseObject();
        [PreserveSig] int SlotGetViewStateStream();
        [PreserveSig] int SlotGetControlWindow();
        [PreserveSig] int SlotSendControlMsg();
        [PreserveSig] int QueryActiveShellView(out IShellView view);
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        [PreserveSig] int GetWindow(out IntPtr hwnd);
    }

    [ComImport, Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFolderView
    {
        [PreserveSig] int SlotGetCurrentViewMode();
        [PreserveSig] int SlotSetCurrentViewMode();
        [PreserveSig] int GetFolder(ref Guid iid, out IShellFolder folder);
        [PreserveSig] int Item(int index, out IntPtr pidl);
        [PreserveSig] int ItemCount(uint flags, out int count);
        [PreserveSig] int Items(uint flags, ref Guid iid, out IEnumIDList items);
        [PreserveSig] int SlotGetSelectionMarkedItem();
        [PreserveSig] int SlotGetFocusedItem();
        [PreserveSig] int GetItemPosition(IntPtr pidl, out POINT point);
        [PreserveSig] int GetSpacing(ref POINT spacing);
        [PreserveSig] int GetDefaultSpacing(out POINT spacing);
    }

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder { }

    [ComImport, Guid("000214F2-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumIDList
    {
        [PreserveSig] int Next(uint count, out IntPtr pidl, out uint fetched);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int SlotBindToHandler();
        [PreserveSig] int SlotGetParent();
        [PreserveSig] int GetDisplayName(uint kind, out IntPtr displayName);
    }
}
