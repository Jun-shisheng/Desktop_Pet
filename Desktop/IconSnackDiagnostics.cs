using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;

namespace DeepSeekFishPet.Desktop;

/// <summary>Small failure log for the desktop-icon eating effect.</summary>
internal static class IconSnackDiagnostics
{
    private static readonly object Gate = new();

    internal static string? LogPath
    {
        get
        {
            string root = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrWhiteSpace(root) ? null :
                Path.Combine(root, "DeepSeekFishPet", "diagnostics.log");
        }
    }

    internal static void Failure(string stage, IntPtr hwnd = default,
        Rect? bounds = null, int errorCode = 0)
    {
        try
        {
            string rectangle = bounds is Rect area
                ? string.Format(CultureInfo.InvariantCulture,
                    " rect={0:0.##},{1:0.##},{2:0.##},{3:0.##}",
                    area.Left, area.Top, area.Width, area.Height)
                : string.Empty;
            string line = string.Format(CultureInfo.InvariantCulture,
                "{0:O} stage={1} hwnd=0x{2:X} code={3}{4}{5}",
                DateTimeOffset.UtcNow, stage, hwnd.ToInt64(), errorCode,
                rectangle, Environment.NewLine);
            lock (Gate)
            {
                string? path = LogPath;
                if (path is null) return;
                string? directory = Path.GetDirectoryName(path);
                if (directory is null) return;
                Directory.CreateDirectory(directory);
                File.AppendAllText(path, line, Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   ArgumentException or NotSupportedException)
        {
            // Diagnostics must never prevent the original icon from returning.
        }
    }
}
