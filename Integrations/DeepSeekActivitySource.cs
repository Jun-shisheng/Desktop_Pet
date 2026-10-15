using System;
using System.IO;
using System.Text.Json;

namespace DeepSeekFishPet.Integrations;

/// <summary>A recent activity event published by the DeepSeek Harness plugin.</summary>
public sealed record DeepSeekActivity(
    string Pose, string Event, string Tool, DateTimeOffset Timestamp);

/// <summary>The plugin's display text and low-balance flag.</summary>
public sealed record DeepSeekQuota(string Text, bool Low);

/// <summary>
/// Reads the plugin's local JSON snapshots. This never starts or configures
/// Harness and never writes to either application's state directory.
/// </summary>
public static class DeepSeekActivitySource
{
    private const int MaxFileBytes = 4096;
    private const int MaxPoseLength = 32;
    private const int MaxEventLength = 64;
    private const int MaxToolLength = 128;
    private const int MaxQuotaTextLength = 160;
    private static readonly TimeSpan Freshness = TimeSpan.FromMinutes(10);

    public static DeepSeekActivity? ReadActivity()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return ReadActivityFrom(StatePath("DeepSeekFishPet", "deepseek-state.json"), now)
            ?? ReadActivityFrom(StatePath("CrossPet", "deepseek-state.json"), now);
    }

    public static DeepSeekQuota? ReadQuota()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return ReadQuotaFrom(StatePath("DeepSeekFishPet", "deepseek-quota.json"), now)
            ?? ReadQuotaFrom(StatePath("CrossPet", "deepseek-quota.json"), now);
    }

    private static string StatePath(string app, string fileName)
    {
        string? configuredPath = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        string localAppData = string.IsNullOrWhiteSpace(configuredPath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : configuredPath;
        return Path.Combine(localAppData, app, "state", fileName);
    }

    private static DeepSeekActivity? ReadActivityFrom(string path, DateTimeOffset now)
    {
        try
        {
            byte[]? bytes = ReadBoundedFile(path, now, requireRecentWrite: false);
            if (bytes is null) return null;
            using JsonDocument document = JsonDocument.Parse(bytes,
                new JsonDocumentOptions { MaxDepth = 8 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("pose", out JsonElement poseValue) ||
                !root.TryGetProperty("event", out JsonElement eventValue) ||
                !root.TryGetProperty("ts", out JsonElement timestampValue) ||
                timestampValue.ValueKind != JsonValueKind.Number ||
                !timestampValue.TryGetDouble(out double seconds))
                return null;

            string? pose = ReadString(poseValue, MaxPoseLength, allowEmpty: false);
            string? activityEvent = ReadString(eventValue, MaxEventLength, allowEmpty: false);
            string tool = "";
            if (root.TryGetProperty("tool", out JsonElement toolValue))
            {
                string? parsedTool = ReadString(toolValue, MaxToolLength, allowEmpty: true);
                if (parsedTool is null) return null;
                tool = parsedTool;
            }

            double ageSeconds = now.ToUnixTimeMilliseconds() / 1000d - seconds;
            if (pose is null || activityEvent is null || !double.IsFinite(ageSeconds) ||
                ageSeconds < 0 || ageSeconds > Freshness.TotalSeconds)
                return null;

            return new DeepSeekActivity(pose, activityEvent, tool,
                DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000)));
        }
        catch (Exception error) when (IsExpectedReadFailure(error))
        {
            return null;
        }
    }

    private static DeepSeekQuota? ReadQuotaFrom(string path, DateTimeOffset now)
    {
        try
        {
            // Quota JSON has no timestamp. Its last write time is the only
            // freshness signal available from the plugin.
            byte[]? bytes = ReadBoundedFile(path, now, requireRecentWrite: true);
            if (bytes is null) return null;
            using JsonDocument document = JsonDocument.Parse(bytes,
                new JsonDocumentOptions { MaxDepth = 8 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("text", out JsonElement textValue) ||
                !root.TryGetProperty("low", out JsonElement lowValue) ||
                lowValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return null;

            string? displayText = ReadString(textValue, MaxQuotaTextLength,
                allowEmpty: true);
            return displayText is null ? null : new DeepSeekQuota(displayText,
                lowValue.GetBoolean());
        }
        catch (Exception error) when (IsExpectedReadFailure(error))
        {
            return null;
        }
    }

    private static byte[]? ReadBoundedFile(string path, DateTimeOffset now,
        bool requireRecentWrite)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is <= 0 or > MaxFileBytes)
            return null;
        if (requireRecentWrite)
        {
            TimeSpan age = now - new DateTimeOffset(file.LastWriteTimeUtc);
            if (age < TimeSpan.Zero || age > Freshness)
                return null;
        }

        // The producer renames complete temporary files into place. Keep the
        // read bounded anyway in case another writer replaces one mid-read.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        byte[] buffer = new byte[MaxFileBytes + 1];
        int length = 0;
        while (length < buffer.Length)
        {
            int count = stream.Read(buffer, length, buffer.Length - length);
            if (count == 0) break;
            length += count;
        }
        if (length is <= 0 or > MaxFileBytes) return null;
        return buffer[..length];
    }

    private static string? ReadString(JsonElement value, int maxLength,
        bool allowEmpty)
    {
        if (value.ValueKind != JsonValueKind.String) return null;
        string? text = value.GetString();
        if (text is null || text.Length > maxLength ||
            (!allowEmpty && string.IsNullOrWhiteSpace(text)))
            return null;
        return text;
    }

    private static bool IsExpectedReadFailure(Exception error) =>
        error is IOException or UnauthorizedAccessException or JsonException or
            ArgumentException or NotSupportedException;
}
