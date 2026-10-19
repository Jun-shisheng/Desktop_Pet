using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace DeepSeekFishPet.Behavior;

public enum PetPose
{
    Stand, Walk, Run, Tiptoe, Jump, Sit, Lie, Sleep, Wave, Bow,
    Stretch, Sword, Carry, Fish, Read, Drink, Climb, Fall, Cheer, Spin
}

public enum PetProp
{
    None, Sword, FishingRod, Fish, TeaCup, Book, Shortcut, Pillow,
    Sparkle, Question, MusicNote, Heart, PaperPlane,
    Broom, Cloth, Bubble, Ball, Rice, Phone
}

public enum PetAction
{
    Pet, DoubleClick, DragStart, DragEnd, Call, Fish, Nap, Read, Tea, Sword,
    Shortcut, Wave, Cheer, Pause, Resume, IconHop, PaperPlane, ShakeSpit,
    AbortIconSnack, FeedRice, Sweep, Sing, Dance, PlayBall, BlowBubbles,
    Stargaze
}

public enum PetState
{
    Stand, Blink, LookLeft, LookRight, TiltHead, AdjustRibbon, CheckArmor,
    CheckSword, Sway, Hum, Think, Smile, Pout, Salute, Nod, Yawn,
    MorningStretch, Sneeze, Sigh,
    WalkLeft, WalkRight, RunLeft, RunRight, SprintLeft, SprintRight,
    TiptoeLeft, TiptoeRight, HopLeft, HopRight, SkipLeft, SkipRight,
    DashLeft, DashRight, Slide, SweepLeft, SweepRight, TurnAround,
    Stumble, Recover,
    Sit, Stretch, LieDown, Nap, Dream, Wake, RollOver, TeaBreak,
    ReadBook, FishCast, FishWait, FishBite, FishReel, FishCelebrate,
    Wave, Bow, Spin, Cheer, SwordPose, SwordSwing, SwordSheathe,
    PetHappy, PetShy, PetAnnoyed, Laugh, Sparkle,
    Daydream, Stargaze, Sunbathe, Sing, Dance, PlayBall, ChaseTail,
    BlowBubbles, PhoneScroll, WipeDesk, CookRice, EatRice, RiceStuffed,
    PickShortcut, CarryShortcut, InspectShortcut, ReturnShortcut,
    ClimbLeft, ClimbRight, Hang, Drop, Land, Dragged,
    IconApproach, IconJump, IconLand, IconInspect, IconNibble, IconClimb, IconHang,
    IconFall, IconRest, IconWaitForBridge,
    PlaneFold, PlaneTakeoff, PlaneFlight, PlaneLand,
    IconHungry, IconSeekSnack, IconEat, IconStuffed, IconSpit
}

public readonly record struct PetFrame(
    double X, double Y, double Scale, double Rotation, bool Flip,
    PetPose Pose, PetProp Prop, string Speech, string StateName,
    double Bob, double Opacity, double Progress);

/// <summary>
/// Autonomous, deterministic-from-seed behavior controller. Coordinates are
/// screen pixels and refer to the top-left corner of the compact pet body.
/// The controller never changes desktop files or Explorer icon positions.
/// </summary>
public sealed class PetBrain
{
    public const double PetWidth = 76;
    public const double PetHeight = 84;

    private readonly Random _random;
    private readonly Queue<PetState> _sequence = new();
    private Rect _bounds;
    private PetState _state;
    private double _elapsed;
    private double _duration;
    private double _x;
    private double _y;
    private double _energy = 0.8;
    private bool _faceLeft;
    private bool _dragging;
    private string _speech = "";
    private double _speechTime;
    private int _consecutiveMotion;
    private readonly Dictionary<string, IconPlatform> _iconPlatforms = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<WindowPlatform> _windowPlatforms = new();
    private readonly HashSet<string> _visitedIcons = new(StringComparer.OrdinalIgnoreCase);
    private string? _currentIconId;
    private string? _targetIconId;
    private string? _lifePerchIconId;
    private IntPtr _lifePerchWindowHandle;
    private bool _lifePerchHanging;
    private double _bridgeWaitSeconds;
    private double _motionStartX;
    private double _motionStartY;
    private double _motionEndX;
    private double _motionEndY;
    private double _hopHeight;
    private double _hunger = 0.2;
    private double _mood = 0.72;
    private double _iconSnackRetrySeconds;
    private double _chatterSeconds;
    private double _chatterRemaining;
    private string _chatter = "";
    // The UI owns the swallowed visuals and their masks. The brain only needs
    // their IDs to avoid eating the same desktop icon again before a spit.
    private readonly HashSet<string> _storedSnackIconIds = new(StringComparer.OrdinalIgnoreCase);
    private bool _pendingIconAdventureStart;
    private bool _pendingPaperPlane;
    private IconPlatform? _eatenIconPlatform;

    public PetBrain(Rect bounds, int? seed = null)
    {
        _random = seed is null ? new Random() : new Random(seed.Value);
        _chatterSeconds = 90 + _random.NextDouble() * 150;
        _bounds = bounds;
        _x = Math.Clamp(bounds.Left + bounds.Width * 0.66, bounds.Left, MaxX);
        _y = GroundY;
        Enter(PetState.Stand);
    }

    public PetState CurrentState => _state;
    public string CurrentStateName => Info[_state].Name;
    public double Hunger => _hunger;
    public double Energy => _energy;
    public double Mood => _mood;
    public bool IsPaused { get; set; }
    public bool AllowShortcutPlay { get; set; } = true;
    /// <summary>Bias autonomous activities toward morning, noon, evening, and night.</summary>
    public bool UseDailyRoutine { get; set; } = true;
    /// <summary>Occasionally speak a line tied to the time of day, hunger, or mood.</summary>
    public bool AllowChatter { get; set; } = true;
    public bool IsIconAdventureActive { get; private set; }
    public bool IsWaitingForBridge => IsIconAdventureActive && _state == PetState.IconWaitForBridge;
    public bool IsLifePerched => !IsIconAdventureActive && _lifePerchIconId is not null;
    public string? LifePerchedIconId => IsLifePerched ? _lifePerchIconId : null;
    public bool IsLifeWindowPerched => !IsIconAdventureActive &&
        _lifePerchWindowHandle != IntPtr.Zero;
    public IntPtr LifePerchedWindowHandle => IsLifeWindowPerched
        ? _lifePerchWindowHandle : IntPtr.Zero;
    public bool HasStandableIcon => _iconPlatforms.Values.Any(CanUseIcon);
    public bool BridgeWaitTimedOut { get; private set; }
    public int IconHopCombo { get; private set; }
    public bool HasStoredIconSnacks => _storedSnackIconIds.Count > 0;
    /// <summary>The icon being eaten now; older swallowed icons belong to the UI.</summary>
    public IconPlatform? EatenIconPlatform => _eatenIconPlatform;
    public event Action<PetState>? StateChanged;

    /// <summary>Sync IDs of swallowed icons whose visuals are retained by the UI.</summary>
    public void SetStoredSnackIconIds(IEnumerable<string> ids)
    {
        _storedSnackIconIds.Clear();
        foreach (string id in ids)
            if (!string.IsNullOrWhiteSpace(id))
                _storedSnackIconIds.Add(id);
    }

    private double MaxX => Math.Max(_bounds.Left, _bounds.Right - PetWidth);
    private double GroundY => Math.Max(_bounds.Top, _bounds.Bottom - PetHeight);

    public void SetBounds(Rect bounds)
    {
        _bounds = bounds;
        _x = Math.Clamp(_x, _bounds.Left, MaxX);
        _y = Math.Clamp(_y, _bounds.Top, GroundY);
    }

    public void PlaceAt(double x, double y)
    {
        _x = Math.Clamp(x, _bounds.Left, MaxX);
        _y = Math.Clamp(y, _bounds.Top, GroundY);
        // Manual relocation (for example "move to primary screen") detaches
        // the old support. Dragging already detaches on DragStart.
        if (!_dragging && (_lifePerchIconId is not null ||
                           _lifePerchWindowHandle != IntPtr.Zero))
        {
            _lifePerchIconId = null;
            _lifePerchWindowHandle = IntPtr.Zero;
            _lifePerchHanging = false;
            _sequence.Clear();
            Enter(_y < GroundY - 12 ? PetState.Drop : PetState.Land);
        }
    }

    /// <summary>
    /// Snap a dragged pet onto the nearest icon under its feet. The icon view
    /// is read-only; this only changes the pet's position and game state.
    /// </summary>
    public bool TryPerchOnIcon(bool startGame = true)
    {
        if (!_dragging || _eatenIconPlatform.HasValue ||
            (startGame && HasStoredIconSnacks) || _iconPlatforms.Count == 0)
            return false;

        double footX = _x + PetWidth / 2;
        double footY = _y + PetHeight;
        IconPlatform? nearest = null;
        double bestDistance = double.MaxValue;
        foreach (IconPlatform icon in _iconPlatforms.Values)
        {
            if (IsIconCovered(icon))
                continue;
            double surfaceY = icon.SurfaceY;
            // A top-row icon can be held from its side in life mode. There is
            // no room to put the pet's feet on its upper edge.
            if (startGame && !CanUseIcon(icon))
                continue;

            double dx = Math.Abs(footX - icon.CenterX);
            double dy = footY - surfaceY;
            double horizontalReach = icon.Bounds.Width / 2 + 12;
            double verticalReach = HasStandingRoom(icon)
                ? Math.Min(40, icon.Bounds.Height * 0.45)
                : Math.Max(76, PetHeight * 0.9);
            bool feetNearSurface = dx <= horizontalReach &&
                dy is >= -30 && dy <= verticalReach;
            // Grabbing the head often puts the cursor on the icon while the
            // body's feet are already below its top. The body must genuinely
            // overlap the visible glyph to count as a drop in that case.
            double glyphHalfWidth = Math.Min(28, icon.Bounds.Width * 0.36);
            Rect glyph = new(icon.CenterX - glyphHalfWidth, surfaceY,
                glyphHalfWidth * 2, Math.Min(44, icon.Bounds.Height * 0.6));
            Rect overlap = Rect.Intersect(new Rect(_x, _y, PetWidth, PetHeight), glyph);
            bool bodyOverGlyph = !overlap.IsEmpty &&
                overlap.Width >= 8 && overlap.Height >= 8;
            (double hangX, double hangY) = IconHangAnchor(icon);
            bool atHangAnchor = !HasStandingRoom(icon) &&
                Math.Abs(_x - hangX) <= 18 && Math.Abs(_y - hangY) <= 18;
            if (!feetNearSurface && !bodyOverGlyph && !atHangAnchor)
                continue;

            double distance = Math.Pow(dx / Math.Max(40, horizontalReach), 2) +
                              Math.Pow(dy / 115, 2);
            if (atHangAnchor)
                distance = Math.Min(distance,
                    Math.Pow((_x - hangX) / 36, 2) +
                    Math.Pow((_y - hangY) / 36, 2));
            if (distance >= bestDistance)
                continue;
            nearest = icon;
            bestDistance = distance;
        }

        if (nearest is not IconPlatform platform)
            return false;

        _dragging = false;
        _pendingIconAdventureStart = false;
        _pendingPaperPlane = false;
        _sequence.Clear();
        if (startGame)
        {
            _lifePerchIconId = null;
            _lifePerchWindowHandle = IntPtr.Zero;
            _lifePerchHanging = false;
            IsIconAdventureActive = true;
            _bridgeWaitSeconds = 0;
            BridgeWaitTimedOut = false;
            IconHopCombo = 0;
            _visitedIcons.Clear();
            _visitedIcons.Add(platform.Id);
            _currentIconId = platform.Id;
            _targetIconId = null;
            (_x, _y) = IconAnchor(platform);
            Enter(HasStandingRoom(platform) ? PetState.IconLand : PetState.IconHang);
            _duration = Math.Max(_duration, 1.5);
        }
        else
        {
            _lifePerchIconId = platform.Id;
            _lifePerchWindowHandle = IntPtr.Zero;
            _lifePerchHanging = !HasStandingRoom(platform);
            (_x, _y) = _lifePerchHanging ? IconHangAnchor(platform) : IconAnchor(platform);
            Enter(_lifePerchHanging ? PetState.IconHang : PetState.IconLand);
            _duration = Math.Max(_duration, 1.0);
        }
        return true;
    }

    /// <summary>Snap a dragged pet's feet onto an exposed application top edge.</summary>
    public bool TryPerchOnWindow()
    {
        if (!_dragging || IsIconAdventureActive || _eatenIconPlatform.HasValue)
            return false;

        double footX = _x + PetWidth / 2;
        double footY = _y + PetHeight;
        WindowPlatform? nearest = null;
        double bestDistance = double.MaxValue;
        foreach (WindowPlatform platform in _windowPlatforms)
        {
            if (!platform.HasExposedTopAt(footX) ||
                platform.Bounds.Top < _bounds.Top + PetHeight)
                continue;
            double dy = footY - platform.Bounds.Top;
            bool feetNearEdge = dy is >= -30 and <= 45;
            bool bodyStraddlesEdge = _y <= platform.Bounds.Top - 8 &&
                dy is >= 8 and <= PetHeight - 8;
            if (!feetNearEdge && !bodyStraddlesEdge)
                continue;
            double distance = Math.Abs(dy);
            if (distance >= bestDistance)
                continue;
            nearest = platform;
            bestDistance = distance;
        }
        if (nearest is not WindowPlatform host)
            return false;

        _dragging = false;
        _lifePerchIconId = null;
        _lifePerchHanging = false;
        _lifePerchWindowHandle = host.Handle;
        _sequence.Clear();
        _y = Math.Clamp(host.Bounds.Top - PetHeight, _bounds.Top, GroundY);
        Enter(PetState.Land);
        return true;
    }

    /// <summary>Replace ordinary-window rectangles without moving any host window.</summary>
    public void SetWindowPlatforms(IReadOnlyList<WindowPlatform> platforms)
    {
        WindowPlatform? oldHost = FindWindowPlatform(_lifePerchWindowHandle);
        _windowPlatforms.Clear();
        foreach (WindowPlatform platform in platforms)
            if (platform.Handle != IntPtr.Zero && !platform.Bounds.IsEmpty &&
                platform.Bounds.IntersectsWith(_bounds))
                _windowPlatforms.Add(platform);

        if (_lifePerchWindowHandle == IntPtr.Zero || IsIconAdventureActive || _dragging)
            return;
        WindowPlatform? current = FindWindowPlatform(_lifePerchWindowHandle);
        if (current is not WindowPlatform host)
        {
            LeaveLifeWindowPerch();
            return;
        }
        if (oldHost is WindowPlatform previous)
            _x = Math.Clamp(_x + host.Bounds.Left - previous.Bounds.Left,
                _bounds.Left, MaxX);
        if (!host.HasExposedTopAt(_x + PetWidth / 2) ||
            host.Bounds.Top < _bounds.Top + PetHeight)
        {
            LeaveLifeWindowPerch();
            return;
        }
        _y = Math.Clamp(host.Bounds.Top - PetHeight, _bounds.Top, GroundY);
    }

    /// <summary>Replace the read-only Explorer layout snapshot; existing icon IDs survive moves.</summary>
    public void SetIconPlatforms(IReadOnlyList<IconPlatform> platforms)
    {
        var previous = new Dictionary<string, IconPlatform>(_iconPlatforms, StringComparer.OrdinalIgnoreCase);
        _iconPlatforms.Clear();
        foreach (IconPlatform platform in platforms)
        {
            if (string.IsNullOrWhiteSpace(platform.Id) || platform.Bounds.IsEmpty ||
                platform.Bounds.Width <= 0 || platform.Bounds.Height <= 0 ||
                !platform.Bounds.IntersectsWith(_bounds))
                continue;
            _iconPlatforms[platform.Id] = platform;
        }

        bool layoutChanged = previous.Count != _iconPlatforms.Count ||
            _iconPlatforms.Any(kv =>
                !previous.TryGetValue(kv.Key, out IconPlatform old) ||
                Math.Abs(kv.Value.CenterX - old.CenterX) > 3 ||
                Math.Abs(kv.Value.SurfaceY - old.SurfaceY) > 3);
        if (layoutChanged && IsWaitingForBridge)
        {
            _bridgeWaitSeconds = 0;
            BridgeWaitTimedOut = false;
        }

        // Moving a visited icon gives the player a fresh stepping stone.
        foreach ((string id, IconPlatform platform) in _iconPlatforms)
        {
            if (previous.TryGetValue(id, out IconPlatform old) &&
                (Math.Abs(platform.CenterX - old.CenterX) > 10 ||
                 Math.Abs(platform.Bounds.Top - old.Bounds.Top) > 10))
                _visitedIcons.Remove(id);
        }

        if (_lifePerchIconId is not null && !IsIconAdventureActive && !_dragging)
        {
            if (!_iconPlatforms.TryGetValue(_lifePerchIconId, out IconPlatform lifePlatform))
                LeaveLifePerch();
            else
            {
                if (previous.TryGetValue(_lifePerchIconId, out IconPlatform oldLifePlatform))
                {
                    _x = Math.Clamp(_x + lifePlatform.CenterX - oldLifePlatform.CenterX,
                        _bounds.Left, MaxX);
                    _y = Math.Clamp(_y + lifePlatform.SurfaceY - oldLifePlatform.SurfaceY,
                        _bounds.Top, GroundY);
                }
                bool shouldHang = !HasStandingRoom(lifePlatform);
                if (shouldHang != _lifePerchHanging)
                {
                    _lifePerchHanging = shouldHang;
                    (_x, _y) = shouldHang ? IconHangAnchor(lifePlatform) : IconAnchor(lifePlatform);
                    Enter(shouldHang ? PetState.IconHang : PetState.IconLand);
                }
            }
        }

        if (_eatenIconPlatform is IconPlatform eaten &&
            _iconPlatforms.TryGetValue(eaten.Id, out IconPlatform movedSource))
            _eatenIconPlatform = movedSource;

        if (IsIconAdventureActive)
        {
            if (!HasStandableIcon)
            {
                StopIconAdventure();
                return;
            }
            if (_targetIconId is not null &&
                (!_iconPlatforms.TryGetValue(_targetIconId, out IconPlatform targeted) ||
                 !CanUseIcon(targeted)))
            {
                _targetIconId = null;
                if (IsIconMotionState(_state)) Enter(PetState.IconFall);
            }
            if (_currentIconId is not null &&
                (!_iconPlatforms.TryGetValue(_currentIconId, out IconPlatform current) ||
                 !CanUseIcon(current)) &&
                !IsIconMotionState(_state))
            {
                _currentIconId = null;
                Enter(PetState.IconFall);
            }
            if (_targetIconId is not null && _iconPlatforms.TryGetValue(_targetIconId, out IconPlatform target))
            {
                (_motionEndX, _motionEndY) = IconAnchor(target);
            }
            if (!IsPaused && IsWaitingForBridge && FindHopTarget() is IconPlatform bridge)
                BeginIconHop(bridge);
        }
        else if (_state == PetState.IconSeekSnack && _targetIconId is not null)
        {
            if (_iconPlatforms.TryGetValue(_targetIconId, out IconPlatform snack))
                (_motionEndX, _motionEndY) = SnackAnchor(snack);
            else
            {
                _targetIconId = null;
                Enter(PetState.Stand);
            }
        }
    }

    /// <summary>Start the explicit icon platform mini-game. Life-mode eating is suspended.</summary>
    public void StartIconAdventure()
    {
        if (IsIconAdventureActive || _dragging || !HasStandableIcon) return;
        _lifePerchIconId = null;
        _lifePerchWindowHandle = IntPtr.Zero;
        _lifePerchHanging = false;
        if (_eatenIconPlatform.HasValue || HasStoredIconSnacks ||
            _state == PetState.IconSpit)
        {
            _pendingIconAdventureStart = true;
            if (_state == PetState.IconSpit) return;
            _sequence.Clear();
            Enter(PetState.IconSpit);
            return;
        }
        BeginIconAdventure();
    }

    private void BeginIconAdventure()
    {
        _pendingIconAdventureStart = false;
        _pendingPaperPlane = false;
        IsIconAdventureActive = true;
        _bridgeWaitSeconds = 0;
        BridgeWaitTimedOut = false;
        IconHopCombo = 0;
        _visitedIcons.Clear();
        _sequence.Clear();
        _currentIconId = null;
        IconPlatform first = ChooseStartingPlatform();
        BeginIconApproach(first);
    }

    public void StopIconAdventure()
    {
        _pendingIconAdventureStart = false;
        _pendingPaperPlane = false;
        if (!IsIconAdventureActive) return;
        IsIconAdventureActive = false;
        _bridgeWaitSeconds = 0;
        BridgeWaitTimedOut = false;
        _currentIconId = null;
        _targetIconId = null;
        _visitedIcons.Clear();
        _sequence.Clear();
        Enter(_y < GroundY - 12 ? PetState.Drop : PetState.Stand);
    }

    public void Trigger(PetAction action)
    {
        switch (action)
        {
            case PetAction.Pause: IsPaused = true; return;
            case PetAction.Resume: IsPaused = false; return;
            case PetAction.AbortIconSnack:
                _eatenIconPlatform = null;
                _targetIconId = null;
                _hunger = 0;
                _iconSnackRetrySeconds = 60;
                _sequence.Clear();
                Enter(PetState.Stand);
                return;
            case PetAction.ShakeSpit:
                if (IsIconAdventureActive ||
                    (!_eatenIconPlatform.HasValue && !HasStoredIconSnacks) ||
                    _state == PetState.IconSpit) return;
                _dragging = false;
                _sequence.Clear();
                Enter(PetState.IconSpit);
                return;
            case PetAction.DragStart:
                _dragging = true;
                _lifePerchIconId = null;
                _lifePerchWindowHandle = IntPtr.Zero;
                _lifePerchHanging = false;
                if (IsIconAdventureActive) StopIconAdventure();
                _sequence.Clear();
                Enter(PetState.Dragged);
                return;
            case PetAction.DragEnd:
                _dragging = false;
                _sequence.Clear();
                if (_y < GroundY - 12) StartSequence(PetState.Drop, PetState.Land);
                else StartSequence(PetState.Land);
                return;
        }

        if (_dragging) return;
        if (action == PetAction.IconHop)
        {
            if (!IsIconAdventureActive) StartIconAdventure();
            else if (!IsIconMotionState(_state) && _state is not PetState.PlaneFold and not PetState.PlaneTakeoff)
            {
                _sequence.Clear();
                ChooseNextIconHop();
            }
            return;
        }
        if (action == PetAction.PaperPlane)
        {
            if (IsIconAdventureActive)
            {
                if (IsIconMotionState(_state) || _state == PetState.IconFall)
                    _pendingPaperPlane = true;
                else
                {
                    _pendingPaperPlane = false;
                    BeginPaperPlaneOrWait();
                }
            }
            return;
        }
        switch (action)
        {
            case PetAction.Pet:
                _mood = Math.Min(1, _mood + 0.1);
                StartSequence(Pick(PetState.PetHappy, PetState.PetShy, PetState.PetAnnoyed), PetState.Smile);
                break;
            case PetAction.DoubleClick:
                _mood = Math.Min(1, _mood + 0.05);
                StartSequence(PetState.Spin, PetState.Cheer, PetState.Sparkle);
                break;
            case PetAction.Call:
                StartSequence(PetState.LookLeft, PetState.LookRight, PetState.Wave);
                break;
            case PetAction.Fish:
                StartSequence(PetState.Sit, PetState.FishCast, PetState.FishWait,
                    PetState.FishBite, PetState.FishReel, PetState.FishCelebrate);
                break;
            case PetAction.Nap:
                StartSequence(PetState.Yawn, PetState.LieDown, PetState.Nap,
                    PetState.Dream, PetState.Wake);
                break;
            case PetAction.Read:
                StartSequence(PetState.Sit, PetState.ReadBook, PetState.Stretch);
                break;
            case PetAction.Tea:
                StartSequence(PetState.Sit, PetState.TeaBreak, PetState.Smile);
                break;
            case PetAction.Sword:
                StartSequence(PetState.CheckSword, PetState.SwordPose,
                    PetState.SwordSwing, PetState.SwordSheathe, PetState.Salute);
                break;
            case PetAction.Shortcut when AllowShortcutPlay:
                StartSequence(PetState.PickShortcut, PetState.CarryShortcut,
                    PetState.InspectShortcut, PetState.ReturnShortcut);
                break;
            case PetAction.Wave: StartSequence(PetState.Wave, PetState.Bow); break;
            case PetAction.Cheer: StartSequence(PetState.Cheer, PetState.Sparkle); break;
            case PetAction.FeedRice:
                if (IsIconAdventureActive || _eatenIconPlatform.HasValue) return;
                _hunger = 0.05;
                _mood = Math.Min(1, _mood + 0.08);
                StartSequence(PetState.CookRice, PetState.EatRice, PetState.RiceStuffed);
                break;
            case PetAction.Sweep:
                StartSequence(Pick(PetState.SweepLeft, PetState.SweepRight), PetState.WipeDesk);
                break;
            case PetAction.Sing: StartSequence(PetState.Sing, PetState.Smile); break;
            case PetAction.Dance: StartSequence(PetState.Dance, PetState.Cheer); break;
            case PetAction.PlayBall: StartSequence(PetState.PlayBall, PetState.Cheer); break;
            case PetAction.BlowBubbles:
                StartSequence(PetState.BlowBubbles, PetState.ChaseTail);
                break;
            case PetAction.Stargaze:
                StartSequence(PetState.Sit, PetState.Stargaze, PetState.Smile);
                break;
        }
    }

    public PetFrame Update(double deltaSeconds)
    {
        double dt = Math.Clamp(deltaSeconds, 0, 0.1);
        if (!IsPaused && !_dragging)
        {
            if (IsWaitingForBridge)
            {
                _bridgeWaitSeconds += dt;
                if (_bridgeWaitSeconds >= 30)
                    BridgeWaitTimedOut = true;
            }
            _elapsed += dt;
            _speechTime = Math.Max(0, _speechTime - dt);
            _iconSnackRetrySeconds = Math.Max(0, _iconSnackRetrySeconds - dt);
            if (!IsIconAdventureActive && !_eatenIconPlatform.HasValue &&
                _state is not PetState.IconHungry and not PetState.IconSeekSnack and not PetState.IconEat)
                _hunger = Math.Min(1, _hunger + dt * 0.008);
            _mood = Math.Max(0, _mood - dt * 0.0007);
            UpdateChatter(dt);
            Move(dt);
            if (_elapsed >= _duration) Advance();
        }
        return BuildFrame();
    }

    private void UpdateChatter(double dt)
    {
        _chatterRemaining = Math.Max(0, _chatterRemaining - dt);
        if (!AllowChatter) return;
        _chatterSeconds -= dt;
        if (_chatterSeconds > 0) return;
        if (!CanChatter())
        {
            _chatterSeconds = 15;
            return;
        }
        _chatter = PickChatter();
        _chatterRemaining = 3.2;
        _chatterSeconds = 110 + _random.NextDouble() * 190;
    }

    private bool CanChatter() =>
        !IsIconAdventureActive && !_eatenIconPlatform.HasValue && _speechTime <= 0 &&
        _state is (PetState.Stand or PetState.Blink or PetState.LookLeft or
            PetState.LookRight or PetState.TiltHead or PetState.Sway or PetState.Hum or
            PetState.Think or PetState.Smile or PetState.Sit or PetState.Daydream or
            PetState.Stargaze or PetState.Sunbathe or PetState.PhoneScroll or
            PetState.BlowBubbles or PetState.TeaBreak or PetState.ReadBook or
            PetState.MorningStretch);

    private string PickChatter()
    {
        if (_hunger >= 0.85)
            return Pick("肚子咕咕叫了……有白饭吗？", "好饿呀，想吃白饭……");
        if (_mood <= 0.25)
            return Pick("有点无聊……陪我说说话嘛", "唉……今天好像没什么精神", "摸摸头会让我开心哦");
        if (_mood >= 0.85)
            return Pick("今天心情特别好！", "转个圈给你看~", "和你在一起真开心！");
        int hour = DateTime.Now.Hour;
        if (hour is >= 5 and < 11)
            return Pick("早上好！今天也要加油~", "白饭在锅里咕嘟咕嘟~", "伸个懒腰，开始新的一天！");
        if (hour is >= 11 and < 14)
            return Pick("到饭点啦，今天吃白饭吗？", "一起摸鱼休息一下吧~", "肚子有点饿了呢");
        if (hour is >= 14 and < 18)
            return Pick("喝杯茶休息一下吧", "桌面好干净呀，是我扫的！", "DeepSeek 在忙什么呢？");
        if (hour is >= 18 and < 22)
            return Pick("今天辛苦啦~", "晚上想吃点什么？", "一起看会儿星星吧");
        return Pick("夜深了，记得早点睡哦~", "我陪你到这么晚呀", "窗外好安静……");
    }

    private void Move(double dt)
    {
        if (IsIconAdventureActive)
        {
            MoveIconAdventure(dt);
            return;
        }
        if (_state == PetState.IconSeekSnack)
        {
            double t = Math.Clamp(_elapsed / _duration, 0, 1);
            double ease = t * t * (3 - 2 * t);
            _x = Math.Clamp(_motionStartX + (_motionEndX - _motionStartX) * ease, _bounds.Left, MaxX);
            _y = Math.Clamp(_motionStartY + (_motionEndY - _motionStartY) * ease, _bounds.Top, GroundY);
            return;
        }
        if (_state is PetState.IconHungry or PetState.IconNibble or PetState.IconEat or
            PetState.IconStuffed or PetState.IconSpit)
            return;
        if (_lifePerchHanging && _lifePerchIconId is not null)
        {
            if (_iconPlatforms.TryGetValue(_lifePerchIconId, out IconPlatform hanging))
                (_x, _y) = IconHangAnchor(hanging);
            else LeaveLifePerch();
            return;
        }
        double velocity = _state switch
        {
            PetState.WalkLeft => -32,
            PetState.WalkRight => 32,
            PetState.RunLeft => -62,
            PetState.RunRight => 62,
            PetState.SprintLeft => -88,
            PetState.SprintRight => 88,
            PetState.TiptoeLeft => -16,
            PetState.TiptoeRight => 16,
            PetState.HopLeft => -36,
            PetState.HopRight => 36,
            PetState.SkipLeft => -44,
            PetState.SkipRight => 44,
            PetState.DashLeft => -112,
            PetState.DashRight => 112,
            PetState.Slide => _faceLeft ? -48 : 48,
            PetState.SweepLeft => -22,
            PetState.SweepRight => 22,
            PetState.CarryShortcut => _faceLeft ? -18 : 18,
            _ => 0
        };

        if (velocity != 0)
        {
            _x += velocity * dt;
            _energy = Math.Max(0, _energy - Math.Abs(velocity) * dt / 6500);
            if (_x <= _bounds.Left)
            {
                _x = _bounds.Left;
                if (velocity < 0 && _sequence.Count == 0 && _elapsed > 0.2)
                    StartSequence(PetState.TurnAround,
                        _state == PetState.SweepLeft ? PetState.SweepRight : PetState.WalkRight);
            }
            else if (_x >= MaxX)
            {
                _x = MaxX;
                if (velocity > 0 && _sequence.Count == 0 && _elapsed > 0.2)
                    StartSequence(PetState.TurnAround,
                        _state == PetState.SweepRight ? PetState.SweepLeft : PetState.WalkLeft);
            }
        }
        else if (_state is PetState.Sit or PetState.Nap or PetState.TeaBreak or
                 PetState.ReadBook or PetState.FishWait or PetState.Daydream or
                 PetState.Stargaze or PetState.Sunbathe or PetState.PhoneScroll)
        {
            _energy = Math.Min(1, _energy + dt * 0.025);
        }

        if (_lifePerchWindowHandle != IntPtr.Zero)
        {
            WindowPlatform? platform = FindWindowPlatform(_lifePerchWindowHandle);
            if (platform is not WindowPlatform host ||
                !host.HasExposedTopAt(_x + PetWidth / 2))
                LeaveLifeWindowPerch();
            else
                _y = Math.Clamp(host.Bounds.Top - PetHeight, _bounds.Top, GroundY);
            return;
        }

        if (_lifePerchIconId is not null)
        {
            if (!_iconPlatforms.TryGetValue(_lifePerchIconId, out IconPlatform platform) ||
                !HasStandingRoom(platform) ||
                !IsFootSupportedBy(platform))
                LeaveLifePerch();
            else
                _y = Math.Clamp(platform.SurfaceY - PetHeight, _bounds.Top, GroundY);
            return;
        }

        double previousFootY = _y + PetHeight;
        switch (_state)
        {
            case PetState.ClimbLeft:
                _x = _bounds.Left;
                _y = Math.Max(_bounds.Top + 15, _y - 45 * dt);
                _faceLeft = true;
                break;
            case PetState.ClimbRight:
                _x = MaxX;
                _y = Math.Max(_bounds.Top + 15, _y - 45 * dt);
                _faceLeft = false;
                break;
            case PetState.Drop:
                _y = Math.Min(GroundY, _y + (90 + _elapsed * 130) * dt);
                break;
            case PetState.Land:
                _y = Math.Min(GroundY, _y + 100 * dt);
                break;
            default:
                if (_state is not PetState.Hang && _y < GroundY)
                    _y = Math.Min(GroundY, _y + 160 * dt);
                break;
        }
        if (_y + PetHeight > previousFootY + 0.01 &&
            TryLandOnFallingSurface(previousFootY, _y + PetHeight))
            return;
        if (_state == PetState.Drop && _y >= GroundY)
            Advance();
    }

    private void MoveIconAdventure(double dt)
    {
        double t = Math.Clamp(_elapsed / _duration, 0, 1);
        if (_state is PetState.IconApproach or PetState.IconJump or PetState.PlaneFlight)
        {
            double ease = t * t * (3 - 2 * t);
            _x = Math.Clamp(_motionStartX + (_motionEndX - _motionStartX) * ease, _bounds.Left, MaxX);
            double arc = _state == PetState.IconApproach ? 0 : _hopHeight * 4 * t * (1 - t);
            _y = Math.Clamp(_motionStartY + (_motionEndY - _motionStartY) * ease - arc,
                _bounds.Top, GroundY);
        }
        else if (_state == PetState.IconFall)
        {
            _y = Math.Min(GroundY, _motionStartY + 80 * t * t);
        }
        else if (_state == PetState.IconClimb && _currentIconId is not null &&
                 _iconPlatforms.TryGetValue(_currentIconId, out IconPlatform climbing))
        {
            (double anchorX, double anchorY) = IconAnchor(climbing);
            _x = anchorX + (_faceLeft ? -1 : 1) * Math.Sin(t * Math.PI) * 2;
            _y = Math.Max(_bounds.Top, anchorY - Math.Sin(t * Math.PI) * 18);
        }
        else if (_state is PetState.IconLand or PetState.IconInspect or PetState.IconHang or
                 PetState.IconRest or PetState.IconWaitForBridge or PetState.PlaneFold or
                 PetState.PlaneTakeoff or PetState.PlaneLand)
        {
            // Follow a moved platform gently on the next layout snapshot, never move Explorer's icon.
            if (_currentIconId is not null && _iconPlatforms.TryGetValue(_currentIconId, out IconPlatform current))
            {
                (double anchorX, double anchorY) = IconAnchor(current);
                _x += (anchorX - _x) * Math.Min(1, dt * 5);
                _y += (anchorY - _y) * Math.Min(1, dt * 5);
            }
        }
        // Manual actions in this mode remain beside the current icon; they do not fall to the floor.
    }

    private PetFrame BuildFrame()
    {
        StateInfo info = Info[_state];
        bool hangingFromTop = IsIconAdventureActive && _currentIconId is not null &&
            _iconPlatforms.TryGetValue(_currentIconId, out IconPlatform current) &&
            !HasStandingRoom(current);
        PetPose pose = hangingFromTop && _state is (PetState.IconWaitForBridge or PetState.PlaneFold)
            ? PetPose.Climb : info.Pose;
        double p = _duration <= 0 ? 0 : Math.Clamp(_elapsed / _duration, 0, 1);
        double beat = Math.Sin(_elapsed * Math.PI * 4);
        double bob = _state switch
        {
            PetState.PlayBall => -Math.Abs(Math.Sin(_elapsed * Math.PI * 3)) * 18,
            PetState.Dance => -Math.Abs(Math.Sin(_elapsed * Math.PI * 5)) * 5,
            PetState.Sing => -Math.Abs(Math.Sin(_elapsed * 3)) * 1.5,
            PetState.BlowBubbles => Math.Sin(_elapsed * 2) * 0.8,
            PetState.Sneeze => -Math.Abs(Math.Sin(p * Math.PI)) * 3,
            _ => info.Pose switch
            {
                PetPose.Walk => -Math.Abs(beat) * 0.8,
                PetPose.Run => -Math.Abs(Math.Sin(_elapsed * Math.PI * 8)) * 1.1,
                PetPose.Tiptoe => -Math.Abs(beat) * 0.4,
                PetPose.Jump when IsIconAdventureActive => 0,
                PetPose.Jump => -Math.Abs(Math.Sin(p * Math.PI *
                    (_state is PetState.SkipLeft or PetState.SkipRight ? 2 : 1))) * 25,
                PetPose.Cheer => -Math.Abs(Math.Sin(_elapsed * Math.PI * 6)) * 4,
                PetPose.Sleep => Math.Sin(_elapsed * 2) * 0.8,
                PetPose.Fish => Math.Sin(_elapsed * 2.5) * 0.7,
                _ => 0
            }
        };
        double rotation = _state switch
        {
            PetState.Spin => 360 * p,
            PetState.RollOver => 360 * p,
            PetState.ChaseTail => 360 * p,
            PetState.Dance => 12 * Math.Sin(_elapsed * 4),
            PetState.Sneeze => 4 * Math.Sin(p * Math.PI * 2),
            PetState.BlowBubbles => 2 * Math.Sin(_elapsed * 2),
            PetState.Stumble => 22 * Math.Sin(p * Math.PI),
            PetState.Bow => 18 * Math.Sin(p * Math.PI),
            PetState.CheckArmor => 6 * Math.Sin(p * Math.PI),
            PetState.TiltHead => 12 * Math.Sin(p * Math.PI),
            PetState.Nod => 4 * Math.Sin(p * Math.PI * 2),
            PetState.Sway => 2.5 * Math.Sin(p * Math.PI),
            PetState.LookLeft => -2.5 * Math.Sin(p * Math.PI),
            PetState.LookRight => 2.5 * Math.Sin(p * Math.PI),
            PetState.PetShy => 9 * Math.Sin(p * Math.PI),
            PetState.PetAnnoyed => -7 * Math.Sin(p * Math.PI),
            PetState.SwordSwing => -15 + 30 * p,
            PetState.Slide => _faceLeft ? -5 : 5,
            PetState.Dragged => _faceLeft ? -4 : 4,
            PetState.IconNibble or PetState.IconEat => 2.5 * Math.Sin(_elapsed * 14),
            PetState.PlaneFlight => (_faceLeft ? -3 : 3) * Math.Sin(p * Math.PI),
            _ => 0
        };
        double scale = _state switch
        {
            PetState.Land => 1 - 0.12 * Math.Sin(p * Math.PI),
            PetState.Stretch or PetState.MorningStretch => 1 + 0.07 * Math.Sin(p * Math.PI),
            PetState.Cheer => 1 + 0.05 * Math.Abs(Math.Sin(_elapsed * 8)),
            PetState.Dance => 1 + 0.04 * Math.Abs(Math.Sin(_elapsed * 5)),
            PetState.PlayBall => 1 + 0.03 * Math.Abs(Math.Sin(_elapsed * Math.PI * 3)),
            PetState.Sneeze => 1 + 0.05 * Math.Sin(p * Math.PI * 3),
            PetState.Dream => 1 + 0.025 * Math.Sin(_elapsed * 2),
            _ => 1
        };
        scale *= IsIconAdventureActive ? 0.48 : 0.50;
        double opacity = _state == PetState.Dream ? 0.85 + 0.15 * Math.Sin(_elapsed * 2) : 1;
        string speech = _state == PetState.IconWaitForBridge
            ? "搭一座图标桥，我才能继续跳！"
            : _state == PetState.IconStuffed
                ? "吃撑啦，抓住我摇一摇！"
                : _speechTime > 0 ? _speech
                : _chatterRemaining > 0 ? _chatter : "";
        return new PetFrame(_x, _y + bob, scale, rotation, _faceLeft,
            pose, info.Prop, speech, info.Name, bob, opacity, p);
    }

    private void Advance()
    {
        if (_state == PetState.IconHungry)
        {
            if (_targetIconId is not null && _iconPlatforms.TryGetValue(_targetIconId, out IconPlatform snack))
                BeginSnackApproach(snack);
            else Enter(PetState.Stand);
            return;
        }
        if (_state == PetState.IconSeekSnack)
        {
            if (_targetIconId is not null && _iconPlatforms.TryGetValue(_targetIconId, out IconPlatform snack))
            {
                _eatenIconPlatform = snack;
                _targetIconId = null;
                _faceLeft = snack.CenterX < _x + PetWidth / 2;
                Enter(PetState.IconNibble);
            }
            else Enter(PetState.Stand);
            return;
        }
        if (_state == PetState.IconNibble)
        {
            Enter(PetState.IconEat);
            return;
        }
        if (_state == PetState.IconEat)
        {
            Enter(PetState.IconStuffed);
            return;
        }
        if (_state == PetState.IconStuffed)
        {
            // Digestion releases the current snack slot. Its desktop mask stays
            // with the UI, so later hunger can lead to another distinct icon.
            _eatenIconPlatform = null;
            _hunger = 0.08;
            if (_y < GroundY - 12) StartSequence(PetState.Drop, PetState.Land);
            else Enter(PetState.Stand);
            return;
        }
        if (_state == PetState.IconSpit)
        {
            _eatenIconPlatform = null;
            _hunger = 0.08;
            if (_pendingIconAdventureStart && !_dragging && HasStandableIcon)
            {
                // Continue directly after the spit animation. Calling the
                // public gate here would see IconSpit and queue itself again.
                BeginIconAdventure();
            }
            else
            {
                _pendingIconAdventureStart = false;
                if (_y < GroundY - 12) StartSequence(PetState.Drop, PetState.Land);
                else Enter(PetState.Stand);
            }
            return;
        }

        if (IsIconAdventureActive)
        {
            switch (_state)
            {
                case PetState.IconApproach:
                case PetState.IconJump:
                case PetState.PlaneFlight:
                    if (_targetIconId is null || !_iconPlatforms.TryGetValue(_targetIconId, out IconPlatform landed))
                    {
                        Enter(PetState.IconFall);
                        return;
                    }
                    _currentIconId = landed.Id;
                    _targetIconId = null;
                    _visitedIcons.Add(landed.Id);
                    (_x, _y) = IconAnchor(landed);
                    if (_state is PetState.IconJump or PetState.PlaneFlight) IconHopCombo++;
                    Enter(!HasStandingRoom(landed) ? PetState.IconHang :
                        _state == PetState.PlaneFlight ? PetState.PlaneLand : PetState.IconLand);
                    return;
                case PetState.PlaneFold:
                    Enter(PetState.PlaneTakeoff);
                    return;
                case PetState.PlaneTakeoff:
                    Enter(PetState.PlaneFlight);
                    return;
                case PetState.IconLand:
                case PetState.PlaneLand:
                    if (_pendingPaperPlane)
                    {
                        _pendingPaperPlane = false;
                        BeginPaperPlaneOrWait();
                    }
                    else ChooseIconActivity();
                    return;
                case PetState.IconInspect:
                case PetState.IconClimb:
                case PetState.IconHang:
                case PetState.IconRest:
                    ChooseNextIconHop();
                    return;
                case PetState.IconWaitForBridge:
                    if (FindHopTarget() is IconPlatform bridge) BeginIconHop(bridge);
                    else if (_random.NextDouble() < 0.08 && FindPlaneTarget() is IconPlatform distant)
                        BeginPaperPlane(distant);
                    else Enter(PetState.IconWaitForBridge);
                    return;
                case PetState.IconFall:
                    IconHopCombo = 0;
                    if (HasStandableIcon)
                    {
                        IconPlatform nearest = _iconPlatforms.Values
                            .Where(CanUseIcon)
                            .OrderBy(p => Math.Abs(IconAnchor(p).X - _x) + Math.Abs(IconAnchor(p).Y - _y))
                            .First();
                        BeginIconApproach(nearest);
                    }
                    else StopIconAdventure();
                    return;
            }
        }
        if (_state == PetState.Drop && _y < GroundY)
        {
            _elapsed = 0;
            _duration = Math.Max(0.4, (GroundY - _y) / 180);
            return;
        }
        if (_sequence.Count > 0)
        {
            Enter(_sequence.Dequeue());
            return;
        }
        if (_state == PetState.Stumble)
        {
            Enter(PetState.Recover);
            return;
        }
        if (_state is PetState.ClimbLeft or PetState.ClimbRight)
        {
            StartSequence(PetState.Hang, PetState.Drop, PetState.Land);
            return;
        }
        if (_state == PetState.Hang)
        {
            StartSequence(PetState.Drop, PetState.Land);
            return;
        }
        if (IsIconAdventureActive)
        {
            ChooseIconActivity();
            return;
        }
        if (_lifePerchWindowHandle != IntPtr.Zero)
        {
            ChooseLifeWindowPerchActivity();
            return;
        }
        if (_lifePerchIconId is not null)
        {
            ChooseLifePerchActivity();
            return;
        }
        ChooseAutonomousState();
    }

    private static bool IsIconMotionState(PetState state) => state is
        PetState.IconApproach or PetState.IconJump or PetState.PlaneFold or
        PetState.PlaneTakeoff or PetState.PlaneFlight;

    // The Shell reports an icon grid cell. Its visible glyph starts a few
    // pixels below the cell origin, so land at that surface instead.
    private bool HasStandingRoom(IconPlatform icon) =>
        icon.SurfaceY >= _bounds.Top + PetHeight && icon.SurfaceY <= _bounds.Bottom;

    // Top-row icons are still usable as side-hanging waypoints. Their upper
    // surfaces are above the space available for a standing pet.
    private bool CanUseIcon(IconPlatform icon) =>
        icon.Bounds.IntersectsWith(_bounds) &&
        icon.SurfaceY <= _bounds.Bottom - 12 && !IsIconCovered(icon);

    private bool IsIconCovered(IconPlatform icon)
    {
        double glyphY = icon.SurfaceY + Math.Min(20, icon.Bounds.Height * 0.25);
        return _windowPlatforms.Any(window => window.Covers(icon.CenterX, glyphY));
    }

    private WindowPlatform? FindWindowPlatform(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return null;
        foreach (WindowPlatform platform in _windowPlatforms)
            if (platform.Handle == handle) return platform;
        return null;
    }

    // Window coordinates refer to the compact pet hit box; its body is
    // centered above the icon and its feet reach the visible glyph.
    private (double X, double Y) IconAnchor(IconPlatform icon) =>
        HasStandingRoom(icon)
            ? (Math.Clamp(icon.CenterX - PetWidth / 2, _bounds.Left, MaxX),
               Math.Clamp(icon.SurfaceY - PetHeight, _bounds.Top, GroundY))
            : IconHangAnchor(icon);

    // Top-row cells leave too little room to stand above the glyph. In life
    // mode, the pet visibly clings beside the cell instead of faking a perch.
    private (double X, double Y) IconHangAnchor(IconPlatform icon)
    {
        bool hangOnRight = icon.CenterX < _bounds.Left + _bounds.Width / 2;
        double x = hangOnRight
            ? icon.Bounds.Right - PetWidth * 0.25
            : icon.Bounds.Left - PetWidth * 0.75;
        return (Math.Clamp(x, _bounds.Left, MaxX),
            Math.Clamp(icon.SurfaceY + 5, _bounds.Top, GroundY));
    }

    private bool IsFootSupportedBy(IconPlatform icon)
    {
        // Use the feet rather than the whole pet window. Walking past this
        // narrow glyph surface starts a real fall, even if grid cells overlap.
        double footX = _x + PetWidth / 2;
        double halfGlyphWidth = Math.Min(26, icon.Bounds.Width * 0.36);
        return Math.Abs(footX - icon.CenterX) <= halfGlyphWidth;
    }

    private bool TryLandOnFallingSurface(double previousFootY, double currentFootY)
    {
        // The feet must cross a top edge from above. A window moved over an
        // already-hidden icon cannot pull the pet up through its contents.
        WindowPlatform? landingWindow = null;
        IconPlatform? landingIcon = null;
        double firstTop = double.MaxValue;
        double footX = _x + PetWidth / 2;
        foreach (WindowPlatform window in _windowPlatforms)
        {
            double top = window.Bounds.Top;
            if (top < _bounds.Top + PetHeight || top >= firstTop ||
                previousFootY >= top - 1 || currentFootY < top ||
                !window.HasExposedTopAt(footX))
                continue;
            landingWindow = window;
            landingIcon = null;
            firstTop = top;
        }
        foreach (IconPlatform icon in _iconPlatforms.Values)
        {
            double top = icon.SurfaceY;
            if (top >= firstTop || previousFootY >= top - 1 ||
                currentFootY < top || !HasStandingRoom(icon) ||
                IsIconCovered(icon) || !IsFootSupportedBy(icon))
                continue;
            landingWindow = null;
            landingIcon = icon;
            firstTop = top;
        }
        if (landingWindow is WindowPlatform host)
        {
            _lifePerchWindowHandle = host.Handle;
            _lifePerchIconId = null;
            _lifePerchHanging = false;
            _y = Math.Clamp(host.Bounds.Top - PetHeight, _bounds.Top, GroundY);
            _sequence.Clear();
            Enter(PetState.Land);
            return true;
        }
        if (landingIcon is IconPlatform platform)
        {
            _lifePerchIconId = platform.Id;
            _lifePerchWindowHandle = IntPtr.Zero;
            _lifePerchHanging = false;
            _y = Math.Clamp(platform.SurfaceY - PetHeight, _bounds.Top, GroundY);
            _sequence.Clear();
            Enter(PetState.IconLand);
            return true;
        }
        return false;
    }

    private void LeaveLifeWindowPerch()
    {
        if (_lifePerchWindowHandle == IntPtr.Zero) return;
        _lifePerchWindowHandle = IntPtr.Zero;
        _sequence.Clear();
        Enter(_y < GroundY - 12 ? PetState.Drop : PetState.Land);
    }

    private void LeaveLifePerch()
    {
        if (_lifePerchIconId is null) return;
        _lifePerchIconId = null;
        _lifePerchHanging = false;
        _sequence.Clear();
        Enter(_y < GroundY - 12 ? PetState.Drop : PetState.Land);
    }

    private void ChooseLifePerchActivity()
    {
        if (_hunger >= 0.75 && _iconSnackRetrySeconds <= 0 &&
            _iconPlatforms.Values.Any(IsAvailableSnack))
        {
            _lifePerchIconId = null;
            _lifePerchHanging = false;
            ChooseAutonomousState();
            return;
        }
        if (_lifePerchHanging)
        {
            // A top-edge icon has no walking surface; after looking around,
            // she can let go and fall back to the desktop on her own.
            if (_random.NextDouble() < 0.18) LeaveLifePerch();
            else Enter(PetState.IconHang);
            return;
        }
        double choice = _random.NextDouble();
        if (choice < 0.28)
            Enter(_random.Next(2) == 0 ? PetState.WalkLeft : PetState.WalkRight);
        else if (choice < 0.44)
            Enter(PetState.IconInspect);
        else if (choice < 0.58)
            Enter(PetState.Sit);
        else if (choice < 0.72)
            Enter(PetState.Blink);
        else
            Enter(PetState.Stand);
    }

    private void ChooseLifeWindowPerchActivity()
    {
        double choice = _random.NextDouble();
        if (choice < 0.50)
            Enter(_random.Next(2) == 0 ? PetState.WalkLeft : PetState.WalkRight);
        else if (choice < 0.65)
            Enter(PetState.Sit);
        else if (choice < 0.78)
            Enter(PetState.ReadBook);
        else if (choice < 0.87)
            Enter(PetState.Blink);
        else
            Enter(PetState.Stand);
    }

    private (double X, double Y) SnackAnchor(IconPlatform icon) =>
        (Math.Clamp(icon.CenterX + 8, _bounds.Left, MaxX),
         Math.Clamp(icon.Bounds.Top + icon.Bounds.Height / 2 - PetHeight / 2,
             _bounds.Top, GroundY));

    private IconPlatform ChooseStartingPlatform() => _iconPlatforms.Values
        .Where(CanUseIcon)
        .OrderBy(p => p.Bounds.Top)
        .ThenBy(p => Math.Abs(p.CenterX - (_x + PetWidth / 2)))
        .First();

    private void BeginIconApproach(IconPlatform platform)
    {
        _sequence.Clear();
        _targetIconId = platform.Id;
        _motionStartX = _x;
        _motionStartY = _y;
        (_motionEndX, _motionEndY) = IconAnchor(platform);
        _faceLeft = _motionEndX < _x;
        Enter(PetState.IconApproach);
        double distance = Math.Sqrt(Math.Pow(_motionEndX - _x, 2) + Math.Pow(_motionEndY - _y, 2));
        _duration = Math.Clamp(distance / 160, 0.6, 6);
    }

    private void BeginIconHop(IconPlatform platform)
    {
        _sequence.Clear();
        _bridgeWaitSeconds = 0;
        BridgeWaitTimedOut = false;
        _targetIconId = platform.Id;
        _motionStartX = _x;
        _motionStartY = _y;
        (_motionEndX, _motionEndY) = IconAnchor(platform);
        _hopHeight = Math.Clamp(26 + Math.Abs(_motionEndX - _x) * 0.17, 30, 64);
        _faceLeft = _motionEndX < _x;
        Enter(PetState.IconJump);
        _duration = Math.Clamp(0.55 + Math.Abs(_motionEndX - _x) / 280, 0.55, 1.15);
    }

    private void BeginPaperPlane(IconPlatform platform)
    {
        _sequence.Clear();
        _bridgeWaitSeconds = 0;
        BridgeWaitTimedOut = false;
        _targetIconId = platform.Id;
        _motionStartX = _x;
        _motionStartY = _y;
        (_motionEndX, _motionEndY) = IconAnchor(platform);
        _hopHeight = 34;
        _faceLeft = _motionEndX < _x;
        Enter(PetState.PlaneFold);
    }

    private void BeginPaperPlaneOrWait()
    {
        if (FindPlaneTarget() is IconPlatform target) BeginPaperPlane(target);
        else Enter(PetState.IconWaitForBridge);
    }

    private void BeginSnackApproach(IconPlatform platform)
    {
        _targetIconId = platform.Id;
        _motionStartX = _x;
        _motionStartY = _y;
        (_motionEndX, _motionEndY) = SnackAnchor(platform);
        _faceLeft = _motionEndX < _x;
        Enter(PetState.IconSeekSnack);
        double distance = Math.Sqrt(Math.Pow(_motionEndX - _x, 2) + Math.Pow(_motionEndY - _y, 2));
        _duration = Math.Clamp(distance / 120, 0.7, 8);
    }

    private IconPlatform? FindHopTarget()
    {
        if (_currentIconId is null || !_iconPlatforms.TryGetValue(_currentIconId, out IconPlatform current))
            return null;
        var candidates = _iconPlatforms.Values
            .Where(p => CanUseIcon(p) &&
                        !string.Equals(p.Id, current.Id, StringComparison.OrdinalIgnoreCase) &&
                        !_visitedIcons.Contains(p.Id))
            .Select(p => new
            {
                Platform = p,
                Dx = Math.Abs(p.CenterX - current.CenterX),
                Dy = p.Bounds.Top - current.Bounds.Top
            })
            .Where(c =>
                (c.Dy is >= 25 and <= 175 && c.Dx <= 150) ||
                (Math.Abs(c.Dy) < 25 && c.Dx is >= 35 and <= 145) ||
                (c.Dy is >= -115 and <= -25 && c.Dx <= 125))
            .OrderBy(c => c.Dy >= 25 ? 0 : c.Dy > -25 ? 1 : 2)
            .ThenBy(c => c.Dy >= 25 ? c.Dy : c.Dy > -25 ? c.Dx : -c.Dy)
            .ThenBy(c => c.Dx)
            .ToList();
        if (candidates.Count == 0) return null;
        int choice = candidates.Count > 1 && _random.NextDouble() < 0.2 ? 1 : 0;
        return candidates[choice].Platform;
    }

    private IconPlatform? FindPlaneTarget()
    {
        if (_currentIconId is null || !_iconPlatforms.TryGetValue(_currentIconId, out IconPlatform current))
            return null;
        return _iconPlatforms.Values
            .Where(p => CanUseIcon(p) &&
                        !string.Equals(p.Id, current.Id, StringComparison.OrdinalIgnoreCase) &&
                        !_visitedIcons.Contains(p.Id))
            .Where(p => Math.Abs(p.CenterX - current.CenterX) is >= 165 and <= 520 &&
                        p.Bounds.Top - current.Bounds.Top is >= -15 and <= 90)
            .OrderBy(p => Math.Abs(p.CenterX - current.CenterX))
            .Select(p => (IconPlatform?)p)
            .FirstOrDefault();
    }

    private void ChooseNextIconHop()
    {
        if (_currentIconId is null || !_iconPlatforms.ContainsKey(_currentIconId))
        {
            if (HasStandableIcon)
                BeginIconApproach(ChooseStartingPlatform());
            else StopIconAdventure();
            return;
        }
        if (FindHopTarget() is IconPlatform next) BeginIconHop(next);
        else Enter(PetState.IconWaitForBridge);
    }

    private void ChooseIconActivity()
    {
        if (_currentIconId is null)
        {
            ChooseNextIconHop();
            return;
        }
        if (_iconPlatforms.TryGetValue(_currentIconId, out IconPlatform platform) &&
            !HasStandingRoom(platform))
        {
            // Hanging cells have no upper surface for a sitting pose.
            if (_random.NextDouble() < 0.3) Enter(PetState.IconHang);
            else ChooseNextIconHop();
            return;
        }
        double r = _random.NextDouble();
        if (r < 0.18) Enter(PetState.IconInspect);
        else if (r < 0.30) Enter(PetState.IconClimb);
        else if (r < 0.40) Enter(PetState.IconHang);
        else if (r < 0.52) Enter(PetState.IconRest);
        else ChooseNextIconHop();
    }

    private void ChooseAutonomousState()
    {
        if (_eatenIconPlatform.HasValue)
        {
            Enter(PetState.IconStuffed);
            return;
        }
        if (_hunger >= 0.75 && _iconSnackRetrySeconds <= 0 &&
            _iconPlatforms.Values.Any(IsAvailableSnack))
        {
            IconPlatform snack = _iconPlatforms.Values
                .Where(IsAvailableSnack)
                .OrderBy(p => Math.Abs(IconAnchor(p).X - _x) +
                              Math.Abs(IconAnchor(p).Y - _y))
                .First();
            _targetIconId = snack.Id;
            Enter(PetState.IconHungry);
            return;
        }
        if (_hunger >= 0.85 && _iconSnackRetrySeconds <= 0 &&
            !_iconPlatforms.Values.Any(IsAvailableSnack))
        {
            // No edible shortcut is visible, so she makes her own rice.
            _hunger = 0.05;
            StartSequence(PetState.CookRice, PetState.EatRice, PetState.RiceStuffed);
            return;
        }
        if (UseDailyRoutine && _random.NextDouble() < 0.3 &&
            PickRoutineState() is PetState routine)
        {
            StartRoutineState(routine);
            return;
        }
        if (_mood <= 0.28 && _random.NextDouble() < 0.4)
        {
            Enter(Pick(PetState.Sigh, PetState.Pout, PetState.Daydream,
                PetState.Sit, PetState.Stargaze));
            return;
        }
        if (_mood >= 0.78 && _random.NextDouble() < 0.3)
        {
            StartSequence(Pick(PetState.Dance, PetState.Sing, PetState.PlayBall,
                PetState.BlowBubbles, PetState.ChaseTail), PetState.Cheer);
            return;
        }
        if (_random.NextDouble() < 0.05)
        {
            if (_random.NextDouble() < 0.65)
                Trigger(PetAction.Sweep);
            else
                Enter(PetState.WipeDesk);
            return;
        }
        double r = _random.NextDouble();
        if (AllowShortcutPlay && r < 0.025)
        {
            Trigger(PetAction.Shortcut);
            return;
        }
        if (r < 0.065)
        {
            Trigger(PetAction.Fish);
            return;
        }
        if (r < 0.095)
        {
            Trigger(PetAction.Sword);
            return;
        }
        if (_energy < 0.35 && r < 0.35 || r is >= 0.095 and < 0.125)
        {
            Trigger(PetAction.Nap);
            return;
        }
        if (r < 0.18)
        {
            StartSequence(Pick(PetState.Sit, PetState.Stretch, PetState.TeaBreak,
                PetState.ReadBook, PetState.RollOver));
            return;
        }
        if (r < 0.30)
        {
            StartSequence(Pick(PetState.Wave, PetState.Bow, PetState.Cheer,
                PetState.Spin, PetState.Sparkle, PetState.Laugh));
            return;
        }
        if (r < 0.59 && _consecutiveMotion < 3)
        {
            _consecutiveMotion++;
            double leftRoom = _x - _bounds.Left;
            double rightRoom = MaxX - _x;
            bool goLeft = _faceLeft;
            if (leftRoom < 85 || rightRoom < 85)
                goLeft = leftRoom > rightRoom;
            else if (_consecutiveMotion == 1 && _random.NextDouble() < 0.08)
                goLeft = !goLeft;

            PetState next = ChooseTravelState(goLeft);
            if (goLeft != _faceLeft)
                StartSequence(PetState.TurnAround, next);
            else
                Enter(next);
            return;
        }
        _consecutiveMotion = 0;
        if ((_x - _bounds.Left < 25 || MaxX - _x < 25) && r > 0.94)
        {
            Enter(_x - _bounds.Left < 25 ? PetState.ClimbLeft : PetState.ClimbRight);
            return;
        }
        PetState[] idle =
        [
            PetState.Stand, PetState.Blink, PetState.LookLeft, PetState.LookRight,
            PetState.TiltHead, PetState.AdjustRibbon, PetState.CheckArmor,
            PetState.CheckSword, PetState.Sway, PetState.Hum, PetState.Think,
            PetState.Smile, PetState.Pout, PetState.Salute, PetState.Nod, PetState.Yawn,
            PetState.Daydream, PetState.PhoneScroll, PetState.BlowBubbles,
            PetState.Sneeze
        ];
        Enter(idle[_random.Next(idle.Length)]);
    }

    /// <summary>Pick an activity that fits the current time of day, or null for no bias.</summary>
    private PetState? PickRoutineState()
    {
        int hour = DateTime.Now.Hour;
        if (hour is >= 5 and < 11)
            return Pick(PetState.MorningStretch, PetState.Hum,
                _hunger > 0.45 ? PetState.CookRice : PetState.Sunbathe, PetState.Blink);
        if (hour is >= 11 and < 14)
            return Pick(_hunger > 0.4 ? PetState.CookRice : PetState.TeaBreak,
                PetState.Sit, PetState.BlowBubbles, PetState.Sing);
        if (hour is >= 14 and < 18)
            return Pick(PetState.TeaBreak, PetState.ReadBook, PetState.WipeDesk,
                PetState.PhoneScroll, PetState.Sing);
        if (hour is >= 18 and < 22)
            return Pick(PetState.Stargaze, PetState.TeaBreak, PetState.ReadBook,
                PetState.Smile, PetState.Dance);
        return Pick(PetState.Yawn, PetState.Nap, PetState.Stargaze,
            PetState.Dream, PetState.LieDown);
    }

    private void StartRoutineState(PetState state)
    {
        switch (state)
        {
            case PetState.CookRice:
                _hunger = 0.05;
                StartSequence(PetState.CookRice, PetState.EatRice, PetState.RiceStuffed);
                break;
            case PetState.Nap or PetState.LieDown:
                StartSequence(PetState.Yawn, PetState.LieDown, PetState.Nap,
                    PetState.Dream, PetState.Wake);
                break;
            case PetState.MorningStretch:
                StartSequence(PetState.Yawn, PetState.MorningStretch, PetState.Hum);
                break;
            default:
                Enter(state);
                break;
        }
    }

    private bool IsAvailableSnack(IconPlatform icon) =>
        CanUseIcon(icon) && !_storedSnackIconIds.Contains(icon.Id);

    private PetState ChooseTravelState(bool left)
    {
        double r = _random.NextDouble();
        if (_energy < 0.3 || r < 0.46)
            return left ? PetState.WalkLeft : PetState.WalkRight;
        if (r < 0.64)
            return left ? PetState.TiptoeLeft : PetState.TiptoeRight;
        if (r < 0.78)
            return left ? PetState.RunLeft : PetState.RunRight;
        if (r < 0.87)
            return left ? PetState.HopLeft : PetState.HopRight;
        if (r < 0.94)
            return left ? PetState.SkipLeft : PetState.SkipRight;
        if (r < 0.97)
            return left ? PetState.SprintLeft : PetState.SprintRight;
        if (r < 0.985)
            return left ? PetState.DashLeft : PetState.DashRight;
        return r < 0.995 ? PetState.Slide : PetState.Stumble;
    }

    private PetState Pick(params PetState[] values) => values[_random.Next(values.Length)];

    private string Pick(params string[] values) => values[_random.Next(values.Length)];

    private void StartSequence(params PetState[] states)
    {
        if (states.Length == 0) return;
        _sequence.Clear();
        foreach (PetState state in states.Skip(1)) _sequence.Enqueue(state);
        Enter(states[0]);
    }

    private void Enter(PetState state)
    {
        _state = state;
        _elapsed = 0;
        StateInfo info = Info[state];
        _duration = info.MinSeconds + _random.NextDouble() * (info.MaxSeconds - info.MinSeconds);
        if (state == PetState.Dragged) _duration = double.MaxValue;
        if (state == PetState.IconFall) _motionStartY = _y;
        if (state is PetState.WalkLeft or PetState.RunLeft or PetState.SprintLeft or
            PetState.TiptoeLeft or PetState.HopLeft or PetState.SkipLeft or
            PetState.DashLeft or PetState.SweepLeft)
            _faceLeft = true;
        if (state is PetState.WalkRight or PetState.RunRight or PetState.SprintRight or
            PetState.TiptoeRight or PetState.HopRight or PetState.SkipRight or
            PetState.DashRight or PetState.SweepRight)
            _faceLeft = false;
        // Mood follows what the pet is doing, not only player interaction.
        if (state is PetState.Cheer or PetState.PetHappy or PetState.Laugh or
            PetState.Dance or PetState.Sing or PetState.PlayBall or
            PetState.Sparkle or PetState.FishCelebrate)
            _mood = Math.Min(1, _mood + 0.012);
        if (state is PetState.Pout or PetState.PetAnnoyed or PetState.Sigh or
            PetState.Stumble)
            _mood = Math.Max(0, _mood - 0.008);
        if (state == PetState.Dream)
            _mood = Math.Min(1, _mood + 0.01);
        if (state == PetState.RiceStuffed)
        {
            _hunger = 0.05;
            _mood = Math.Min(1, _mood + 0.1);
        }
        _chatterRemaining = 0;
        _speech = info.Speech;
        _speechTime = string.IsNullOrEmpty(_speech) ? 0 : Math.Min(2, _duration);
        StateChanged?.Invoke(state);
    }

    private readonly record struct StateInfo(
        string Name, PetPose Pose, PetProp Prop, double MinSeconds,
        double MaxSeconds, string Speech = "");

    private static readonly IReadOnlyDictionary<PetState, StateInfo> Info = BuildInfo();

    private static IReadOnlyDictionary<PetState, StateInfo> BuildInfo()
    {
        var d = new Dictionary<PetState, StateInfo>();
        void Add(PetState state, string name, PetPose pose = PetPose.Stand,
            PetProp prop = PetProp.None, double min = 0.7, double max = 1.6,
            string speech = "") => d[state] = new StateInfo(name, pose, prop, min, max, speech);

        Add(PetState.Stand, "站立", min: 1.3, max: 3.5);
        Add(PetState.Blink, "眨眼", min: 0.3, max: 0.5);
        Add(PetState.LookLeft, "向左张望", min: 0.7, max: 1.2);
        Add(PetState.LookRight, "向右张望", min: 0.7, max: 1.2);
        Add(PetState.TiltHead, "歪头", min: 0.9, max: 1.5, speech: "嗯？");
        Add(PetState.AdjustRibbon, "拍拍鱼鳍", PetPose.Wave, min: 0.8, max: 1.4);
        Add(PetState.CheckArmor, "摸摸圆肚皮", PetPose.Bow, min: 0.8, max: 1.4);
        Add(PetState.CheckSword, "端起白饭", PetPose.Sword, PetProp.Sword, 1, 1.7);
        Add(PetState.Sway, "轻轻摇晃", min: 1.2, max: 2.2);
        Add(PetState.Hum, "哼歌", prop: PetProp.MusicNote, min: 1.5, max: 2.5, speech: "♪");
        Add(PetState.Think, "思考", prop: PetProp.Question, min: 1.1, max: 2);
        Add(PetState.Smile, "微笑", min: 0.9, max: 1.6);
        Add(PetState.Pout, "鼓脸", min: 0.8, max: 1.4, speech: "哼~");
        Add(PetState.Salute, "敬礼", PetPose.Wave, min: 0.8, max: 1.3);
        Add(PetState.Nod, "点头", min: 0.5, max: 1);
        Add(PetState.Yawn, "打哈欠", PetPose.Stretch, min: 1, max: 1.7, speech: "哈啊……");
        Add(PetState.MorningStretch, "早安伸展", PetPose.Stretch, min: 1.3, max: 2.1, speech: "早上好~");
        Add(PetState.Sneeze, "打喷嚏", min: 0.6, max: 1, speech: "阿嚏！");
        Add(PetState.Sigh, "叹气", min: 1.2, max: 2, speech: "唉……");

        Add(PetState.WalkLeft, "向左散步", PetPose.Walk, min: 1.5, max: 3.4);
        Add(PetState.WalkRight, "向右散步", PetPose.Walk, min: 1.5, max: 3.4);
        Add(PetState.RunLeft, "向左跑", PetPose.Run, min: 1.1, max: 2.5);
        Add(PetState.RunRight, "向右跑", PetPose.Run, min: 1.1, max: 2.5);
        Add(PetState.SprintLeft, "向左冲刺", PetPose.Run, min: 0.6, max: 1.1);
        Add(PetState.SprintRight, "向右冲刺", PetPose.Run, min: 0.6, max: 1.1);
        Add(PetState.TiptoeLeft, "向左踮脚", PetPose.Tiptoe, min: 1.2, max: 2.2);
        Add(PetState.TiptoeRight, "向右踮脚", PetPose.Tiptoe, min: 1.2, max: 2.2);
        Add(PetState.HopLeft, "向左蹦跳", PetPose.Jump, min: 0.9, max: 1.4);
        Add(PetState.HopRight, "向右蹦跳", PetPose.Jump, min: 0.9, max: 1.4);
        Add(PetState.SkipLeft, "向左连跳", PetPose.Jump, min: 1.3, max: 2);
        Add(PetState.SkipRight, "向右连跳", PetPose.Jump, min: 1.3, max: 2);
        Add(PetState.DashLeft, "向左疾走", PetPose.Run, min: 0.6, max: 1);
        Add(PetState.DashRight, "向右疾走", PetPose.Run, min: 0.6, max: 1);
        Add(PetState.Slide, "滑步", PetPose.Run, min: 0.7, max: 1.1);
        Add(PetState.SweepLeft, "向左扫地", PetPose.Walk, PetProp.Broom, 1.8, 3);
        Add(PetState.SweepRight, "向右扫地", PetPose.Walk, PetProp.Broom, 1.8, 3);
        Add(PetState.TurnAround, "转身", min: 0.4, max: 0.7);
        Add(PetState.Stumble, "绊了一下", PetPose.Fall, min: 0.5, max: 0.8, speech: "哎呀！");
        Add(PetState.Recover, "站稳", PetPose.Stand, min: 0.5, max: 0.8);

        Add(PetState.Sit, "坐下休息", PetPose.Sit, min: 1.8, max: 3.2);
        Add(PetState.Stretch, "伸懒腰", PetPose.Stretch, min: 1.2, max: 2);
        Add(PetState.LieDown, "躺下", PetPose.Lie, min: 0.7, max: 1.2);
        Add(PetState.Nap, "午睡", PetPose.Sleep, PetProp.Pillow, 4, 7, "Zzz");
        Add(PetState.Dream, "做梦", PetPose.Sleep, PetProp.Sparkle, 1.8, 3);
        Add(PetState.Wake, "醒来", PetPose.Stretch, min: 0.9, max: 1.5);
        Add(PetState.RollOver, "翻滚", PetPose.Spin, min: 0.8, max: 1.2);
        Add(PetState.TeaBreak, "喝茶摸鱼", PetPose.Drink, PetProp.TeaCup, 2.5, 4.5, "休息一下~");
        Add(PetState.ReadBook, "坐着看书", PetPose.Read, PetProp.Book, 2.5, 4.5);
        Add(PetState.FishCast, "甩出钓竿", PetPose.Fish, PetProp.FishingRod, 0.8, 1.3);
        Add(PetState.FishWait, "桌面钓鱼摸鱼", PetPose.Fish, PetProp.FishingRod, 3.5, 6, "今天也要摸鱼！");
        Add(PetState.FishBite, "鱼儿上钩", PetPose.Fish, PetProp.FishingRod, 0.5, 0.9, "咦？");
        Add(PetState.FishReel, "收线", PetPose.Fish, PetProp.Fish, 0.8, 1.4);
        Add(PetState.FishCelebrate, "钓鱼成功", PetPose.Cheer, PetProp.Fish, 1.1, 1.8, "抓到了！");

        Add(PetState.Wave, "挥手", PetPose.Wave, min: 0.9, max: 1.5, speech: "你好！");
        Add(PetState.Bow, "鞠躬", PetPose.Bow, min: 0.9, max: 1.4);
        Add(PetState.Spin, "转圈", PetPose.Spin, min: 0.8, max: 1.2);
        Add(PetState.Cheer, "欢呼", PetPose.Cheer, PetProp.Sparkle, 1, 1.7, "耶！");
        Add(PetState.SwordPose, "举起饭勺", PetPose.Sword, PetProp.Sword, 1.1, 1.7, "开饭啦！");
        Add(PetState.SwordSwing, "舀一口白饭", PetPose.Sword, PetProp.Sword, 0.55, 0.85);
        Add(PetState.SwordSheathe, "放下饭勺", PetPose.Sword, PetProp.Sword, 0.6, 1);
        Add(PetState.PetHappy, "被摸头很开心", PetPose.Cheer, PetProp.Heart, 1, 1.8, "嘿嘿~");
        Add(PetState.PetShy, "被摸头害羞", PetPose.Stand, PetProp.Heart, 1, 1.7, "唔……");
        Add(PetState.PetAnnoyed, "被摸头闹别扭", PetPose.Stand, min: 0.9, max: 1.5, speech: "别闹啦！");
        Add(PetState.Laugh, "笑出声", PetPose.Cheer, min: 0.9, max: 1.5, speech: "哈哈！");
        Add(PetState.Sparkle, "闪亮登场", PetPose.Cheer, PetProp.Sparkle, 0.9, 1.5);
        Add(PetState.Daydream, "发呆", PetPose.Sit, PetProp.Question, 2.5, 4.5, "……");
        Add(PetState.Stargaze, "看星星", PetPose.Sit, PetProp.Sparkle, 3, 5, "星星真好看~");
        Add(PetState.Sunbathe, "晒太阳", PetPose.Lie, PetProp.Sparkle, 3, 5, "暖洋洋的~");
        Add(PetState.Sing, "唱歌", PetPose.Cheer, PetProp.MusicNote, 2.4, 4, "♪ 白饭白饭~");
        Add(PetState.Dance, "跳舞", PetPose.Spin, PetProp.MusicNote, 2, 3.2, "一起跳舞吧！");
        Add(PetState.PlayBall, "玩球", PetPose.Jump, PetProp.Ball, 1.8, 3, "接住！");
        Add(PetState.ChaseTail, "追尾巴", PetPose.Spin, min: 1.2, max: 2, speech: "转呀转~");
        Add(PetState.BlowBubbles, "吹泡泡", PetPose.Stand, PetProp.Bubble, 2.4, 4, "啵~");
        Add(PetState.PhoneScroll, "刷手机", PetPose.Sit, PetProp.Phone, 2.5, 4.5, "嘿嘿…");
        Add(PetState.WipeDesk, "擦桌面", PetPose.Carry, PetProp.Cloth, 2, 3.4, "擦得亮亮的~");
        Add(PetState.CookRice, "淘米煮饭", PetPose.Sword, PetProp.Rice, 2, 3.4, "今天也要好好吃饭！");
        Add(PetState.EatRice, "吃白饭", PetPose.Sword, PetProp.Rice, 1.4, 2.4, "啊呜~ 好吃！");
        Add(PetState.RiceStuffed, "吃饱啦", PetPose.Sit, PetProp.Heart, 2.2, 3.4, "吃饱啦~");

        Add(PetState.PickShortcut, "拿起快捷方式分身", PetPose.Carry, PetProp.Shortcut, 0.9, 1.4, "借我看看~");
        Add(PetState.CarryShortcut, "搬运快捷方式分身", PetPose.Carry, PetProp.Shortcut, 2, 3.2);
        Add(PetState.InspectShortcut, "研究快捷方式分身", PetPose.Carry, PetProp.Shortcut, 1, 1.7, "是什么呢？");
        Add(PetState.ReturnShortcut, "放回快捷方式分身", PetPose.Carry, PetProp.Shortcut, 0.8, 1.2, "还给你！");

        Add(PetState.ClimbLeft, "攀爬左边缘", PetPose.Climb, min: 1.8, max: 3);
        Add(PetState.ClimbRight, "攀爬右边缘", PetPose.Climb, min: 1.8, max: 3);
        Add(PetState.Hang, "挂在边缘", PetPose.Climb, min: 1, max: 2);
        Add(PetState.Drop, "落下", PetPose.Fall, min: 1, max: 3);
        Add(PetState.Land, "着陆", PetPose.Fall, min: 0.4, max: 0.7);
        Add(PetState.Dragged, "被拎起来", PetPose.Fall, min: 10, max: 10, speech: "欸欸？");

        Add(PetState.IconApproach, "靠近图标", PetPose.Run, min: 0.5, max: 1.5);
        Add(PetState.IconJump, "跳向下一图标", PetPose.Jump, min: 0.6, max: 1.1);
        Add(PetState.IconLand, "落在图标旁", PetPose.Sit, min: 0.25, max: 0.45);
        Add(PetState.IconInspect, "检查图标平台", PetPose.Stand, PetProp.Question, 0.8, 1.4);
        Add(PetState.IconNibble, "啃图标分身", PetPose.Carry, min: 0.6, max: 1.1, speech: "啊呜~");
        Add(PetState.IconClimb, "攀爬图标", PetPose.Climb, min: 0.9, max: 1.5);
        Add(PetState.IconHang, "挂在图标边", PetPose.Climb, min: 0.8, max: 1.4);
        Add(PetState.IconFall, "从图标滑落", PetPose.Fall, min: 0.5, max: 0.8, speech: "哎呀！");
        Add(PetState.IconRest, "图标旁休息", PetPose.Sit, min: 0.8, max: 1.4);
        Add(PetState.IconWaitForBridge, "等待图标桥", PetPose.Sit, min: 2.4, max: 3.6,
            speech: "搭一座图标桥，我才能继续跳！");
        Add(PetState.PlaneFold, "折纸飞机", PetPose.Sit, PetProp.PaperPlane, 0.7, 1.1, "纸飞机出发！");
        Add(PetState.PlaneTakeoff, "纸飞机起飞", PetPose.Jump, PetProp.PaperPlane, 0.35, 0.55);
        Add(PetState.PlaneFlight, "乘纸飞机飞行", PetPose.Jump, PetProp.PaperPlane, 1.0, 1.6);
        Add(PetState.PlaneLand, "纸飞机着陆", PetPose.Sit, PetProp.PaperPlane, 0.4, 0.7);

        Add(PetState.IconHungry, "肚子饿了", PetPose.Stand, min: 0.7, max: 1.1, speech: "有点饿了……");
        Add(PetState.IconSeekSnack, "寻找图标点心", PetPose.Run, min: 1.0, max: 2.5);
        Add(PetState.IconEat, "吃掉图标分身", PetPose.Carry, min: 0.8, max: 1.3, speech: "啊呜！");
        Add(PetState.IconStuffed, "吃撑了", PetPose.Sit, min: 10, max: 14,
            speech: "抓住我摇一摇！");
        Add(PetState.IconSpit, "吐出图标分身", PetPose.Fall, min: 0.8, max: 1.2,
            speech: "哇，吐出来啦！");
        if (d.Count != Enum.GetValues<PetState>().Length)
            throw new InvalidOperationException("Missing pet state metadata.");
        return d;
    }
}
