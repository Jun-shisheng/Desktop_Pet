using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DeepSeekFishPet.Desktop;

/// <summary>A desktop shortcut and a frozen copy of its Explorer icon.</summary>
public sealed record DesktopShortcut(string Name, string FullPath, ImageSource Icon);

/// <summary>
/// Reads shortcut icons from the current user's and the shared desktop folders.
/// It never opens, moves, renames, or deletes a shortcut. Call Refresh after the
/// desktop changes; random selections use the cached snapshot.
/// </summary>
public sealed class DesktopShortcutService
{
    private const uint SHGFI_ICON = 0x00000100;
    private const uint SHGFI_ADDOVERLAYS = 0x00000020;

    private IReadOnlyList<DesktopShortcut>? _snapshot;

    public IReadOnlyList<DesktopShortcut> GetShortcuts() => _snapshot ??= Scan();

    public void Refresh() => _snapshot = Scan();

    public DesktopShortcut? GetRandomShortcut(Random? random = null)
    {
        IReadOnlyList<DesktopShortcut> shortcuts = GetShortcuts();
        return shortcuts.Count == 0 ? null : shortcuts[(random ?? Random.Shared).Next(shortcuts.Count)];
    }

    private static IReadOnlyList<DesktopShortcut> Scan()
    {
        var result = new List<DesktopShortcut>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Environment.SpecialFolder folder in new[]
        {
            Environment.SpecialFolder.DesktopDirectory,
            Environment.SpecialFolder.CommonDesktopDirectory
        })
        {
            string directory = Environment.GetFolderPath(folder);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                continue;

            string[] paths;
            try
            {
                paths = Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string path in paths)
            {
                string extension = Path.GetExtension(path);
                if (!extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!seenPaths.Add(path))
                    continue;

                try
                {
                    FileAttributes attributes = File.GetAttributes(path);
                    if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                        continue;

                    ImageSource? icon = ReadShellIcon(path);
                    if (icon is not null)
                        result.Add(new DesktopShortcut(Path.GetFileNameWithoutExtension(path), path, icon));
                }
                catch (IOException) { /* The shortcut vanished while scanning. */ }
                catch (UnauthorizedAccessException) { /* An unreadable shortcut is optional. */ }
                catch (ArgumentException) { /* Ignore an invalid filename. */ }
                catch (ExternalException) { /* Ignore an icon handler failure. */ }
            }
        }

        return result;
    }

    private static ImageSource? ReadShellIcon(string path)
    {
        IntPtr result = SHGetFileInfoW(path, 0, out SHFILEINFO info,
            (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_ADDOVERLAYS);
        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            return null;

        try
        {
            BitmapSource source = Imaging.CreateBitmapSourceFromHIcon(
                info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
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

    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr SHGetFileInfoW(
        string path, uint fileAttributes, out SHFILEINFO fileInfo, uint fileInfoSize, uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
