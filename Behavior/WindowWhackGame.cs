using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using DeepSeekFishPet.Desktop;
using Forms = System.Windows.Forms;
using Point = System.Windows.Point;

namespace DeepSeekFishPet.Behavior;

public enum PeekSide
{
    Top,
    Left,
    Right
}

/// <summary>
/// A head peek anchored to a window border. BorderPoint and HostBounds are
/// physical screen pixels, matching TopLevelWindowService.GetCandidates.
/// </summary>
public readonly record struct WindowWhackTarget(
    IntPtr WindowHandle, Point BorderPoint, PeekSide Side, Rect HostBounds);

/// <summary>
/// A timed, read-only "whack the mole" game. The caller supplies fresh window
/// snapshots; this controller never activates, moves, or changes those windows.
/// </summary>
public sealed class WindowWhackGame
{
    public const double RoundDurationSeconds = 10;
    public const double PeekSize = 100;
    public const double PeekOutsidePixels = 82;

    private const double PeekInsidePixels = PeekSize - PeekOutsidePixels;
    private const double BorderStep = 12;

    private readonly record struct PeekPlacement(PeekSide Side, Point BorderPoint, double Fraction);

    private readonly Random _random;
    private IntPtr _hostHandle;
    private IntPtr _previousHostHandle;
    private double _phaseRemaining;
    private bool _visible;

    public WindowWhackGame(int? seed = null)
    {
        _random = seed.HasValue ? new Random(seed.Value) : new Random();
    }

    public bool IsActive { get; private set; }
    public bool IsPeekVisible => IsActive && _visible && Target.HasValue;
    public WindowWhackTarget? Target { get; private set; }
    public int Score { get; private set; }
    public int Misses { get; private set; }
    public double TimeRemaining { get; private set; }

    /// <summary>Edges that can show the complete 100px head on screen.</summary>
    public static IReadOnlyList<PeekSide> GetViableSides(PeekWindow host) =>
        GetViablePlacements(host).Select(p => p.Side).Distinct().ToArray();

    /// <summary>
    /// Picks a visible border point with 82px of space outside the host.
    /// The caller may use this for life-mode peeks as well as the timed game.
    /// </summary>
    public static bool TryCreateTarget(PeekWindow host, Random random, out WindowWhackTarget target)
    {
        ArgumentNullException.ThrowIfNull(random);
        List<PeekPlacement> placements = GetViablePlacements(host);
        if (placements.Count == 0)
        {
            target = default;
            return false;
        }
        PeekSide[] sides = placements.Select(p => p.Side).Distinct().ToArray();
        PeekSide side = sides[random.Next(sides.Length)];
        PeekPlacement[] choices = placements.Where(p => p.Side == side).ToArray();
        target = ToTarget(host, choices[random.Next(choices.Length)]);
        return true;
    }

    /// <summary>
    /// Follows a moved or resized host on the same edge when possible. If that
    /// edge becomes hidden or leaves the screen, chooses another visible edge.
    /// </summary>
    public static bool TryRefreshTarget(PeekWindow host, WindowWhackTarget previous,
        Random random, out WindowWhackTarget target)
    {
        ArgumentNullException.ThrowIfNull(random);
        if (host.Handle != previous.WindowHandle)
        {
            target = default;
            return false;
        }
        List<PeekPlacement> placements = GetViablePlacements(host);
        if (placements.Count == 0)
        {
            target = default;
            return false;
        }
        double previousFraction = previous.Side == PeekSide.Top
            ? (previous.BorderPoint.X - previous.HostBounds.Left) / previous.HostBounds.Width
            : (previous.BorderPoint.Y - previous.HostBounds.Top) / previous.HostBounds.Height;
        PeekPlacement[] sameSide = placements.Where(p => p.Side == previous.Side).ToArray();
        if (sameSide.Length > 0)
        {
            PeekPlacement nearest = sameSide.MinBy(p => Math.Abs(p.Fraction - previousFraction));
            target = ToTarget(host, nearest);
            return true;
        }
        PeekSide[] sides = placements.Select(p => p.Side).Distinct().ToArray();
        PeekSide side = sides[random.Next(sides.Length)];
        PeekPlacement[] choices = placements.Where(p => p.Side == side).ToArray();
        target = ToTarget(host, choices[random.Next(choices.Length)]);
        return true;
    }

    /// <summary>Starts a fresh round. Returns false if there is no eligible window.</summary>
    public bool Start(IReadOnlyList<PeekWindow> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        Stop();
        Score = 0;
        Misses = 0;
        TimeRemaining = RoundDurationSeconds;
        IsActive = true;
        if (ShowNext(windows)) return true;
        Stop();
        TimeRemaining = 0;
        return false;
    }

    /// <summary>Ends a round, preserving score and remaining time for display.</summary>
    public void Stop()
    {
        IsActive = false;
        Hide();
        _phaseRemaining = 0;
        _previousHostHandle = IntPtr.Zero;
    }

    /// <summary>
    /// Advances the round and follows a moved or resized host window. An
    /// ineligible host hides its peek without counting as a miss, then the
    /// game selects another window. If none remain, the round ends.
    /// </summary>
    public void Update(double elapsedSeconds, IReadOnlyList<PeekWindow> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        if (!IsActive) return;
        if (double.IsNaN(elapsedSeconds) || double.IsInfinity(elapsedSeconds) || elapsedSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));

        if (_visible)
        {
            PeekWindow? host = FindHost(windows, _hostHandle);
            if (host is PeekWindow current && Target is WindowWhackTarget previous &&
                TryRefreshTarget(current, previous, _random, out WindowWhackTarget refreshed))
                Target = refreshed;
            else
            {
                Hide();
                if (!ShowNext(windows))
                {
                    Stop();
                    return;
                }
            }
        }
        else if (windows.Count == 0)
        {
            Stop();
            return;
        }

        double remainingStep = Math.Min(elapsedSeconds, TimeRemaining);
        while (remainingStep > 0 && IsActive)
        {
            double consumed = Math.Min(remainingStep, _phaseRemaining);
            _phaseRemaining -= consumed;
            TimeRemaining -= consumed;
            remainingStep -= consumed;

            if (TimeRemaining <= 0)
            {
                TimeRemaining = 0;
                Stop();
                break;
            }
            if (_phaseRemaining > 0) break;

            if (_visible)
            {
                Misses++;
                Hide();
                _phaseRemaining = NextHiddenDuration();
            }
            else if (!ShowNext(windows))
            {
                Stop();
                break;
            }
        }
    }

    /// <summary>Scores a click on a visible head and immediately hides it.</summary>
    public bool Hit()
    {
        if (!IsPeekVisible) return false;
        Score++;
        Hide();
        _phaseRemaining = NextHiddenDuration();
        return true;
    }

    private bool ShowNext(IReadOnlyList<PeekWindow> windows)
    {
        PeekWindow[] viable = windows.Where(w => GetViableSides(w).Count > 0).ToArray();
        if (viable.Length == 0) return false;
        PeekWindow[] alternatives = viable.Where(w => w.Handle != _previousHostHandle).ToArray();
        IReadOnlyList<PeekWindow> choices = alternatives.Length > 0 ? alternatives : viable;
        PeekWindow selected = choices[_random.Next(choices.Count)];
        if (!TryCreateTarget(selected, _random, out WindowWhackTarget target)) return false;
        _hostHandle = selected.Handle;
        _previousHostHandle = selected.Handle;
        Target = target;
        _visible = true;
        _phaseRemaining = 1.2 + _random.NextDouble() * 1.2;
        return true;
    }

    private void Hide()
    {
        _visible = false;
        Target = null;
        _hostHandle = IntPtr.Zero;
    }

    private double NextHiddenDuration() => 0.4 + _random.NextDouble() * 0.7;

    private static PeekWindow? FindHost(IReadOnlyList<PeekWindow> windows, IntPtr handle)
    {
        foreach (PeekWindow candidate in windows)
            if (candidate.Handle == handle) return candidate;
        return null;
    }

    private static WindowWhackTarget ToTarget(PeekWindow host, PeekPlacement placement) =>
        new(host.Handle, placement.BorderPoint, placement.Side, host.Bounds);

    private static List<PeekPlacement> GetViablePlacements(PeekWindow host)
    {
        var placements = new List<PeekPlacement>();
        if (host.Bounds.IsEmpty || host.Bounds.Width < PeekSize || host.Bounds.Height < PeekSize)
            return placements;
        Rect screen = host.ScreenBounds;
        if (screen.IsEmpty || screen.Width <= 0 || screen.Height <= 0)
        {
            var monitor = Forms.Screen.FromHandle(host.Handle).Bounds;
            screen = new Rect(monitor.Left, monitor.Top, monitor.Width, monitor.Height);
        }
        AddSide(PeekSide.Top, host.Bounds.Width);
        AddSide(PeekSide.Left, host.Bounds.Height);
        AddSide(PeekSide.Right, host.Bounds.Height);
        return placements;

        void AddSide(PeekSide side, double length)
        {
            double start = Math.Max(PeekSize / 2, length * 0.18);
            double end = Math.Min(length - PeekSize / 2, length * 0.82);
            if (end < start) return;
            for (double offset = start; offset <= end + 0.01; offset += BorderStep)
                AddIfViable(side, offset, length);
            if (end - start > 0.01)
                AddIfViable(side, end, length);
        }

        void AddIfViable(PeekSide side, double offset, double length)
        {
            Rect bounds = host.Bounds;
            Point border = side switch
            {
                PeekSide.Left => new Point(bounds.Left, bounds.Top + offset),
                PeekSide.Right => new Point(bounds.Right, bounds.Top + offset),
                _ => new Point(bounds.Left + offset, bounds.Top)
            };
            Rect peek = side switch
            {
                PeekSide.Left => new Rect(border.X - PeekOutsidePixels,
                    border.Y - PeekSize / 2, PeekSize, PeekSize),
                PeekSide.Right => new Rect(border.X - PeekInsidePixels,
                    border.Y - PeekSize / 2, PeekSize, PeekSize),
                _ => new Rect(border.X - PeekSize / 2,
                    border.Y - PeekOutsidePixels, PeekSize, PeekSize)
            };
            if (!screen.Contains(peek)) return;
            Point exposedBorder = side switch
            {
                PeekSide.Left => new Point(border.X + 3, border.Y),
                PeekSide.Right => new Point(border.X - 3, border.Y),
                _ => new Point(border.X, border.Y + 3)
            };
            if (host.OccluderBounds is not null &&
                host.OccluderBounds.Any(cover => cover.Contains(exposedBorder)))
                return;
            placements.Add(new PeekPlacement(side, border, offset / length));
        }
    }
}
