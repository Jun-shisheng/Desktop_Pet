using System;
using System.IO;
using System.Text.Json;

namespace DeepSeekFishPet;

public enum PetMode { Life, IconJump, WindowWhack }

public sealed class PetSettings
{
    public bool AlwaysOnTop { get; set; } = true;
    public bool IsPaused { get; set; }
    public bool ShowBubbles { get; set; } = true;
    public bool AllowShortcutPlay { get; set; } = true;
    public bool AmbientWindowPeek { get; set; } = true;
    public bool FollowDeepSeekActivity { get; set; } = true;
    public bool ShowDeepSeekBalance { get; set; }
    public PetMode Mode { get; set; } = PetMode.Life;

    private static string SettingsPath => Path.Combine(
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory,
        "settings.json");

    public static PetSettings Load()
    {
        try
        {
            string path = SettingsPath;
            if (File.Exists(path))
                return JsonSerializer.Deserialize<PetSettings>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A read-only folder or a damaged optional settings file uses defaults.
        }
        return new();
    }

    public void Save()
    {
        string? temporaryPath = null;
        try
        {
            string path = SettingsPath;
            temporaryPath = path + ".tmp";
            File.WriteAllText(temporaryPath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Running from read-only media is supported; settings are session-only.
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}
