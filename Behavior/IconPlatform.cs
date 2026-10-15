using System.Windows;

namespace DeepSeekFishPet.Behavior;

/// <summary>A read-only snapshot of a visible Explorer desktop icon cell in physical screen pixels.</summary>
public readonly record struct IconPlatform(string Id, string Name, Rect Bounds)
{
    public double CenterX => Bounds.Left + Bounds.Width / 2;
    // Explorer positions a cell that includes whitespace above its visible icon.
    public double SurfaceY => Bounds.Top + System.Math.Clamp(Bounds.Height * 0.08, 6, 10);
}
