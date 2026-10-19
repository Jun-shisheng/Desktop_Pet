using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using DeepSeekFishPet.Behavior;
using DeepSeekFishPet.Desktop;
using DeepSeekFishPet.Integrations;
using Forms = System.Windows.Forms;
using MediaBrushes = System.Windows.Media.Brushes;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace DeepSeekFishPet;

public partial class MainWindow : Window
{
    // The art keeps its original canvas so speech and props have room, while the
    // controller uses a compact body whose feet can actually rest on an icon.
    private const double VisualWindowWidth = 180;
    private const double VisualWindowHeight = 220;
    private const double VisualOffsetX = (VisualWindowWidth - PetBrain.PetWidth) / 2;
    private const double VisualOffsetY = VisualWindowHeight - PetBrain.PetHeight;
    private const int WM_DPICHANGED = 0x02E0;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    private readonly PetSettings _settings;
    private readonly PetBrain _brain;
    private readonly DesktopShortcutService _shortcuts = new();
    private readonly DesktopIconLayoutService _iconLayout = new();
    private readonly WindowWhackGame _whack = new();
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _clickTimer;
    private readonly DispatcherTimer _perchRetryTimer;
    private readonly Stopwatch _clock = new();
    private readonly Dictionary<string, BitmapSource> _sprites = new(StringComparer.OrdinalIgnoreCase);
    private readonly ScaleTransform _characterScale = new();
    private readonly RotateTransform _characterRotation = new();
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly Forms.ContextMenuStrip _trayMenu;

    private DesktopShortcut? _heldShortcut;
    private ImageSource? _eatenIconImage;
    private readonly Dictionary<string, StoredIconSnack> _storedIconSnacks =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ReturningIconSnack> _returningIcons = new();
    private BitmapSource? _peekSprite;
    private IReadOnlyList<PeekWindow> _windowCandidates = Array.Empty<PeekWindow>();
    private IReadOnlyList<IconPlatform> _lastIconPlatforms = Array.Empty<IconPlatform>();
    private DateTime _lastNonEmptyIconSnapshotAt;
    private WindowWhackTarget? _ambientPeekTarget;
    private Rect _activityBounds;
    private bool _hasShortcuts;
    private bool _hasIconPlatforms;
    private double _animationSeconds;
    private double _iconLayoutSeconds;
    private double _windowPlatformSeconds;
    private double _windowScanSeconds;
    private double _desktopLayerSeconds;
    private double _iconJumpInactiveSeconds;
    private bool _desktopLayerDuringFall;
    private bool _wasDesktopLayer;
    private bool _wasWindowLayer;
    private double _ambientIdleSeconds;
    private double _ambientNextPeekSeconds = NextAmbientDelaySeconds();
    private double _ambientPeekSecondsRemaining;
    private double _ambientWindowScanSeconds;
    private double _deepSeekPollSeconds;
    private DeepSeekActivity? _deepSeekActivity;
    private DeepSeekQuota? _deepSeekQuota;
    private PetPose? _lastPose;
    private IntPtr _hwnd;
    private bool _pointerDown;
    private bool _dragging;
    private int _perchRetryCount;
    private int _shakeDirection;
    private int _shakeAxis;
    private int _shakeReversals;
    private DateTime _lastShakeStepAt;
    private POINT _pressPoint;
    private POINT _dragOffset;
    private POINT _shakeAnchor;
    private string _temporarySpeech = "";
    private DateTime _temporarySpeechUntil;
    private string _lastSpriteKey = "";
    private int _lastWindowX = int.MinValue;
    private int _lastWindowY = int.MinValue;

    private sealed class StoredIconSnack
    {
        public StoredIconSnack(IconPlatform source, ImageSource image,
            DesktopIconVisualMask mask)
        {
            Source = source;
            Image = image;
            Mask = mask;
        }

        public IconPlatform Source { get; set; }
        public ImageSource Image { get; }
        public DesktopIconVisualMask Mask { get; set; }
    }

    private sealed class ReturningIconSnack
    {
        public ReturningIconSnack(IconPlatform source, DesktopIconVisualMask mask,
            IconReturnWindow window)
        {
            Source = source;
            Mask = mask;
            Window = window;
        }

        public IconPlatform Source { get; }
        public DesktopIconVisualMask Mask { get; }
        public IconReturnWindow Window { get; }
    }

    public MainWindow()
    {
        InitializeComponent();
        PetHitArea.Width = PetBrain.PetWidth;
        PetHitArea.Height = PetBrain.PetHeight;
        _settings = PetSettings.Load();
        if (_settings.Mode == PetMode.WindowWhack)
            _settings.Mode = PetMode.Life; // A timed round starts only from the menu.
        Topmost = _settings.AlwaysOnTop;

        _activityBounds = GetActivityBounds(Forms.Screen.PrimaryScreen);
        _hasShortcuts = _shortcuts.GetShortcuts().Count > 0;
        _brain = new PetBrain(_activityBounds)
        {
            IsPaused = _settings.IsPaused,
            AllowShortcutPlay = _settings.AllowShortcutPlay && _hasShortcuts,
            UseDailyRoutine = _settings.UseDailyRoutine,
            AllowChatter = _settings.AmbientChatter
        };
        _brain.StateChanged += OnBrainStateChanged;

        CharacterVisual.RenderTransform = new TransformGroup
        {
            Children = new TransformCollection { _characterScale, _characterRotation }
        };
        WhackVisual.Clip = new EllipseGeometry(new Point(50, 50), 43, 45);
        LoadSprites();

        var petMenu = new ContextMenu();
        petMenu.Opened += (_, _) => PopulatePetMenu(petMenu);
        RootGrid.ContextMenu = petMenu;

        _trayMenu = new Forms.ContextMenuStrip();
        _trayMenu.Opening += (_, _) => PopulateTrayMenu();
        System.Drawing.Icon trayImage;
        try
        {
            trayImage = Environment.ProcessPath is string path
                ? System.Drawing.Icon.ExtractAssociatedIcon(path) ?? System.Drawing.SystemIcons.Application
                : System.Drawing.SystemIcons.Application;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            trayImage = System.Drawing.SystemIcons.Application;
        }
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = trayImage,
            Text = "DeepSeek 大肥鱼桌宠",
            ContextMenuStrip = _trayMenu,
            Visible = true
        };
        _trayIcon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                Dispatcher.BeginInvoke(() => { SetMode(PetMode.Life); Show(); BringForward(); _brain.Trigger(PetAction.Call); });
        };

        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _timer.Tick += OnTick;

        _clickTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(Forms.SystemInformation.DoubleClickTime)
        };
        _clickTimer.Tick += (_, _) =>
        {
            _clickTimer.Stop();
            _brain.Trigger(_settings.Mode == PetMode.IconJump ? PetAction.IconHop : PetAction.Pet);
            RenderFrame(_brain.Update(0));
        };

        _perchRetryTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _perchRetryTimer.Tick += RetryPerchDrop;

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) =>
        {
            RefreshWindowPlatforms();
            RefreshIconPlatforms();
            PollDeepSeek(0, force: true);
            _clock.Restart();
            PetFrame frame = _brain.Update(0);
            RenderFrame(frame);
            UpdatePetWindowLayer(frame, force: true);
            _timer.Start();
        };
        Closed += OnClosed;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    private static Rect GetVirtualBounds()
    {
        // Physical pixels, including monitors placed to the left or above the primary display.
        return new Rect(GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
            Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN)),
            Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN)));
    }

    private static Rect GetActivityBounds(Forms.Screen? screen)
    {
        Rect virtualBounds = GetVirtualBounds();
        int workingBottom = screen?.WorkingArea.Bottom ?? (int)virtualBounds.Bottom;
        return new Rect(virtualBounds.Left, virtualBounds.Top, virtualBounds.Width,
            Math.Max(1, workingBottom - virtualBounds.Top));
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        DesktopWindowLayer.TryMakeIndependent(_hwnd);
        HwndSource.FromHwnd(_hwnd)?.AddHook(WindowProc);
        UpdateDpiLayout();
        PetFrame frame = _brain.Update(0);
        RenderFrame(frame);
        UpdatePetWindowLayer(frame, force: true);
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WM_DPICHANGED)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, UpdateDpiLayout);
        return IntPtr.Zero;
    }

    private void UpdateDpiLayout()
    {
        if (_hwnd == IntPtr.Zero) return;
        uint dpi = GetDpiForWindow(_hwnd);
        double dipPerPixel = dpi == 0 ? 1 : 96.0 / dpi;
        RootGrid.LayoutTransform = new ScaleTransform(dipPerPixel, dipPerPixel);
        bool peeking = _settings.Mode == PetMode.WindowWhack || _ambientPeekTarget.HasValue;
        RootGrid.Width = peeking ? 100 : VisualWindowWidth;
        RootGrid.Height = peeking ? 100 : VisualWindowHeight;
        Width = RootGrid.Width * dipPerPixel;
        Height = RootGrid.Height * dipPerPixel;
        _lastWindowX = int.MinValue;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            Forms.Screen screen = _hwnd != IntPtr.Zero && GetWindowRect(_hwnd, out RECT rect)
                ? Forms.Screen.FromPoint(new System.Drawing.Point(
                    (rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2))
                : Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0];
            _activityBounds = GetActivityBounds(screen);
            _brain.SetBounds(_activityBounds);
            RefreshWindowPlatforms();
            RefreshIconPlatforms();
            if (_settings.Mode == PetMode.WindowWhack)
                RenderWhackFrame();
            else
                RenderFrame(_brain.Update(0));
        });
    }

    private void OnTick(object? sender, EventArgs e)
    {
        double dt = _clock.Elapsed.TotalSeconds;
        _clock.Restart();
        _animationSeconds += dt;
        PollDeepSeek(dt);
        if (_settings.Mode == PetMode.WindowWhack)
        {
            // Entering this game while stuffed starts a short icon-return
            // animation. Keep checking its source even though life pauses.
            if (_returningIcons.Count > 0)
            {
                _iconLayoutSeconds += dt;
                if (_iconLayoutSeconds >= 0.2)
                    RefreshIconPlatforms();
            }
            TickWhack(dt);
            return;
        }
        _windowPlatformSeconds += dt;
        if (_windowPlatformSeconds >= (_brain.IsLifeWindowPerched ? 0.2 : 0.7))
            RefreshWindowPlatforms();
        _iconLayoutSeconds += dt;
        double iconRefreshSeconds = _returningIcons.Count > 0 ? 0.2 :
            _brain.IsLifePerched ? 0.5 :
            _brain.IsWaitingForBridge ? 0.8 :
            _brain.IsIconAdventureActive || _brain.EatenIconPlatform.HasValue ||
            _storedIconSnacks.Count > 0 ? 1.5 : 4;
        if (_iconLayoutSeconds >= iconRefreshSeconds)
            RefreshIconPlatforms();
        PetFrame frame = _brain.Update(_ambientPeekTarget.HasValue ? 0 : dt);
        if (_settings.Mode == PetMode.IconJump)
            _iconJumpInactiveSeconds = _brain.IsIconAdventureActive ? 0 :
                _iconJumpInactiveSeconds + Math.Min(dt, 0.1);
        else
            _iconJumpInactiveSeconds = 0;
        if (_settings.Mode == PetMode.IconJump &&
            (_brain.BridgeWaitTimedOut || _iconJumpInactiveSeconds >= 30))
        {
            SetMode(PetMode.Life);
            SayTemporarily("等桥太久，先回生活模式休息啦", 3);
            RenderFrame(_brain.Update(0));
            return;
        }
        Forms.Screen screen = Forms.Screen.FromPoint(new System.Drawing.Point(
            (int)(frame.X + PetBrain.PetWidth / 2), (int)(frame.Y + PetBrain.PetHeight / 2)));
        Rect activityBounds = GetActivityBounds(screen);
        if (Math.Abs(activityBounds.Bottom - _activityBounds.Bottom) > 1)
        {
            _activityBounds = activityBounds;
            _brain.SetBounds(activityBounds);
            frame = _brain.Update(0);
        }
        TickAmbientPeek(dt);
        RenderFrame(frame);
        UpdatePetWindowLayer(frame, dt);
    }

    private void PollDeepSeek(double dt, bool force = false)
    {
        _deepSeekPollSeconds += Math.Max(0, dt);
        if (!force && _deepSeekPollSeconds < 0.75)
            return;
        _deepSeekPollSeconds = 0;
        _deepSeekActivity = _settings.FollowDeepSeekActivity
            ? DeepSeekActivitySource.ReadActivity() : null;
        _deepSeekQuota = _settings.ShowDeepSeekBalance
            ? DeepSeekActivitySource.ReadQuota() : null;
        UpdateDeepSeekBalanceBadge();
    }

    private void UpdateDeepSeekBalanceBadge()
    {
        bool show = _settings.ShowDeepSeekBalance &&
            _settings.Mode == PetMode.Life && !_ambientPeekTarget.HasValue &&
            _deepSeekQuota is { } quota && !string.IsNullOrWhiteSpace(quota.Text);
        DeepSeekBalanceBadge.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show || _deepSeekQuota is not { } balance) return;
        DeepSeekBalanceText.Text = balance.Text;
        DeepSeekBalanceText.Foreground = balance.Low ? MediaBrushes.DarkRed : MediaBrushes.DarkSlateBlue;
        DeepSeekBalanceBadge.Background = balance.Low ? MediaBrushes.MistyRose : MediaBrushes.AliceBlue;
    }

    private DeepSeekActivityDisplay? CurrentDeepSeekDisplay(PetFrame frame)
    {
        if (!_settings.FollowDeepSeekActivity || _settings.IsPaused ||
            _settings.Mode != PetMode.Life || _dragging ||
            _ambientPeekTarget.HasValue || _brain.IsLifePerched ||
            _brain.IsLifeWindowPerched || _brain.IsIconAdventureActive ||
            _brain.EatenIconPlatform.HasValue || _storedIconSnacks.Count > 0 ||
            _returningIcons.Count > 0 || frame.Prop != PetProp.None)
            return null;
        return DeepSeekActivityPresentation.Map(_deepSeekActivity,
            DateTimeOffset.UtcNow);
    }

    private void UpdatePetWindowLayer(PetFrame frame, double dt = 0, bool force = false)
    {
        if (_hwnd == IntPtr.Zero) return;
        bool hasIconCovers = _storedIconSnacks.Count > 0 ||
            _returningIcons.Count > 0;
        bool windowPerched = _brain.IsLifeWindowPerched;
        bool iconInteraction = _brain.IsLifePerched || _brain.CurrentState is
            PetState.IconHungry or PetState.IconSeekSnack or PetState.IconNibble or
            PetState.IconEat or PetState.IconStuffed or PetState.IconSpit;
        if (iconInteraction)
            _desktopLayerDuringFall = true;
        else if (windowPerched)
            _desktopLayerDuringFall = false;
        else if (_wasWindowLayer &&
                 frame.Y < _activityBounds.Bottom - PetBrain.PetHeight - 2)
            _desktopLayerDuringFall = true;
        else if (frame.Y >= _activityBounds.Bottom - PetBrain.PetHeight - 2)
            _desktopLayerDuringFall = false;

        bool atWindow = !hasIconCovers && !_dragging &&
            !_ambientPeekTarget.HasValue && _settings.Mode == PetMode.Life &&
            windowPerched;
        bool atDesktop = !_dragging && !_ambientPeekTarget.HasValue &&
            _settings.Mode != PetMode.WindowWhack && !atWindow &&
            (_settings.Mode == PetMode.IconJump || iconInteraction ||
             _desktopLayerDuringFall || hasIconCovers);
        bool shouldBeTopmost = !hasIconCovers &&
            (_settings.Mode == PetMode.WindowWhack ||
             _ambientPeekTarget.HasValue ||
             (!atDesktop && !atWindow && _settings.AlwaysOnTop));
        if (Topmost != shouldBeTopmost)
        {
            Topmost = shouldBeTopmost;
            force = true;
        }
        if (!atDesktop && !atWindow)
        {
            _desktopLayerSeconds = 0;
            _wasDesktopLayer = false;
            _wasWindowLayer = false;
            MaintainEatenIconLayer();
            return;
        }
        _desktopLayerSeconds += dt;
        if (atWindow)
        {
            if (force || !_wasWindowLayer || _desktopLayerSeconds >= 0.2)
            {
                DesktopWindowLayer.TryPlaceAboveWindow(_hwnd,
                    _brain.LifePerchedWindowHandle);
                _desktopLayerSeconds = 0;
            }
            _wasWindowLayer = true;
            _wasDesktopLayer = false;
            MaintainEatenIconLayer();
            return;
        }
        _wasWindowLayer = false;
        bool refreshDesktopLayer = force || !_wasDesktopLayer ||
            _desktopLayerSeconds >= 0.5;
        if (refreshDesktopLayer)
        {
            // The mask must go transparent before any reordering can put it
            // over the pet. Let its own routine place both windows together.
            if (!hasIconCovers)
                DesktopWindowLayer.TryPlaceAboveDesktop(_hwnd);
            _desktopLayerSeconds = 0;
        }
        _wasDesktopLayer = true;
        MaintainEatenIconLayer();
    }

    private void MaintainEatenIconLayer()
    {
        if (_storedIconSnacks.Count == 0 && _returningIcons.Count == 0)
            return;
        List<DesktopIconVisualMask> masks = AllIconMasks();
        if (DesktopIconVisualMask.TryKeepBelowPet(masks, _hwnd)) return;

        // A failed group reorder may leave one cover above the pet. Removing all
        // covers restores their real Explorer icons without moving any files.
        ReleaseAllIconVisuals();
        _eatenIconImage = null;
        if (_brain.EatenIconPlatform.HasValue)
            _brain.Trigger(PetAction.AbortIconSnack);
    }

    private void RenderFrame(PetFrame frame)
    {
        if (_settings.Mode == PetMode.WindowWhack)
        {
            RenderWhackFrame();
            return;
        }
        if (_ambientPeekTarget.HasValue)
        {
            RenderAmbientPeekFrame();
            return;
        }
        CharacterVisual.RenderTransformOrigin = new Point(0.5, 1);
        PetHitArea.Visibility = Visibility.Visible;
        int windowX = (int)Math.Round(frame.X - VisualOffsetX);
        int windowY = (int)Math.Round(frame.Y - VisualOffsetY);
        if (_hwnd != IntPtr.Zero && (windowX != _lastWindowX || windowY != _lastWindowY))
        {
            SetWindowPos(_hwnd, IntPtr.Zero, windowX, windowY,
                0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            _lastWindowX = windowX;
            _lastWindowY = windowY;
        }

        _characterScale.ScaleY = frame.Scale;
        _characterRotation.Angle = frame.Rotation;
        CharacterVisual.Opacity = Math.Clamp(frame.Opacity, 0, 1);
        UpdateDeepSeekBalanceBadge();
        DeepSeekActivityDisplay? deepSeekDisplay = CurrentDeepSeekDisplay(frame);

        if (_lastPose != frame.Pose)
        {
            _lastPose = frame.Pose;
            _animationSeconds = 0;
        }
        string spriteKey = PoseToSprite(frame.Pose);
        if (_brain.CurrentState == PetState.Nap && _sprites.ContainsKey("sleep_blanket"))
            spriteKey = "sleep_blanket";
        string? iconSprite = _brain.CurrentState switch
        {
            PetState.IconNibble => "nibble",
            PetState.IconEat => "icon_eat",
            PetState.IconStuffed => "stuffed",
            PetState.IconSpit => "icon_spit",
            PetState.IconClimb or PetState.IconHang => "icon_climb",
            PetState.IconLand or PetState.PlaneLand => "icon_land",
            PetState.IconWaitForBridge or PetState.PlaneFold
                when frame.Pose == PetPose.Climb => "icon_climb",
            PetState.PlaneFold => "fold_plane",
            PetState.PlaneTakeoff or PetState.PlaneFlight => "paper_plane",
            _ => null
        };
        if (iconSprite is not null && _sprites.ContainsKey(iconSprite))
            spriteKey = iconSprite;
        if (frame.Pose is PetPose.Walk or PetPose.Tiptoe &&
            _sprites.ContainsKey("walk_alt") && (int)(_animationSeconds * 4) % 2 == 1)
            spriteKey = "walk_alt";
        else if (frame.Pose == PetPose.Run &&
            _sprites.ContainsKey("run_alt") && (int)(_animationSeconds * 8) % 2 == 1)
            spriteKey = "run_alt";
        if (deepSeekDisplay?.SpriteKey is string workSprite &&
            frame.Pose is (PetPose.Stand or PetPose.Sit or PetPose.Read) &&
            _sprites.ContainsKey(workSprite))
            spriteKey = workSprite;
        // All whale poses share one forward/right-facing source direction.
        _characterScale.ScaleX = frame.Flip ? -frame.Scale : frame.Scale;
        if (_lastSpriteKey != spriteKey)
        {
            _lastSpriteKey = spriteKey;
            if (!_sprites.TryGetValue(spriteKey, out BitmapSource? sprite))
                _sprites.TryGetValue("idle", out sprite);
            SpriteImage.Source = sprite;
            SpriteImage.Visibility = sprite is null ? Visibility.Collapsed : Visibility.Visible;
            FallbackArt.Visibility = sprite is null ? Visibility.Visible : Visibility.Collapsed;
        }

        bool eatingIcon = _brain.CurrentState is PetState.IconNibble or PetState.IconEat;
        if (eatingIcon && _eatenIconImage is not null)
        {
            double size = _brain.CurrentState == PetState.IconEat
                ? Math.Max(0, 48 * (1 - frame.Progress)) : 48;
            ShortcutImage.Source = _eatenIconImage;
            ShortcutImage.Width = size;
            ShortcutImage.Height = size;
            ShortcutImage.Margin = new Thickness(frame.Flip ? 28 : -28, 0, 0, 45);
            // Once the copy has reached the mouth, hide it instead of leaving
            // a permanent six-pixel speck on the desktop.
            ShortcutImage.Visibility = size >= 8
                ? Visibility.Visible : Visibility.Collapsed;
            PropGlyph.Visibility = Visibility.Collapsed;
        }
        else if (frame.Prop == PetProp.Shortcut && _heldShortcut is not null)
        {
            ShortcutImage.Source = _heldShortcut.Icon;
            ShortcutImage.Width = 38;
            ShortcutImage.Height = 38;
            ShortcutImage.Margin = new Thickness(0, 0, 0, 35);
            ShortcutImage.Visibility = Visibility.Visible;
            PropGlyph.Visibility = Visibility.Collapsed;
        }
        else
        {
            ShortcutImage.Visibility = Visibility.Collapsed;
            bool propIsInArtwork = SpriteImage.Source is not null &&
                (frame.Pose == PetPose.Sword && frame.Prop == PetProp.Sword ||
                 frame.Pose == PetPose.Fish && frame.Prop == PetProp.FishingRod ||
                 frame.Pose == PetPose.Read && frame.Prop == PetProp.Book ||
                 frame.Pose == PetPose.Drink && frame.Prop == PetProp.TeaCup ||
                 frame.Pose == PetPose.Sleep && frame.Prop == PetProp.Pillow ||
                 spriteKey == "paper_plane" && frame.Prop == PetProp.PaperPlane);
            string glyph = propIsInArtwork ? "" : PropToGlyph(frame.Prop);
            PropGlyph.Text = glyph;
            PropGlyph.Visibility = glyph.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        string speech = _temporarySpeechUntil > DateTime.UtcNow
            ? _temporarySpeech : (deepSeekDisplay?.Text ?? frame.Speech);
        SpeechText.Text = speech;
        SpeechBubble.Visibility = _settings.ShowBubbles && !string.IsNullOrWhiteSpace(speech)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnBrainStateChanged(PetState state)
    {
        if (state == PetState.IconNibble)
        {
            if (_brain.EatenIconPlatform is IconPlatform snack)
            {
                _eatenIconImage = _iconLayout.GetIcon(snack.Id);
                if (_eatenIconImage is null)
                    IconSnackDiagnostics.Failure("nibble.icon-image-missing",
                        _hwnd, snack.Bounds);
            }
            else
            {
                _eatenIconImage = null;
                IconSnackDiagnostics.Failure("nibble.platform-missing", _hwnd);
            }
        }
        else if (state == PetState.IconEat)
        {
            bool hidden;
            if (_eatenIconImage is null)
            {
                IconSnackDiagnostics.Failure("eat.icon-image-missing", _hwnd);
                hidden = false;
            }
            else if (_brain.EatenIconPlatform is not IconPlatform swallowed)
            {
                IconSnackDiagnostics.Failure("eat.platform-missing", _hwnd);
                hidden = false;
            }
            else
            {
                DesktopIconVisualMask? mask = null;
                hidden = !_storedIconSnacks.ContainsKey(swallowed.Id) &&
                    TryHideEatenIcon(swallowed, out mask);
                if (hidden && mask is not null)
                {
                    _storedIconSnacks.Add(swallowed.Id,
                        new StoredIconSnack(swallowed, _eatenIconImage, mask));
                    SyncStoredSnackIds();
                    // The swallowed source is no longer a visible platform.
                    // A window edge must not support a pet kept in the low
                    // desktop layer while the wallpaper covers exist.
                    _brain.SetIconPlatforms(VisibleIconPlatforms(
                        _lastIconPlatforms));
                    RefreshWindowPlatforms();
                }
            }
            if (!hidden)
            {
                // Only finish swallowing when the original icon can also be
                // visually covered; otherwise it would appear in two places.
                _eatenIconImage = null;
                _brain.Trigger(PetAction.AbortIconSnack);
                SayTemporarily("暂时盖不住这个图标，稍后再试", 2.5);
            }
        }
        else if (state == PetState.IconSpit)
        {
            ReturnAllStoredIcons();
            _eatenIconImage = null;
        }
        else if (state is not PetState.IconNibble &&
                 !_brain.EatenIconPlatform.HasValue)
        {
            // Digestion ends the active bite. Older copies stay in the ledger.
            _eatenIconImage = null;
        }
        if (state == PetState.PickShortcut)
        {
            _shortcuts.Refresh();
            _heldShortcut = _shortcuts.GetRandomShortcut();
            if (_heldShortcut is null)
            {
                _hasShortcuts = false;
                _brain.AllowShortcutPlay = false;
                SayTemporarily("桌面上没有可玩的快捷方式", 2.5);
            }
        }
        else if (state is not (PetState.CarryShortcut or PetState.InspectShortcut or PetState.ReturnShortcut))
        {
            _heldShortcut = null;
        }
    }

    private bool TryHideEatenIcon(IconPlatform icon,
        out DesktopIconVisualMask? newMask)
    {
        newMask = null;
        if (_hwnd == IntPtr.Zero)
        {
            IconSnackDiagnostics.Failure("hide.pet-hwnd-missing", _hwnd,
                icon.Bounds);
            return false;
        }
        // A swallowed icon belongs to the desktop layer, including the pet.
        // Make both windows ordinary before calculating their relative order.
        if (Topmost)
            Topmost = false;
        if (!_iconLayout.TryGetVisualBounds(icon.Id, out Rect visualBounds))
        {
            IconSnackDiagnostics.Failure("hide.visual-bounds-failed", _hwnd,
                icon.Bounds);
            return false;
        }
        if (!DesktopIconVisualMask.TryShow(icon, visualBounds, out newMask))
        {
            IconSnackDiagnostics.Failure("hide.mask-show-failed", _hwnd,
                visualBounds);
            return false;
        }

        // Showing a WPF cover can change Z order after the ordinary desktop
        // placement call. Verify the actual HWND order rather than assuming it.
        List<DesktopIconVisualMask> masks = AllIconMasks();
        if (newMask is DesktopIconVisualMask mask)
            masks.Add(mask);
        if (newMask is null ||
            !DesktopIconVisualMask.TryKeepBelowPet(masks, _hwnd))
        {
            IconSnackDiagnostics.Failure("hide.mask-order-failed", _hwnd,
                visualBounds);
            newMask?.Dispose();
            newMask = null;
            if (masks.Count > 1 &&
                !DesktopIconVisualMask.TryKeepBelowPet(AllIconMasks(), _hwnd))
                ReleaseAllIconVisuals();
            return false;
        }
        return true;
    }

    private List<DesktopIconVisualMask> AllIconMasks()
    {
        var masks = new List<DesktopIconVisualMask>(
            _storedIconSnacks.Count + _returningIcons.Count);
        masks.AddRange(_storedIconSnacks.Values.Select(snack => snack.Mask));
        masks.AddRange(_returningIcons.Select(returning => returning.Mask));
        return masks;
    }

    private IReadOnlyList<IconPlatform> VisibleIconPlatforms(
        IReadOnlyList<IconPlatform> platforms)
    {
        if (_storedIconSnacks.Count == 0 && _returningIcons.Count == 0)
            return platforms;
        var hidden = new HashSet<string>(_storedIconSnacks.Keys,
            StringComparer.OrdinalIgnoreCase);
        foreach (ReturningIconSnack returning in _returningIcons)
            hidden.Add(returning.Source.Id);
        return platforms.Where(platform => !hidden.Contains(platform.Id))
            .ToArray();
    }

    private void SyncStoredSnackIds() =>
        _brain.SetStoredSnackIconIds(_storedIconSnacks.Keys);

    private void ReturnAllStoredIcons()
    {
        if (_storedIconSnacks.Count == 0) return;
        Dictionary<string, IconPlatform> live = new(
            StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (IconPlatform platform in _iconLayout.GetPlatforms())
                live[platform.Id] = platform;
        }
        catch (Exception ex)
        {
            IconSnackDiagnostics.Failure("return.layout-failed", _hwnd,
                errorCode: ex.HResult);
            ReleaseAllIconVisuals();
            return;
        }

        // A shortcut can be moved by Explorer just before the shake. Rebuild
        // its cover at the live destination while every other cover remains in
        // the group, then animate the copy back to that destination.
        foreach (StoredIconSnack stored in _storedIconSnacks.Values.ToArray())
        {
            if (!_storedIconSnacks.TryGetValue(stored.Source.Id,
                    out StoredIconSnack? stillStored) ||
                !ReferenceEquals(stillStored, stored))
                continue;
            if (!live.TryGetValue(stored.Source.Id, out IconPlatform current))
            {
                _storedIconSnacks.Remove(stored.Source.Id);
                SafeDisposeMask(stored.Mask);
                continue;
            }
            if (MaskMatchesLiveIcon(stored.Mask, current))
            {
                stored.Source = current;
                continue;
            }
            _storedIconSnacks.Remove(stored.Source.Id);
            SafeDisposeMask(stored.Mask);
            if (TryHideEatenIcon(current, out DesktopIconVisualMask? movedMask) &&
                movedMask is not null)
            {
                stored.Source = current;
                stored.Mask = movedMask;
                _storedIconSnacks.Add(current.Id, stored);
            }
        }

        StoredIconSnack[] snacks = _storedIconSnacks.Values.ToArray();
        _storedIconSnacks.Clear();
        SyncStoredSnackIds();
        var preparedReturns = new List<ReturningIconSnack>(snacks.Length);
        foreach (StoredIconSnack snack in snacks)
        {
            IconReturnWindow? window = null;
            ReturningIconSnack? returning = null;
            try
            {
                if (!live.TryGetValue(snack.Source.Id,
                        out IconPlatform destinationIcon) ||
                    !MaskMatchesLiveIcon(snack.Mask, destinationIcon) ||
                    _lastWindowX == int.MinValue ||
                    _lastWindowY == int.MinValue)
                {
                    SafeDisposeMask(snack.Mask);
                    continue;
                }

                Point mouth = new(_lastWindowX + VisualWindowWidth / 2,
                    _lastWindowY + VisualWindowHeight - 20);
                Point destination = new(destinationIcon.Bounds.Left +
                    destinationIcon.Bounds.Width / 2,
                    destinationIcon.Bounds.Top + destinationIcon.Bounds.Height / 3);
                window = new IconReturnWindow(snack.Image, mouth, destination,
                    _hwnd);
                returning = new ReturningIconSnack(destinationIcon, snack.Mask,
                    window);
                _returningIcons.Add(returning);
                ReturningIconSnack finished = returning;
                window.Closed += (_, _) => FinishReturningIcon(finished);
                preparedReturns.Add(returning);
            }
            catch (Exception ex)
            {
                IconSnackDiagnostics.Failure("return.animation-failed", _hwnd,
                    errorCode: ex.HResult);
                if (returning is not null)
                    _returningIcons.Remove(returning);
                if (window is not null)
                {
                    try { window.Close(); }
                    catch (Exception closeEx)
                    {
                        IconSnackDiagnostics.Failure("return.close-failed",
                            _hwnd, errorCode: closeEx.HResult);
                    }
                }
                SafeDisposeMask(snack.Mask);
            }
        }

        // Register every cover before showing any return window. WPF Show can
        // synchronously run window callbacks; the group must include even the
        // copies whose animations have not started yet.
        MaintainEatenIconLayer();
        foreach (ReturningIconSnack returning in preparedReturns)
        {
            if (!_returningIcons.Contains(returning)) continue;
            try
            {
                returning.Window.Show();
                MaintainEatenIconLayer();
            }
            catch (Exception ex)
            {
                IconSnackDiagnostics.Failure("return.show-failed", _hwnd,
                    errorCode: ex.HResult);
                _returningIcons.Remove(returning);
                try { returning.Window.Close(); }
                catch (Exception closeEx)
                {
                    IconSnackDiagnostics.Failure("return.close-failed", _hwnd,
                        errorCode: closeEx.HResult);
                }
                SafeDisposeMask(returning.Mask);
            }
        }
        if (_returningIcons.Count == 0)
        {
            _iconLayoutSeconds = 4;
            _windowPlatformSeconds = 0.7;
        }
    }

    private void FinishReturningIcon(ReturningIconSnack returning)
    {
        bool removed = _returningIcons.Remove(returning);
        SafeDisposeMask(returning.Mask);
        if (removed && _returningIcons.Count == 0 &&
            _storedIconSnacks.Count == 0)
        {
            // The next frame can offer real icon and window platforms again.
            _iconLayoutSeconds = 4;
            _windowPlatformSeconds = 0.7;
        }
    }

    private void ReleaseAllIconVisuals()
    {
        ReturningIconSnack[] returning = _returningIcons.ToArray();
        StoredIconSnack[] stored = _storedIconSnacks.Values.ToArray();
        _returningIcons.Clear();
        _storedIconSnacks.Clear();
        SyncStoredSnackIds();
        foreach (ReturningIconSnack item in returning)
        {
            try { item.Window.Close(); }
            catch (Exception ex)
            {
                IconSnackDiagnostics.Failure("cleanup.return-window-failed",
                    _hwnd, errorCode: ex.HResult);
            }
            SafeDisposeMask(item.Mask);
        }
        foreach (StoredIconSnack item in stored)
            SafeDisposeMask(item.Mask);
        _iconLayoutSeconds = 4;
        _windowPlatformSeconds = 0.7;
    }

    private void SafeDisposeMask(DesktopIconVisualMask mask)
    {
        try { mask.Dispose(); }
        catch (Exception ex)
        {
            IconSnackDiagnostics.Failure("cleanup.mask-failed", _hwnd,
                errorCode: ex.HResult);
        }
    }

    private bool MaskMatchesLiveIcon(DesktopIconVisualMask mask, IconPlatform icon)
    {
        Rect source = mask.SourcePlatformBounds;
        if (Math.Abs(source.Left - icon.Bounds.Left) > 2 ||
            Math.Abs(source.Top - icon.Bounds.Top) > 2 ||
            Math.Abs(source.Width - icon.Bounds.Width) > 2 ||
            Math.Abs(source.Height - icon.Bounds.Height) > 2 ||
            !_iconLayout.TryGetVisualBounds(icon.Id, out Rect visual))
            return false;

        const double fringe = 3;
        Rect expected = new(Math.Floor(visual.Left - fringe),
            Math.Floor(visual.Top - fringe),
            Math.Ceiling(visual.Right + fringe) - Math.Floor(visual.Left - fringe),
            Math.Ceiling(visual.Bottom + fringe) - Math.Floor(visual.Top - fringe));
        Rect covered = mask.ScreenBounds;
        return Math.Abs(expected.Left - covered.Left) <= 1 &&
            Math.Abs(expected.Top - covered.Top) <= 1 &&
            Math.Abs(expected.Right - covered.Right) <= 1 &&
            Math.Abs(expected.Bottom - covered.Bottom) <= 1;
    }

    private void SayTemporarily(string message, double seconds)
    {
        _temporarySpeech = message;
        _temporarySpeechUntil = DateTime.UtcNow.AddSeconds(seconds);
    }

    private void LoadSprites()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        foreach (string resourceName in assembly.GetManifestResourceNames())
        {
            const string marker = ".assets.characters.whale.";
            int index = resourceName.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0 || !resourceName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                continue;
            string key = resourceName[(index + marker.Length)..^4];
            try
            {
                using Stream? stream = assembly.GetManifestResourceStream(resourceName);
                if (stream is null) continue;
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelHeight = 384;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                _sprites[key] = image;
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException)
            {
                // A bad optional image falls back to the vector character.
            }
        }
        if (_sprites.TryGetValue("idle", out BitmapSource? idle))
        {
            int x = (int)(idle.PixelWidth * 0.13);
            int y = (int)(idle.PixelHeight * 0.06);
            int width = Math.Min((int)(idle.PixelWidth * 0.75), idle.PixelWidth - x);
            int height = Math.Min((int)(idle.PixelHeight * 0.69), idle.PixelHeight - y);
            var head = new CroppedBitmap(idle, new Int32Rect(x, y, width, height));
            head.Freeze();
            _peekSprite = head;
        }
        WhackImage.Source = _sprites.GetValueOrDefault("peek") ?? _peekSprite;
    }

    private static string PoseToSprite(PetPose pose) => pose switch
    {
        PetPose.Walk or PetPose.Tiptoe => "walk",
        PetPose.Run or PetPose.Climb => "run",
        PetPose.Jump or PetPose.Fall => "jump",
        PetPose.Sit or PetPose.Lie => "sit",
        PetPose.Read => "read",
        PetPose.Drink => "tea",
        PetPose.Fish => "fish",
        PetPose.Sleep => "sleep",
        PetPose.Wave or PetPose.Cheer or PetPose.Spin => "cheer",
        PetPose.Sword => "attack",
        PetPose.Carry => "carry",
        _ => "idle"
    };

    private static string PropToGlyph(PetProp prop) => prop switch
    {
        PetProp.Sword => "🍚",
        PetProp.FishingRod => "🎣",
        PetProp.Fish => "🐟",
        PetProp.TeaCup => "🍵",
        PetProp.Book => "📖",
        PetProp.Shortcut => "▣",
        PetProp.Pillow => "☁",
        PetProp.Sparkle => "✦",
        PetProp.Question => "?",
        PetProp.MusicNote => "♫",
        PetProp.Heart => "♥",
        PetProp.PaperPlane => "✈",
        PetProp.Broom => "🧹",
        PetProp.Cloth => "🧽",
        PetProp.Bubble => "○",
        PetProp.Ball => "⚽",
        PetProp.Rice => "🍙",
        PetProp.Phone => "📱",
        _ => ""
    };

    private void Pet_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        FinishPendingPerchDrop();
        if (_settings.Mode == PetMode.WindowWhack)
        {
            if (!_settings.IsPaused && _whack.Hit()) RenderWhackFrame();
            e.Handled = true;
            return;
        }
        if (_ambientPeekTarget.HasValue)
        {
            EndAmbientPeek();
            e.Handled = true;
            return;
        }
        GetCursorPos(out _pressPoint);
        if (_hwnd != IntPtr.Zero && GetWindowRect(_hwnd, out RECT rect))
            _dragOffset = new POINT(_pressPoint.X - rect.Left, _pressPoint.Y - rect.Top);
        else
            _dragOffset = new POINT(0, 0);
        _pointerDown = true;
        _dragging = false;
        RootGrid.CaptureMouse();
        if (e.ClickCount >= 2)
        {
            _clickTimer.Stop();
            _brain.Trigger(_brain.IsIconAdventureActive ? PetAction.IconHop : PetAction.DoubleClick);
        }
        e.Handled = true;
    }

    private void Pet_MouseMove(object sender, MouseEventArgs e)
    {
        if (_settings.Mode == PetMode.WindowWhack) return;
        if (!_pointerDown || e.LeftButton != MouseButtonState.Pressed) return;
        GetCursorPos(out POINT cursor);
        if (!_dragging && Math.Abs(cursor.X - _pressPoint.X) + Math.Abs(cursor.Y - _pressPoint.Y) > 8)
        {
            _clickTimer.Stop();
            _dragging = true;
            _shakeAnchor = cursor;
            _shakeDirection = 0;
            _shakeAxis = 0;
            _shakeReversals = 0;
            _lastShakeStepAt = DateTime.UtcNow;
            _brain.Trigger(PetAction.DragStart);
        }
        if (_dragging)
        {
            CountShakeStep(cursor);
            _brain.PlaceAt(cursor.X - _dragOffset.X + VisualOffsetX,
                cursor.Y - _dragOffset.Y + VisualOffsetY);
            RenderFrame(_brain.Update(0));
        }
        e.Handled = true;
    }

    private void Pet_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_settings.Mode == PetMode.WindowWhack)
        {
            e.Handled = true;
            return;
        }
        if (!_pointerDown) return;
        _pointerDown = false;
        RootGrid.ReleaseMouseCapture();
        if (_dragging)
        {
            _dragging = false;
            if (_brain.IsPaused)
                SetPaused(false);
            if (_shakeReversals >= 3 &&
                (_storedIconSnacks.Count > 0 || _brain.EatenIconPlatform is not null))
                _brain.Trigger(PetAction.ShakeSpit);
            else
            {
                // Both modes can perch on an icon. Life mode keeps its own
                // walking/falling behavior; only IconJump starts the game.
                RefreshWindowPlatforms();
                RefreshIconPlatforms();
                bool perched = _brain.TryPerchOnIcon(_settings.Mode == PetMode.IconJump) ||
                    (_settings.Mode == PetMode.Life && _brain.TryPerchOnWindow());
                if (!perched)
                {
                    // Explorer may briefly expose its previous layout while a
                    // shortcut is being moved. Give the new location 0.5 s to
                    // settle before deciding that the pet missed the platform.
                    _perchRetryCount = 0;
                    _perchRetryTimer.Start();
                }
            }
            PetFrame frame = _brain.Update(0);
            RenderFrame(frame);
            UpdatePetWindowLayer(frame, force: true);
        }
        else if (e.ClickCount == 1)
        {
            _clickTimer.Stop();
            _clickTimer.Start();
        }
        e.Handled = true;
    }

    private void RetryPerchDrop(object? sender, EventArgs e)
    {
        if (_pointerDown || _dragging || _settings.Mode == PetMode.WindowWhack)
        {
            FinishPendingPerchDrop();
            return;
        }
        RefreshWindowPlatforms();
        RefreshIconPlatforms();
        if (_brain.TryPerchOnIcon(_settings.Mode == PetMode.IconJump) ||
            (_settings.Mode == PetMode.Life && _brain.TryPerchOnWindow()))
        {
            _perchRetryTimer.Stop();
            _perchRetryCount = 0;
        }
        else if (++_perchRetryCount >= 5)
            FinishPendingPerchDrop();
        PetFrame frame = _brain.Update(0);
        RenderFrame(frame);
        UpdatePetWindowLayer(frame, force: true);
    }

    private void FinishPendingPerchDrop()
    {
        if (!_perchRetryTimer.IsEnabled) return;
        _perchRetryTimer.Stop();
        _perchRetryCount = 0;
        _brain.Trigger(PetAction.DragEnd);
    }

    private void CountShakeStep(POINT cursor)
    {
        int dx = cursor.X - _shakeAnchor.X;
        int dy = cursor.Y - _shakeAnchor.Y;
        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) < 28)
            return;
        DateTime now = DateTime.UtcNow;
        if ((now - _lastShakeStepAt).TotalSeconds > 1.5)
        {
            _shakeReversals = 0;
            _shakeDirection = 0;
            _shakeAxis = 0;
        }
        int axis = Math.Abs(dx) >= Math.Abs(dy) ? 1 : 2;
        int direction = Math.Sign(axis == 1 ? dx : dy);
        if (_shakeAxis == axis && _shakeDirection != 0 && direction != _shakeDirection)
            _shakeReversals++;
        _shakeAxis = axis;
        _shakeDirection = direction;
        _shakeAnchor = cursor;
        _lastShakeStepAt = now;
    }

    private void PopulatePetMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        AddWpfAction(menu, "招呼大肥鱼", PetAction.Call);
        AddWpfAction(menu, "挥手", PetAction.Wave);
        AddWpfAction(menu, "钓鱼", PetAction.Fish);
        AddWpfAction(menu, "小憩", PetAction.Nap);
        AddWpfAction(menu, "看书", PetAction.Read);
        AddWpfAction(menu, "喝茶", PetAction.Tea);
        AddWpfAction(menu, "舀一口白饭", PetAction.Sword);
        AddWpfAction(menu, "喂白饭", PetAction.FeedRice);
        AddWpfSubmenu(menu, "更多小动作", items =>
        {
            AddWpfCommand(items, "唱歌", () => TriggerAction(PetAction.Sing));
            AddWpfCommand(items, "跳舞", () => TriggerAction(PetAction.Dance));
            AddWpfCommand(items, "玩球", () => TriggerAction(PetAction.PlayBall));
            AddWpfCommand(items, "吹泡泡", () => TriggerAction(PetAction.BlowBubbles));
            AddWpfCommand(items, "看星星", () => TriggerAction(PetAction.Stargaze));
            AddWpfCommand(items, "扫地", () => TriggerAction(PetAction.Sweep));
        });
        AddWpfCommand(menu, "跳到下一图标", TriggerIconHop);
        AddWpfCommand(menu, "折纸飞机飞过去", TriggerPaperPlane);
        AddWpfAction(menu, "和快捷方式玩耍", PetAction.Shortcut);
        AddWpfAction(menu, "欢呼", PetAction.Cheer);
        menu.Items.Add(new MenuItem { Header = StatusText(), IsEnabled = false });
        menu.Items.Add(new Separator());
        AddWpfToggle(menu, "暂停活动", _settings.IsPaused, SetPaused);
        AddWpfToggle(menu, "平时置顶（站图标时除外）", _settings.AlwaysOnTop, SetAlwaysOnTop);
        AddWpfToggle(menu, "显示对话气泡", _settings.ShowBubbles, SetShowBubbles);
        AddWpfToggle(menu, "跟随 DeepSeek 工作状态", _settings.FollowDeepSeekActivity, SetFollowDeepSeekActivity);
        AddWpfToggle(menu, "显示 DeepSeek 余额", _settings.ShowDeepSeekBalance, SetShowDeepSeekBalance);
        AddWpfToggle(menu, "允许快捷方式分身互动", _settings.AllowShortcutPlay, SetShortcutPlay);
        AddWpfToggle(menu, "偶尔从窗口边缘探头", _settings.AmbientWindowPeek, SetAmbientWindowPeek);
        AddWpfToggle(menu, "按作息时间活动", _settings.UseDailyRoutine, SetUseDailyRoutine);
        AddWpfToggle(menu, "偶尔自言自语", _settings.AmbientChatter, SetAmbientChatter);
        menu.Items.Add(new Separator());
        AddWpfMode(menu, "生活模式", PetMode.Life);
        AddWpfMode(menu, "图标跳一跳", PetMode.IconJump);
        AddWpfMode(menu, "窗口打地鼠（10 秒）", PetMode.WindowWhack);
        if (_whack.IsActive)
            menu.Items.Add(new MenuItem { Header = $"命中 {_whack.Score} · 剩余 {Math.Ceiling(_whack.TimeRemaining)} 秒", IsEnabled = false });
        menu.Items.Add(new Separator());
        AddWpfCommand(menu, "移回主屏幕", MoveToPrimaryScreen);
        AddWpfCommand(menu, "退出", Close);
    }

    private void AddWpfAction(ContextMenu menu, string title, PetAction action) =>
        AddWpfCommand(menu, title, () => TriggerAction(action));

    private static void AddWpfCommand(ContextMenu menu, string title, Action callback) =>
        AddWpfCommand(menu.Items, title, callback);

    private static void AddWpfCommand(ItemCollection items, string title, Action callback)
    {
        var item = new MenuItem { Header = title };
        item.Click += (_, _) => callback();
        items.Add(item);
    }

    private static void AddWpfSubmenu(ContextMenu menu, string title,
        Action<ItemCollection> populate)
    {
        var item = new MenuItem { Header = title };
        populate(item.Items);
        menu.Items.Add(item);
    }

    private string StatusText() =>
        $"状态：{_brain.CurrentStateName} · 饱食 {Math.Round((1 - _brain.Hunger) * 100)}%" +
        $" · 精力 {Math.Round(_brain.Energy * 100)}% · 心情 {Math.Round(_brain.Mood * 100)}%";

    private static void AddWpfToggle(ContextMenu menu, string title, bool value, Action<bool> callback)
    {
        var item = new MenuItem { Header = title, IsCheckable = true, IsChecked = value };
        item.Click += (_, _) => callback(item.IsChecked);
        menu.Items.Add(item);
    }

    private void AddWpfMode(ContextMenu menu, string title, PetMode mode)
    {
        var item = new MenuItem { Header = title, IsCheckable = true, IsChecked = _settings.Mode == mode };
        item.Click += (_, _) => SetMode(mode);
        menu.Items.Add(item);
    }

    private void PopulateTrayMenu()
    {
        _trayMenu.Items.Clear();
        AddTrayAction("招呼大肥鱼", PetAction.Call);
        AddTrayAction("钓鱼", PetAction.Fish);
        AddTrayAction("小憩", PetAction.Nap);
        AddTrayAction("看书", PetAction.Read);
        AddTrayAction("喝茶", PetAction.Tea);
        AddTrayAction("舀一口白饭", PetAction.Sword);
        AddTrayAction("喂白饭", PetAction.FeedRice);
        AddTraySubmenu("更多小动作", items =>
        {
            AddTrayCommand(items, "唱歌", () => TriggerAction(PetAction.Sing));
            AddTrayCommand(items, "跳舞", () => TriggerAction(PetAction.Dance));
            AddTrayCommand(items, "玩球", () => TriggerAction(PetAction.PlayBall));
            AddTrayCommand(items, "吹泡泡", () => TriggerAction(PetAction.BlowBubbles));
            AddTrayCommand(items, "看星星", () => TriggerAction(PetAction.Stargaze));
            AddTrayCommand(items, "扫地", () => TriggerAction(PetAction.Sweep));
        });
        AddTrayCommand("跳到下一图标", TriggerIconHop);
        AddTrayCommand("折纸飞机飞过去", TriggerPaperPlane);
        AddTrayAction("和快捷方式玩耍", PetAction.Shortcut);
        _trayMenu.Items.Add(new Forms.ToolStripMenuItem(StatusText()) { Enabled = false });
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        AddTrayToggle("暂停活动", _settings.IsPaused, SetPaused);
        AddTrayToggle("平时置顶（站图标时除外）", _settings.AlwaysOnTop, SetAlwaysOnTop);
        AddTrayToggle("显示对话气泡", _settings.ShowBubbles, SetShowBubbles);
        AddTrayToggle("跟随 DeepSeek 工作状态", _settings.FollowDeepSeekActivity, SetFollowDeepSeekActivity);
        AddTrayToggle("显示 DeepSeek 余额", _settings.ShowDeepSeekBalance, SetShowDeepSeekBalance);
        AddTrayToggle("允许快捷方式分身互动", _settings.AllowShortcutPlay, SetShortcutPlay);
        AddTrayToggle("偶尔从窗口边缘探头", _settings.AmbientWindowPeek, SetAmbientWindowPeek);
        AddTrayToggle("按作息时间活动", _settings.UseDailyRoutine, SetUseDailyRoutine);
        AddTrayToggle("偶尔自言自语", _settings.AmbientChatter, SetAmbientChatter);
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        AddTrayMode("生活模式", PetMode.Life);
        AddTrayMode("图标跳一跳", PetMode.IconJump);
        AddTrayMode("窗口打地鼠（10 秒）", PetMode.WindowWhack);
        if (_whack.IsActive)
            _trayMenu.Items.Add(new Forms.ToolStripMenuItem(
                $"命中 {_whack.Score} · 剩余 {Math.Ceiling(_whack.TimeRemaining)} 秒") { Enabled = false });
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        AddTrayCommand("移回主屏幕", MoveToPrimaryScreen);
        AddTrayCommand("退出", Close);
    }

    private void AddTrayAction(string title, PetAction action) =>
        AddTrayCommand(title, () => TriggerAction(action));

    private void AddTrayCommand(string title, Action callback) =>
        AddTrayCommand(_trayMenu.Items, title, callback);

    private void AddTrayCommand(Forms.ToolStripItemCollection items, string title,
        Action callback)
    {
        var item = new Forms.ToolStripMenuItem(title);
        item.Click += (_, _) => Dispatcher.BeginInvoke(callback);
        items.Add(item);
    }

    private void AddTraySubmenu(string title,
        Action<Forms.ToolStripItemCollection> populate)
    {
        var item = new Forms.ToolStripMenuItem(title);
        populate(item.DropDownItems);
        _trayMenu.Items.Add(item);
    }

    private void AddTrayToggle(string title, bool value, Action<bool> callback)
    {
        var item = new Forms.ToolStripMenuItem(title) { Checked = value, CheckOnClick = true };
        item.Click += (_, _) => Dispatcher.BeginInvoke(() => callback(item.Checked));
        _trayMenu.Items.Add(item);
    }

    private void AddTrayMode(string title, PetMode mode)
    {
        var item = new Forms.ToolStripMenuItem(title) { Checked = _settings.Mode == mode };
        item.Click += (_, _) => Dispatcher.BeginInvoke(() => SetMode(mode));
        _trayMenu.Items.Add(item);
    }

    private void TriggerAction(PetAction action)
    {
        FinishPendingPerchDrop();
        EndAmbientPeek(false);
        if (_settings.Mode != PetMode.Life)
            SetMode(PetMode.Life);
        if (action == PetAction.Shortcut)
        {
            if (!_settings.AllowShortcutPlay)
            {
                SayTemporarily("快捷方式互动已关闭", 2);
                return;
            }
            RefreshShortcutAvailability();
            if (!_hasShortcuts)
            {
                SayTemporarily("桌面上没有可玩的快捷方式", 2.5);
                return;
            }
        }
        if (_brain.IsPaused)
            SetPaused(false);
        _brain.Trigger(action);
        RenderFrame(_brain.Update(0));
    }

    private void RefreshShortcutAvailability()
    {
        _shortcuts.Refresh();
        _hasShortcuts = _shortcuts.GetShortcuts().Count > 0;
        _brain.AllowShortcutPlay = _settings.AllowShortcutPlay && _hasShortcuts;
    }

    private void RefreshWindowPlatforms()
    {
        _windowPlatformSeconds = 0;
        IReadOnlyList<WindowPlatform> platforms =
            _storedIconSnacks.Count > 0 || _returningIcons.Count > 0
                ? Array.Empty<WindowPlatform>()
                : TopLevelWindowService.GetPlatforms(_hwnd);
        _brain.SetWindowPlatforms(platforms);
    }

    private void RefreshIconPlatforms()
    {
        _iconLayoutSeconds = 0;
        IReadOnlyList<IconPlatform> platforms = _iconLayout.GetPlatforms();
        foreach (ReturningIconSnack returning in _returningIcons.ToArray())
        {
            // An animation must not land on a shortcut that moved or vanished.
            if (platforms.Any(platform =>
                    string.Equals(platform.Id, returning.Source.Id,
                        StringComparison.OrdinalIgnoreCase) &&
                    MaskMatchesLiveIcon(returning.Mask, platform)))
                continue;
            try { returning.Window.Close(); }
            catch (Exception ex)
            {
                IconSnackDiagnostics.Failure("return.close-failed", _hwnd,
                    errorCode: ex.HResult);
            }
            FinishReturningIcon(returning);
        }
        if (platforms.Count > 0)
        {
            _lastIconPlatforms = platforms;
            _lastNonEmptyIconSnapshotAt = DateTime.UtcNow;
        }
        else if (_lastIconPlatforms.Count > 0 &&
                 DateTime.UtcNow - _lastNonEmptyIconSnapshotAt < TimeSpan.FromSeconds(6))
        {
            // Explorer can briefly report no layout while a shortcut is being
            // dragged. Hold the last valid snapshot for a bounded interval.
            platforms = _lastIconPlatforms;
        }
        else
        {
            _lastIconPlatforms = Array.Empty<IconPlatform>();
        }
        _hasIconPlatforms = platforms.Count > 0;
        _brain.SetIconPlatforms(VisibleIconPlatforms(platforms));
        foreach (StoredIconSnack stored in _storedIconSnacks.Values.ToArray())
        {
            if (!_storedIconSnacks.TryGetValue(stored.Source.Id,
                    out StoredIconSnack? stillStored) ||
                !ReferenceEquals(stillStored, stored))
                continue;
            IconPlatform? current = platforms
                .Where(p => string.Equals(p.Id, stored.Source.Id,
                    StringComparison.OrdinalIgnoreCase))
                .Select(p => (IconPlatform?)p)
                .FirstOrDefault();
            if (current is not IconPlatform icon)
            {
                RemoveStoredIcon(stored);
                continue;
            }
            if (MaskMatchesLiveIcon(stored.Mask, icon))
            {
                stored.Source = icon;
                continue;
            }
            // Explorer's real icon moved. Drop the old wallpaper patch before
            // creating a new one at its current live coordinates.
            _storedIconSnacks.Remove(stored.Source.Id);
            SafeDisposeMask(stored.Mask);
            if (TryHideEatenIcon(icon, out DesktopIconVisualMask? movedMask) &&
                movedMask is not null)
            {
                stored.Source = icon;
                stored.Mask = movedMask;
                _storedIconSnacks.Add(icon.Id, stored);
            }
            else if (_brain.EatenIconPlatform is IconPlatform active &&
                     string.Equals(active.Id, icon.Id,
                         StringComparison.OrdinalIgnoreCase))
            {
                _eatenIconImage = null;
                _brain.Trigger(PetAction.AbortIconSnack);
            }
        }
        SyncStoredSnackIds();
        if (_settings.Mode == PetMode.IconJump && _brain.HasStandableIcon)
        {
            if (!_brain.IsIconAdventureActive)
                _brain.StartIconAdventure();
        }
        else if (_brain.IsIconAdventureActive)
        {
            _brain.StopIconAdventure();
        }
    }

    private void RemoveStoredIcon(StoredIconSnack stored)
    {
        _storedIconSnacks.Remove(stored.Source.Id);
        SafeDisposeMask(stored.Mask);
        SyncStoredSnackIds();
        if (_brain.EatenIconPlatform is IconPlatform active &&
            string.Equals(active.Id, stored.Source.Id,
                StringComparison.OrdinalIgnoreCase))
        {
            _eatenIconImage = null;
            _brain.Trigger(PetAction.AbortIconSnack);
        }
    }

    private void TriggerIconHop()
        => TriggerIconAdventureAction(PetAction.IconHop);

    private void TriggerPaperPlane()
        => TriggerIconAdventureAction(PetAction.PaperPlane);

    private void TriggerIconAdventureAction(PetAction action)
    {
        if (_settings.Mode != PetMode.IconJump)
            SetMode(PetMode.IconJump);
        RefreshIconPlatforms();
        if (!_brain.HasStandableIcon)
        {
            SayTemporarily(_hasIconPlatforms
                ? "这些图标暂时够不到，换个位置试试"
                : "桌面没有可见图标", 2.5);
            RenderFrame(_brain.Update(0));
            return;
        }
        if (_brain.IsPaused)
            SetPaused(false);
        _brain.Trigger(action);
        RenderFrame(_brain.Update(0));
    }

    private void SetMode(PetMode mode)
    {
        FinishPendingPerchDrop();
        EndAmbientPeek(false);
        if (mode == PetMode.WindowWhack)
        {
            if (_whack.IsActive) return;
            IReadOnlyList<PeekWindow> windows = TopLevelWindowService.GetCandidates(_hwnd);
            if (!_whack.Start(windows))
            {
                SayTemporarily("打开一个非全屏窗口，就能开始打地鼠", 3.5);
                RenderFrame(_brain.Update(0));
                return;
            }
            if (_settings.IsPaused)
                SetPaused(false);
            // Whack mode does not tick the life brain and uses a topmost pet.
            // End all icon snacks before entering the round.
            if (_brain.EatenIconPlatform.HasValue ||
                _storedIconSnacks.Count > 0)
                _brain.Trigger(PetAction.ShakeSpit);
            ReleaseAllIconVisuals();
            _clickTimer.Stop();
            _pointerDown = false;
            _dragging = false;
            RootGrid.ReleaseMouseCapture();
            _brain.StopIconAdventure();
            _windowCandidates = windows;
            _windowScanSeconds = 0;
            _settings.Mode = PetMode.WindowWhack;
            _settings.Save();
            Topmost = true;
            UpdateDpiLayout();
            RenderWhackFrame();
            return;
        }

        bool wasWhack = _settings.Mode == PetMode.WindowWhack;
        if (wasWhack)
            _whack.Stop();
        if (mode == PetMode.IconJump &&
            (_brain.EatenIconPlatform.HasValue || _storedIconSnacks.Count > 0))
            _brain.Trigger(PetAction.ShakeSpit);
        _settings.Mode = mode;
        _iconJumpInactiveSeconds = 0;
        ScheduleAmbientPeek();
        _settings.Save();
        WhackVisual.Visibility = Visibility.Collapsed;
        CharacterVisual.Visibility = Visibility.Visible;
        if (wasWhack) UpdateDpiLayout();
        RefreshWindowPlatforms();
        RefreshIconPlatforms();
        if (mode == PetMode.IconJump && !_brain.HasStandableIcon)
            SayTemporarily(_hasIconPlatforms
                ? "这些图标暂时够不到，换个位置就能开跳"
                : "桌面没有可见图标，放上快捷方式后就能开跳", 3.5);
        PetFrame frame = _brain.Update(0);
        RenderFrame(frame);
        UpdatePetWindowLayer(frame, force: true);
    }

    private void TickWhack(double dt)
    {
        if (_settings.IsPaused) return;
        _windowScanSeconds += dt;
        if (_windowScanSeconds >= 0.12)
        {
            _windowScanSeconds = 0;
            _windowCandidates = TopLevelWindowService.GetCandidates(_hwnd);
        }
        _whack.Update(dt, _windowCandidates);
        if (!_whack.IsActive)
        {
            int score = _whack.Score;
            int misses = _whack.Misses;
            SetMode(PetMode.Life);
            SayTemporarily($"打地鼠结束：命中 {score} 次，漏掉 {misses} 次", 5);
            RenderFrame(_brain.Update(0));
            return;
        }
        RenderWhackFrame();
    }

    private void RenderWhackFrame()
    {
        if (_settings.Mode != PetMode.WindowWhack) return;
        CharacterVisual.Visibility = Visibility.Collapsed;
        SpeechBubble.Visibility = Visibility.Collapsed;
        PetHitArea.Visibility = Visibility.Collapsed;
        if (!_whack.IsPeekVisible || _whack.Target is not WindowWhackTarget target)
        {
            WhackVisual.Visibility = Visibility.Collapsed;
            return;
        }
        RenderPeekTarget(target);
    }

    private static double NextAmbientDelaySeconds() => 120 + Random.Shared.NextDouble() * 120;

    private void ScheduleAmbientPeek()
    {
        _ambientIdleSeconds = 0;
        _ambientNextPeekSeconds = NextAmbientDelaySeconds();
    }

    private void TickAmbientPeek(double dt)
    {
        if (_settings.Mode != PetMode.Life || !_settings.AmbientWindowPeek || _settings.IsPaused)
            return;

        if (_ambientPeekTarget.HasValue)
        {
            _ambientPeekSecondsRemaining -= dt;
            if (_ambientPeekSecondsRemaining <= 0)
            {
                EndAmbientPeek(false);
                return;
            }
            _ambientWindowScanSeconds += dt;
            if (_ambientWindowScanSeconds >= 0.15)
            {
                _ambientWindowScanSeconds = 0;
                UpdateAmbientPeekHost();
            }
            return;
        }

        if (_pointerDown || _dragging || _perchRetryTimer.IsEnabled) return;
        _ambientIdleSeconds += dt;
        if (_ambientIdleSeconds < _ambientNextPeekSeconds) return;
        BeginAmbientPeek();
    }

    private void BeginAmbientPeek()
    {
        if (_storedIconSnacks.Count > 0 || _returningIcons.Count > 0)
        {
            _ambientIdleSeconds = 0;
            _ambientNextPeekSeconds = 15 + Random.Shared.NextDouble() * 15;
            return;
        }
        if (_brain.IsLifePerched || _brain.IsLifeWindowPerched || _brain.CurrentState is
            PetState.IconHungry or PetState.IconSeekSnack or PetState.IconNibble or
            PetState.IconEat or PetState.IconStuffed or PetState.IconSpit)
        {
            _ambientIdleSeconds = 0;
            _ambientNextPeekSeconds = 15 + Random.Shared.NextDouble() * 15;
            return;
        }
        IReadOnlyList<PeekWindow> candidates = TopLevelWindowService.GetCandidates(_hwnd);
        if (candidates.Count == 0)
        {
            _ambientIdleSeconds = 0;
            _ambientNextPeekSeconds = 15 + Random.Shared.NextDouble() * 15;
            return;
        }
        PeekWindow host = candidates[Random.Shared.Next(candidates.Count)];
        if (!WindowWhackGame.TryCreateTarget(host, Random.Shared, out WindowWhackTarget target))
        {
            ScheduleAmbientPeek();
            return;
        }
        _ambientPeekTarget = target;
        _ambientPeekSecondsRemaining = 5 + Random.Shared.NextDouble() * 3;
        _ambientWindowScanSeconds = 0;
        _clickTimer.Stop();
        _pointerDown = false;
        _dragging = false;
        RootGrid.ReleaseMouseCapture();
        Topmost = true;
        UpdateDpiLayout();
    }

    private void UpdateAmbientPeekHost()
    {
        if (_ambientPeekTarget is not WindowWhackTarget target) return;
        PeekWindow? host = TopLevelWindowService.GetCandidates(_hwnd)
            .FirstOrDefault(candidate => candidate.Handle == target.WindowHandle);
        if (host is not PeekWindow current || current.Handle == IntPtr.Zero)
        {
            EndAmbientPeek(false);
            return;
        }
        if (!WindowWhackGame.TryRefreshTarget(current, target, Random.Shared,
                out WindowWhackTarget refreshed))
        {
            EndAmbientPeek(false);
            return;
        }
        _ambientPeekTarget = refreshed;
    }

    private void EndAmbientPeek(bool render = true)
    {
        if (!_ambientPeekTarget.HasValue) return;
        _ambientPeekTarget = null;
        _ambientPeekSecondsRemaining = 0;
        _ambientWindowScanSeconds = 0;
        ScheduleAmbientPeek();
        WhackVisual.Visibility = Visibility.Collapsed;
        CharacterVisual.Visibility = Visibility.Visible;
        UpdateDpiLayout();
        if (render)
            RenderFrame(_brain.Update(0));
        UpdatePetWindowLayer(_brain.Update(0), force: true);
    }

    private void RenderAmbientPeekFrame()
    {
        if (_settings.Mode != PetMode.Life || _ambientPeekTarget is not WindowWhackTarget target)
            return;
        RenderPeekTarget(target);
    }

    private void RenderPeekTarget(WindowWhackTarget target)
    {
        WhackVisual.Visibility = Visibility.Visible;
        CharacterVisual.Visibility = Visibility.Collapsed;
        SpeechBubble.Visibility = Visibility.Collapsed;
        PetHitArea.Visibility = Visibility.Collapsed;
        WhackHud.Visibility = _settings.Mode == PetMode.WindowWhack
            ? Visibility.Visible : Visibility.Collapsed;
        if (_settings.Mode == PetMode.WindowWhack)
            WhackHudText.Text = $"{_whack.Score} · {Math.Ceiling(_whack.TimeRemaining)}s";
        WhackImage.Source = _sprites.GetValueOrDefault("peek") ?? _peekSprite ?? _sprites.GetValueOrDefault("idle");
        WhackImage.RenderTransformOrigin = new Point(0.5, 0.5);
        WhackImage.RenderTransform = new ScaleTransform(target.Side == PeekSide.Right ? -1 : 1, 1);
        double x = target.Side switch
        {
            PeekSide.Left => target.BorderPoint.X - WindowWhackGame.PeekOutsidePixels,
            PeekSide.Right => target.BorderPoint.X -
                (WindowWhackGame.PeekSize - WindowWhackGame.PeekOutsidePixels),
            _ => target.BorderPoint.X - WindowWhackGame.PeekSize / 2
        };
        double y = target.Side == PeekSide.Top
            ? target.BorderPoint.Y - WindowWhackGame.PeekOutsidePixels
            : target.BorderPoint.Y - WindowWhackGame.PeekSize / 2;
        Rect bounds = GetVirtualBounds();
        int windowX = (int)Math.Round(Math.Clamp(x, bounds.Left, bounds.Right - 100));
        int windowY = (int)Math.Round(Math.Clamp(y, bounds.Top, bounds.Bottom - 100));
        if (_hwnd != IntPtr.Zero && (windowX != _lastWindowX || windowY != _lastWindowY))
        {
            SetWindowPos(_hwnd, IntPtr.Zero, windowX, windowY, 0, 0,
                SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            _lastWindowX = windowX;
            _lastWindowY = windowY;
        }
    }

    private void SetPaused(bool value)
    {
        if (value)
            EndAmbientPeek(false);
        else
            ScheduleAmbientPeek();
        _settings.IsPaused = value;
        _brain.Trigger(value ? PetAction.Pause : PetAction.Resume);
        _settings.Save();
    }

    private void SetAlwaysOnTop(bool value)
    {
        _settings.AlwaysOnTop = value;
        UpdatePetWindowLayer(_brain.Update(0), force: true);
        _settings.Save();
    }

    private void SetAmbientWindowPeek(bool value)
    {
        _settings.AmbientWindowPeek = value;
        if (!value)
            EndAmbientPeek();
        else
            ScheduleAmbientPeek();
        _settings.Save();
    }

    private void SetUseDailyRoutine(bool value)
    {
        _settings.UseDailyRoutine = value;
        _brain.UseDailyRoutine = value;
        _settings.Save();
    }

    private void SetAmbientChatter(bool value)
    {
        _settings.AmbientChatter = value;
        _brain.AllowChatter = value;
        _settings.Save();
    }

    private void SetShowBubbles(bool value)
    {
        _settings.ShowBubbles = value;
        _settings.Save();
        RenderFrame(_brain.Update(0));
    }

    private void SetFollowDeepSeekActivity(bool value)
    {
        _settings.FollowDeepSeekActivity = value;
        PollDeepSeek(0, force: true);
        _settings.Save();
        RenderFrame(_brain.Update(0));
    }

    private void SetShowDeepSeekBalance(bool value)
    {
        _settings.ShowDeepSeekBalance = value;
        PollDeepSeek(0, force: true);
        _settings.Save();
    }

    private void SetShortcutPlay(bool value)
    {
        _settings.AllowShortcutPlay = value;
        if (value)
            RefreshShortcutAvailability();
        else
            _brain.AllowShortcutPlay = false;
        _settings.Save();
    }

    private void MoveToPrimaryScreen()
    {
        if (_settings.Mode != PetMode.Life)
            SetMode(PetMode.Life);
        else
            EndAmbientPeek(false);
        Forms.Screen? screen = Forms.Screen.PrimaryScreen;
        if (screen is null) return;
        _activityBounds = GetActivityBounds(screen);
        _brain.SetBounds(_activityBounds);
        _brain.PlaceAt(screen.WorkingArea.Left + (screen.WorkingArea.Width - PetBrain.PetWidth) / 2,
            screen.WorkingArea.Bottom - PetBrain.PetHeight);
        RenderFrame(_brain.Update(0));
        BringForward();
    }

    private void BringForward()
    {
        if (_storedIconSnacks.Count > 0 || _returningIcons.Count > 0)
        {
            MaintainEatenIconLayer();
            return;
        }
        bool wasTopmost = Topmost;
        Topmost = true;
        Topmost = wasTopmost;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _ambientPeekTarget = null;
        _timer.Stop();
        _clickTimer.Stop();
        _perchRetryTimer.Stop();
        ReleaseAllIconVisuals();
        _eatenIconImage = null;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayMenu.Dispose();
        _whack.Stop();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;

        public POINT(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rectangle);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter,
        int x, int y, int cx, int cy, uint flags);
}
