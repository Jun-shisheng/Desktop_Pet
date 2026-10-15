using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Point = System.Windows.Point;
using Brushes = System.Windows.Media.Brushes;
using Image = System.Windows.Controls.Image;

namespace DeepSeekFishPet.Desktop;

/// <summary>A click-through, temporary visual copy that flies back to its desktop icon.</summary>
public sealed class IconReturnWindow : Window
{
    private const int CopyWindowSize = 24;
    private const int CopyImageSize = 22;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    private readonly Point _from;
    private readonly Point _to;
    private readonly IntPtr _petWindow;
    private readonly DispatcherTimer _timer;
    private readonly Image _iconImage;
    private DateTime _started;
    private readonly double _durationSeconds;
    private IntPtr _handle;
    private uint _lastDpi;
    private bool _layerReady;

    public IconReturnWindow(ImageSource icon, Point from, Point to,
        IntPtr petWindow)
    {
        _from = from;
        _to = to;
        _petWindow = petWindow;
        _durationSeconds = Math.Clamp(0.55 + (to - from).Length / 550, 0.65, 1.4);
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Topmost = false;
        ResizeMode = ResizeMode.NoResize;
        Width = CopyWindowSize;
        Height = CopyWindowSize;
        Opacity = 0;
        Content = _iconImage = new Image
        {
            Source = icon,
            Width = CopyImageSize,
            Height = CopyImageSize,
            Stretch = Stretch.Uniform,
            IsHitTestVisible = false
        };
        RenderOptions.SetBitmapScalingMode(_iconImage, BitmapScalingMode.HighQuality);

        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            if (_handle == IntPtr.Zero ||
                !DesktopWindowLayer.TryMakeIndependent(_handle,
                    noActivate: true))
                return;
            int style = GetWindowLongW(_handle, GWL_EXSTYLE);
            SetWindowLongW(_handle, GWL_EXSTYLE,
                style | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            _layerReady = (GetWindowLongW(_handle, GWL_EXSTYLE) &
                (WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) ==
                (WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            UpdateDpiSize();
        };
        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _timer.Tick += OnTick;
        Loaded += (_, _) =>
        {
            // A batch of swallowed icons is prepared before any copy is shown.
            // Start each flight on display so earlier copies do not expire
            // while later windows are still being constructed.
            _started = DateTime.UtcNow;
            OnTick(null, EventArgs.Empty);
            if (IsVisible) _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (!_layerReady || _handle == IntPtr.Zero)
        {
            Close();
            return;
        }
        UpdateDpiSize();
        double p = Math.Clamp((DateTime.UtcNow - _started).TotalSeconds / _durationSeconds, 0, 1);
        double smooth = p * p * (3 - 2 * p);
        double x = _from.X + (_to.X - _from.X) * smooth;
        double y = _from.Y + (_to.Y - _from.Y) * smooth - 44 * Math.Sin(Math.PI * p);
        if (!SetWindowPos(_handle, IntPtr.Zero,
                (int)Math.Round(x - CopyWindowSize / 2.0),
                (int)Math.Round(y - CopyWindowSize / 2.0),
                CopyWindowSize, CopyWindowSize,
                SWP_NOZORDER | SWP_NOACTIVATE) ||
            // Several copies can fly at once. Reordering each one above the
            // pet every frame makes them trade places dozens of times a second.
            // Their movement above keeps the current Z order unchanged.
            (DesktopWindowLayer.IsTopmost(_handle) ||
             (!DesktopWindowLayer.IsAbove(_handle, _petWindow) &&
              !DesktopWindowLayer.TryPlaceJustAbove(_handle, _petWindow))))
        {
            // The original icon becomes visible as soon as Closed releases its
            // cover. Never show a flying copy in an unverified window band.
            Opacity = 0;
            Close();
            return;
        }
        Opacity = Math.Min(1, p * 8) * Math.Min(1, (1 - p) * 12);
        if (p >= 1) Close();
    }

    private void UpdateDpiSize()
    {
        if (_handle == IntPtr.Zero) return;
        uint dpi = GetDpiForWindow(_handle);
        if (dpi == _lastDpi) return;
        _lastDpi = dpi;
        double dipPerPixel = dpi == 0 ? 1 : 96.0 / dpi;
        Width = CopyWindowSize * dipPerPixel;
        Height = CopyWindowSize * dipPerPixel;
        _iconImage.Width = CopyImageSize * dipPerPixel;
        _iconImage.Height = CopyImageSize * dipPerPixel;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", ExactSpelling = true)]
    private static extern int GetWindowLongW(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", ExactSpelling = true)]
    private static extern int SetWindowLongW(IntPtr window, int index, int value);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter,
        int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(IntPtr window);
}
