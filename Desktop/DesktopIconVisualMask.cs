using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeepSeekFishPet.Behavior;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Image = System.Windows.Controls.Image;
using Rect = System.Windows.Rect;

namespace DeepSeekFishPet.Desktop;

/// <summary>
/// Covers one Explorer desktop icon with its current wallpaper pixels. This is
/// only a visual effect: the Shell item, its filename and its layout are never
/// changed. Dispose removes the cover immediately, even if an animation stops.
/// The cover sits above Explorer's icons and below the pet and app windows.
/// </summary>
public sealed class DesktopIconVisualMask : IDisposable
{
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATEANDEAT = 4;
    private const int GW_HWNDNEXT = 2;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_HIDEWINDOW = 0x0080;
    private const int SW_HIDE = 0;
    private const int MaxMaskPixels = 512;

    private static readonly Guid WallpaperClsid =
        new("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD");

    private readonly Window _window;
    private readonly IntPtr _hwnd;
    private readonly IntPtr _desktopHost;
    private readonly Rect _screenBounds;
    private readonly Rect _sourcePlatformBounds;
    private bool _disposed;
    private bool _revealed;
    // Only the mask directly below the pet is tracked. A click that raises
    // the pet above an app inserts that app between the two HWNDs; in that
    // case we must lower the whole group again. Older masks must never keep
    // stale adjacency flags after another mask is added.
    private bool _petImmediatelyAboveMask;

    private DesktopIconVisualMask(Window window, IntPtr hwnd, IntPtr desktopHost,
        Rect screenBounds,
        Rect sourcePlatformBounds)
    {
        _window = window;
        _hwnd = hwnd;
        _desktopHost = desktopHost;
        _screenBounds = screenBounds;
        _sourcePlatformBounds = sourcePlatformBounds;
    }

    /// <summary>
    /// Returns false without showing anything when the exact wallpaper image,
    /// desktop host, or screen geometry cannot be obtained. Call on the WPF UI
    /// thread. The caller owns and disposes the returned cover.
    /// </summary>
    public static bool TryShow(IconPlatform platform, Rect exactScreenBounds,
        out DesktopIconVisualMask? mask)
    {
        mask = null;
        if (!OperatingSystem.IsWindows() || platform.Bounds.IsEmpty ||
            exactScreenBounds.IsEmpty ||
            !double.IsFinite(exactScreenBounds.Left) ||
            !double.IsFinite(exactScreenBounds.Top) ||
            !double.IsFinite(exactScreenBounds.Right) ||
            !double.IsFinite(exactScreenBounds.Bottom))
        {
            IconSnackDiagnostics.Failure("show.invalid-input", bounds: exactScreenBounds);
            return false;
        }

        // The caller obtained this rectangle from the live desktop ListView's
        // LVIR_BOUNDS. Never turn the much larger spacing/layout cell into a
        // cover: it would create an obvious wallpaper-colored rectangle.
        // Explorer's text shadow and selection outline can extend a couple of
        // device pixels beyond LVIR_BOUNDS. Cover that fringe as well, without
        // reverting to the much larger icon spacing cell.
        const double fringe = 3;
        Rect area = new(Math.Floor(exactScreenBounds.Left - fringe),
            Math.Floor(exactScreenBounds.Top - fringe),
            Math.Ceiling(exactScreenBounds.Right + fringe) -
                Math.Floor(exactScreenBounds.Left - fringe),
            Math.Ceiling(exactScreenBounds.Bottom + fringe) -
                Math.Floor(exactScreenBounds.Top - fringe));
        if (area.Width is <= 0 or > MaxMaskPixels ||
            area.Height is <= 0 or > MaxMaskPixels)
        {
            IconSnackDiagnostics.Failure("show.bounds-out-of-range", bounds: area);
            return false;
        }

        IntPtr desktop = FindDesktopHost();
        if (desktop == IntPtr.Zero)
        {
            IconSnackDiagnostics.Failure("show.desktop-host-missing", bounds: area);
            return false;
        }
        if (!TryDrawWallpaper(area, out BitmapSource? pixels))
        {
            IconSnackDiagnostics.Failure("show.wallpaper-unavailable", desktop, area);
            return false;
        }

        Window? window = null;
        try
        {
            window = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                ShowActivated = false,
                Focusable = false,
                Topmost = false,
                ResizeMode = ResizeMode.NoResize,
                Width = area.Width,
                Height = area.Height,
                Opacity = 0,
                Content = new Image
                {
                    Source = pixels,
                    Stretch = Stretch.Fill,
                    // While the icon is swallowed, its old screen position
                    // should not launch an invisible Explorer item on click.
                    IsHitTestVisible = true
                }
            };
            window.Show();
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                IconSnackDiagnostics.Failure("show.mask-hwnd-missing", desktop, area);
                window.Close();
                return false;
            }

            if (!DesktopWindowLayer.TryMakeIndependent(hwnd, noActivate: true))
            {
                IconSnackDiagnostics.Failure("show.mask-owner-detach-failed", hwnd,
                    area);
                window.Close();
                return false;
            }
            // The cover blocks clicks on the swallowed icon without becoming
            // active or rising above the pet when the user clicks its pixels.
            HwndSource? source = HwndSource.FromHwnd(hwnd);
            if (source is null)
            {
                IconSnackDiagnostics.Failure("show.hwnd-source-missing", hwnd, area);
                window.Close();
                return false;
            }
            source.AddHook(BlockCoverMouseActivation);

            // Windows can keep one or more invisible WorkerW windows above the
            // desktop icon host. Place the cover in the ordinary band near the
            // host, then verify relative order rather than impossible adjacency.
            // Move to the target monitor before reading DPI; the newly shown
            // WPF window may have started on a monitor with a different scale.
            if (!SetWindowPos(hwnd, IntPtr.Zero,
                    (int)area.Left, (int)area.Top, (int)area.Width,
                    (int)area.Height, SWP_NOZORDER | SWP_NOACTIVATE))
            {
                IconSnackDiagnostics.Failure("show.initial-position-failed", hwnd,
                    area, Marshal.GetLastPInvokeError());
                window.Close();
                return false;
            }
            uint dpi = GetDpiForWindow(hwnd);
            double dipPerPixel = dpi == 0 ? 1 : 96.0 / dpi;
            window.Width = area.Width * dipPerPixel;
            window.Height = area.Height * dipPerPixel;
            if (!SetWindowPos(hwnd, IntPtr.Zero,
                    (int)area.Left, (int)area.Top, (int)area.Width,
                    (int)area.Height, SWP_NOZORDER | SWP_NOACTIVATE))
            {
                IconSnackDiagnostics.Failure("show.dpi-position-failed", hwnd,
                    area, Marshal.GetLastPInvokeError());
                window.Close();
                return false;
            }
            if (!DesktopWindowLayer.TryPlaceJustAbove(hwnd, desktop))
            {
                IconSnackDiagnostics.Failure("show.above-desktop-failed", hwnd,
                    area);
                window.Close();
                return false;
            }
            if (DesktopWindowLayer.IsTopmost(hwnd))
            {
                IconSnackDiagnostics.Failure("show.mask-unexpected-topmost", hwnd,
                    area);
                window.Close();
                return false;
            }

            // Stay invisible until TryKeepBelowPet has verified that the pet
            // is above us. A compositor frame between Show and that check
            // must never display a cover over the pet or its speech bubble.
            var candidate = new DesktopIconVisualMask(window, hwnd, desktop, area,
                platform.Bounds);
            if (!candidate.HasExpectedBounds())
            {
                IconSnackDiagnostics.Failure("show.mask-bounds-mismatch", hwnd, area);
                candidate.Dispose();
                return false;
            }
            mask = candidate;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or
                                   ExternalException or ArgumentException)
        {
            IconSnackDiagnostics.Failure("show.exception", desktop, area, ex.HResult);
            window?.Close();
            return false;
        }
    }

    public Rect ScreenBounds => _screenBounds;

    /// <summary>The spacing cell when this visual cover was created.</summary>
    public Rect SourcePlatformBounds => _sourcePlatformBounds;

    /// <summary>
    /// Keep the wallpaper cover above Explorer and the pet above that cover.
    /// A WPF window or a click can change their order after Show,
    /// so the caller repeats this check while the icon remains swallowed.
    /// Returns false if the order cannot be verified; the caller must remove
    /// the cover instead of allowing it to clip the pet or its speech bubble.
    /// </summary>
    public bool TryKeepBelowPet(IntPtr petHwnd) =>
        TryKeepBelowPet(new[] { this }, petHwnd);

    /// <summary>
    /// Verify live covers without changing their windows in the steady state.
    /// New covers are revealed individually; only a genuinely displaced cover
    /// requires a group rebase. A failed verification leaves covers hidden.
    /// Call on the WPF UI thread and dispose the group if this returns false.
    /// </summary>
    public static bool TryKeepBelowPet(
        IReadOnlyList<DesktopIconVisualMask> masks, IntPtr petHwnd)
    {
        ArgumentNullException.ThrowIfNull(masks);
        if (masks.Count == 0)
            return true;

        DesktopIconVisualMask? first = masks[0];
        if (first is null)
        {
            ConcealAll(masks);
            IconSnackDiagnostics.Failure("order.null-mask", petHwnd);
            return false;
        }

        IntPtr desktopHost = first._desktopHost;
        var seen = new HashSet<IntPtr>();
        foreach (DesktopIconVisualMask? mask in masks)
        {
            if (mask is null || mask._disposed ||
                mask._desktopHost != desktopHost ||
                !seen.Add(mask._hwnd) || !IsWindow(mask._hwnd) ||
                DesktopWindowLayer.IsTopmost(mask._hwnd) ||
                petHwnd == mask._hwnd)
            {
                ConcealAll(masks);
                IconSnackDiagnostics.Failure("order.invalid-mask", petHwnd,
                    mask?._screenBounds);
                return false;
            }
        }

        if (petHwnd == IntPtr.Zero || petHwnd == desktopHost ||
            !IsWindow(petHwnd) || !IsWindow(desktopHost))
        {
            ConcealAll(masks);
            IconSnackDiagnostics.Failure("order.invalid-window", petHwnd,
                first._screenBounds);
            return false;
        }
        if (FindDesktopHost() != desktopHost)
        {
            ConcealAll(masks);
            IconSnackDiagnostics.Failure("order.desktop-host-changed", desktopHost,
                first._screenBounds);
            return false;
        }
        if (DesktopWindowLayer.IsTopmost(petHwnd))
        {
            ConcealAll(masks);
            IconSnackDiagnostics.Failure("order.unexpected-topmost", petHwnd,
                first._screenBounds);
            return false;
        }

        bool safe = true;
        bool badBounds = false;
        var pending = new List<DesktopIconVisualMask>();
        foreach (DesktopIconVisualMask mask in masks)
        {
            safe &= mask.HasSafeOrder(petHwnd);
            badBounds |= !mask.HasExpectedBounds();
            if (!mask._revealed || !IsWindowVisible(mask._hwnd))
                pending.Add(mask);
        }

        if (badBounds)
        {
            ConcealAll(masks);
            IconSnackDiagnostics.Failure("order.mask-bounds-changed", petHwnd,
                first._screenBounds);
            return false;
        }

        bool petRaised = false;
        if (pending.Count == 0)
        {
            IntPtr directlyBelowPet = GetWindow(petHwnd, GW_HWNDNEXT);
            bool hasSentinel = false;
            foreach (DesktopIconVisualMask mask in masks)
            {
                if (!mask._petImmediatelyAboveMask)
                    continue;
                hasSentinel = true;
                if (directlyBelowPet != mask._hwnd)
                    petRaised = true;
            }
            if (!hasSentinel)
            {
                // The former sentinel may have just been disposed. Adopt the
                // next live mask without moving or flashing any window.
                foreach (DesktopIconVisualMask mask in masks)
                {
                    if (directlyBelowPet == mask._hwnd)
                    {
                        mask._petImmediatelyAboveMask = true;
                        break;
                    }
                }
            }
        }

        // Most frames take this path. In particular, do not call SetWindowPos,
        // SWP_SHOWWINDOW, SWP_HIDEWINDOW or WPF Opacity setters here: even a
        // redundant call can make a layered window visibly flash.
        if (safe && pending.Count == 0 && !petRaised)
            return true;

        // Showing another icon's cover may put that new window above the pet.
        // If the existing covers still have safe order, leave them untouched.
        bool onlyPendingOutOfOrder = !petRaised && pending.Count > 0 &&
            DesktopWindowLayer.IsAbove(petHwnd, desktopHost);
        foreach (DesktopIconVisualMask mask in masks)
        {
            if (!mask.HasSafeOrder(petHwnd) && !pending.Contains(mask))
            {
                onlyPendingOutOfOrder = false;
                break;
            }
        }

        IReadOnlyList<DesktopIconVisualMask> toPlace =
            onlyPendingOutOfOrder ? pending : masks;
        if (!ConcealAll(toPlace))
            return false;
        if (!DesktopWindowLayer.TryMakeIndependent(petHwnd))
        {
            ConcealAll(masks);
            IconSnackDiagnostics.Failure("order.pet-owner-detach-failed",
                petHwnd, first._screenBounds);
            return false;
        }
        if (!onlyPendingOutOfOrder &&
            !DesktopWindowLayer.TryPlaceJustAbove(petHwnd, desktopHost))
        {
            IconSnackDiagnostics.Failure("order.pet-above-desktop-failed",
                petHwnd, first._screenBounds);
            return false;
        }

        foreach (DesktopIconVisualMask mask in toPlace)
        {
            if (!SetWindowPos(mask._hwnd, petHwnd, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE) ||
                !mask.HasSafeOrder(petHwnd))
            {
                ConcealAll(masks);
                IconSnackDiagnostics.Failure("order.mask-below-pet-failed",
                    mask._hwnd, mask._screenBounds,
                    Marshal.GetLastPInvokeError());
                return false;
            }
        }

        foreach (DesktopIconVisualMask mask in toPlace)
        {
            // Show and insert below the pet in one native operation. Opacity
            // is prepared while the HWND is still hidden, so no stale frame
            // can appear in front of the pet between those two operations.
            mask._window.Opacity = 1;
            if (!SetWindowPos(mask._hwnd, petHwnd, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE |
                    SWP_SHOWWINDOW) || !IsWindowVisible(mask._hwnd) ||
                !mask.HasSafeOrder(petHwnd))
            {
                ConcealAll(masks);
                IconSnackDiagnostics.Failure("order.mask-show-failed",
                    mask._hwnd, mask._screenBounds,
                    Marshal.GetLastPInvokeError());
                return false;
            }
            mask._revealed = true;
        }
        foreach (DesktopIconVisualMask mask in masks)
        {
            if (!mask.HasSafeOrder(petHwnd) || !mask.HasExpectedBounds())
            {
                ConcealAll(masks);
                IconSnackDiagnostics.Failure("order.changed-after-reveal",
                    mask._hwnd, mask._screenBounds);
                return false;
            }
        }
        IntPtr belowPet = GetWindow(petHwnd, GW_HWNDNEXT);
        foreach (DesktopIconVisualMask mask in masks)
            mask._petImmediatelyAboveMask = mask._hwnd == belowPet;
        return true;
    }

    private static bool ConcealAll(
        IReadOnlyList<DesktopIconVisualMask> masks)
    {
        bool allHidden = true;
        foreach (DesktopIconVisualMask? mask in masks)
        {
            if (mask is null || mask._disposed)
                continue;

            mask._window.Opacity = 0;
            mask._revealed = false;
            if (!IsWindow(mask._hwnd))
                continue;

            // WPF opacity is composited asynchronously. Hide the HWND in the
            // window manager before any group member changes Z order.
            if (!SetWindowPos(mask._hwnd, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER |
                    SWP_NOACTIVATE | SWP_HIDEWINDOW) &&
                IsWindowVisible(mask._hwnd))
                ShowWindow(mask._hwnd, SW_HIDE);
            if (IsWindowVisible(mask._hwnd))
            {
                allHidden = false;
                IconSnackDiagnostics.Failure("order.mask-hide-failed",
                    mask._hwnd, mask._screenBounds,
                    Marshal.GetLastPInvokeError());
            }
        }
        return allHidden;
    }

    private bool HasSafeOrder(IntPtr petHwnd) =>
        DesktopWindowLayer.IsAbove(petHwnd, _hwnd) &&
        DesktopWindowLayer.IsAbove(_hwnd, _desktopHost) &&
        !DesktopWindowLayer.IsTopmost(petHwnd) &&
        !DesktopWindowLayer.IsTopmost(_hwnd);

    private bool HasExpectedBounds()
    {
        if (!GetWindowRect(_hwnd, out RECT actual))
            return false;
        // WPF can remeasure a layered window when it crosses a DPI boundary.
        // A shifted cover would leave icon pixels visible or paint a hard edge.
        const long tolerance = 1;
        return Math.Abs((long)actual.Left - (long)_screenBounds.Left) <= tolerance &&
            Math.Abs((long)actual.Top - (long)_screenBounds.Top) <= tolerance &&
            Math.Abs((long)actual.Right - (long)_screenBounds.Right) <= tolerance &&
            Math.Abs((long)actual.Bottom - (long)_screenBounds.Bottom) <= tolerance;
    }

    public void Dispose()
    {
        if (_disposed) return;
        ConcealAll(new[] { this });
        _disposed = true;
        _window.Close();
    }

    private static IntPtr BlockCoverMouseActivation(IntPtr hwnd, int message,
        IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WM_MOUSEACTIVATE)
            return IntPtr.Zero;
        handled = true;
        return new IntPtr(MA_NOACTIVATEANDEAT);
    }

    private static bool TryDrawWallpaper(Rect area, out BitmapSource? pixels)
    {
        pixels = null;
        object? instance = null;
        try
        {
            Type? type = Type.GetTypeFromCLSID(WallpaperClsid);
            if (type is null || Activator.CreateInstance(type) is not IDesktopWallpaper wallpaper)
            {
                IconSnackDiagnostics.Failure("wallpaper.service-unavailable",
                    bounds: area);
                return false;
            }
            instance = wallpaper;

            if (wallpaper.GetMonitorDevicePathCount(out uint count) != 0 ||
                count == 0 || count > 32 ||
                wallpaper.GetPosition(out int position) != 0 ||
                position is < 0 or > 5 ||
                wallpaper.GetBackgroundColor(out uint colorRef) != 0)
            {
                IconSnackDiagnostics.Failure("wallpaper.settings-unavailable",
                    bounds: area);
                return false;
            }

            var monitors = new List<(string Id, Rect Area)>();
            Rect virtualArea = Rect.Empty;
            for (uint i = 0; i < count; i++)
            {
                if (wallpaper.GetMonitorDevicePathAt(i, out IntPtr idPtr) != 0 ||
                    idPtr == IntPtr.Zero)
                {
                    IconSnackDiagnostics.Failure("wallpaper.monitor-id-unavailable",
                        bounds: area);
                    return false;
                }
                string? id;
                try { id = Marshal.PtrToStringUni(idPtr); }
                finally { Marshal.FreeCoTaskMem(idPtr); }
                if (string.IsNullOrEmpty(id) ||
                    wallpaper.GetMonitorRECT(id, out RECT monitorRect) != 0)
                {
                    IconSnackDiagnostics.Failure("wallpaper.monitor-bounds-unavailable",
                        bounds: area);
                    return false;
                }
                Rect monitorArea = new(monitorRect.Left, monitorRect.Top,
                    monitorRect.Right - monitorRect.Left,
                    monitorRect.Bottom - monitorRect.Top);
                if (monitorArea.IsEmpty)
                {
                    IconSnackDiagnostics.Failure("wallpaper.monitor-bounds-empty",
                        bounds: area);
                    return false;
                }
                monitors.Add((id, monitorArea));
                virtualArea.Union(monitorArea);
            }

            // A cell on a disconnected display, animated wallpaper, or an
            // unsupported composition must not produce a visible wrong patch.
            if (!monitors.Exists(m => m.Area.IntersectsWith(area)))
            {
                IconSnackDiagnostics.Failure("wallpaper.no-matching-monitor",
                    bounds: area);
                return false;
            }

            int width = (int)area.Width;
            int height = (int)area.Height;
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                Color background = Color.FromRgb((byte)(colorRef & 0xff),
                    (byte)((colorRef >> 8) & 0xff), (byte)((colorRef >> 16) & 0xff));
                dc.DrawRectangle(new SolidColorBrush(background), null,
                    new Rect(0, 0, width, height));

                foreach ((string id, Rect monitorArea) in monitors)
                {
                    Rect overlap = Rect.Intersect(area, monitorArea);
                    if (overlap.IsEmpty) continue;
                    if (!TryReadWallpaperPath(wallpaper, id, out string? path))
                    {
                        IconSnackDiagnostics.Failure("wallpaper.path-unavailable",
                            bounds: area);
                        return false;
                    }
                    if (string.IsNullOrEmpty(path)) continue; // Solid color.
                    if (!File.Exists(path))
                    {
                        IconSnackDiagnostics.Failure("wallpaper.image-missing",
                            bounds: area);
                        return false;
                    }
                    BitmapImage image = LoadWallpaper(path);
                    dc.PushClip(new RectangleGeometry(new Rect(
                        overlap.Left - area.Left, overlap.Top - area.Top,
                        overlap.Width, overlap.Height)));
                    DrawWallpaper(dc, image, position,
                        position is 1 or 5 ? virtualArea : monitorArea, area);
                    dc.Pop();
                }
            }

            var result = new RenderTargetBitmap(width, height, 96, 96,
                PixelFormats.Pbgra32);
            result.Render(visual);
            result.Freeze();
            pixels = result;
            return true;
        }
        catch (Exception ex) when (ex is COMException or ExternalException or
                                   IOException or UnauthorizedAccessException or
                                   NotSupportedException or ArgumentException or
                                   InvalidOperationException)
        {
            IconSnackDiagnostics.Failure("wallpaper.exception", bounds: area,
                errorCode: ex.HResult);
            return false;
        }
        finally
        {
            if (instance is not null && Marshal.IsComObject(instance))
                Marshal.ReleaseComObject(instance);
        }
    }

    private static bool TryReadWallpaperPath(IDesktopWallpaper wallpaper,
        string monitorId, out string? path)
    {
        path = null;
        IntPtr ptr = IntPtr.Zero;
        try
        {
            if (wallpaper.GetWallpaper(monitorId, out ptr) != 0)
                return false;
            path = ptr == IntPtr.Zero ? null : Marshal.PtrToStringUni(ptr);
            return true;
        }
        finally
        {
            if (ptr != IntPtr.Zero) Marshal.FreeCoTaskMem(ptr);
        }
    }

    private static BitmapImage LoadWallpaper(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static void DrawWallpaper(DrawingContext dc, BitmapImage image,
        int position, Rect display, Rect area)
    {
        double imageWidth = image.PixelWidth;
        double imageHeight = image.PixelHeight;
        if (imageWidth <= 0 || imageHeight <= 0)
            throw new InvalidOperationException("Invalid wallpaper image size.");

        if (position == 1) // Tile.
        {
            int x0 = (int)Math.Floor((area.Left - display.Left) / imageWidth);
            int x1 = (int)Math.Ceiling((area.Right - display.Left) / imageWidth);
            int y0 = (int)Math.Floor((area.Top - display.Top) / imageHeight);
            int y1 = (int)Math.Ceiling((area.Bottom - display.Top) / imageHeight);
            for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
                dc.DrawImage(image, new Rect(display.Left + x * imageWidth - area.Left,
                    display.Top + y * imageHeight - area.Top, imageWidth, imageHeight));
            return;
        }

        double scaleX = display.Width / imageWidth;
        double scaleY = display.Height / imageHeight;
        double width, height;
        switch (position)
        {
            case 0: // Center.
                width = imageWidth;
                height = imageHeight;
                break;
            case 2: // Stretch.
                width = display.Width;
                height = display.Height;
                break;
            case 3: // Fit.
                width = imageWidth * Math.Min(scaleX, scaleY);
                height = imageHeight * Math.Min(scaleX, scaleY);
                break;
            case 4: // Fill.
            case 5: // Span.
                width = imageWidth * Math.Max(scaleX, scaleY);
                height = imageHeight * Math.Max(scaleX, scaleY);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(position));
        }
        dc.DrawImage(image, new Rect(display.Left + (display.Width - width) / 2 - area.Left,
            display.Top + (display.Height - height) / 2 - area.Top, width, height));
    }

    internal static IntPtr FindDesktopHost()
    {
        IntPtr desktop = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            IntPtr shellView = FindWindowExW(window, IntPtr.Zero,
                "SHELLDLL_DefView", null);
            if (shellView == IntPtr.Zero || !IsWindowVisible(shellView))
                return true;
            desktop = window;
            return false;
        }, IntPtr.Zero);
        return desktop;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktopWallpaper
    {
        [PreserveSig] int SlotSetWallpaper();
        [PreserveSig] int GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorId,
            out IntPtr wallpaper);
        [PreserveSig] int GetMonitorDevicePathAt(uint index, out IntPtr monitorId);
        [PreserveSig] int GetMonitorDevicePathCount(out uint count);
        [PreserveSig] int GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId,
            out RECT rect);
        [PreserveSig] int SlotSetBackgroundColor();
        [PreserveSig] int GetBackgroundColor(out uint colorRef);
        [PreserveSig] int SlotSetPosition();
        [PreserveSig] int GetPosition(out int position);
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out RECT rect);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetWindow(IntPtr window, int command);

    [DllImport("user32.dll", EntryPoint = "FindWindowExW", CharSet = CharSet.Unicode,
        ExactSpelling = true)]
    private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after,
        string className, string? windowName);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter,
        int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(IntPtr window);
}
