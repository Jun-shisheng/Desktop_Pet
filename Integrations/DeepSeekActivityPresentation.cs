using System;

namespace DeepSeekFishPet.Integrations;

/// <summary>Short status text and an optional key from the existing whale sprites.</summary>
public sealed record DeepSeekActivityDisplay(string Text, string? SpriteKey);

/// <summary>Maps a plugin event to a temporary, purely presentational status.</summary>
public static class DeepSeekActivityPresentation
{
    private static readonly TimeSpan CompletedLifetime = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan WorkingLifetime = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan AskingLifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Returns null for stale, future, idle, or unrecognized events. The caller
    /// supplies UTC time so mapping has no clock, window, or pet-brain effects.
    /// </summary>
    public static DeepSeekActivityDisplay? Map(DeepSeekActivity? activity,
        DateTimeOffset nowUtc)
    {
        if (activity is null || string.IsNullOrWhiteSpace(activity.Pose) ||
            !IsKnownEvent(activity.Event)) return null;
        TimeSpan age = nowUtc.ToUniversalTime() - activity.Timestamp.ToUniversalTime();
        if (age < TimeSpan.Zero) return null;

        string pose = activity.Pose.Trim().ToLowerInvariant();
        if (pose is "" or "idle") return null;

        if (activity.Event == "Stop")
        {
            if (age > CompletedLifetime) return null;
            return pose switch
            {
                "happy" => new("大肥鱼干完活啦！", "cheer"),
                "proud" => new("大肥鱼忙完大工程啦！", "cheer"),
                "surprised" => new("大肥鱼被打断啦。", "idle"),
                "oops" => new("大肥鱼遇到点麻烦。", "sit"),
                _ => null
            };
        }

        if (pose == "asking")
            return age <= AskingLifetime
                ? new("大肥鱼等你回答呢…", "idle") : null;
        if (age > WorkingLifetime) return null;

        return pose switch
        {
            "listening" => new("大肥鱼认真听你说…", "idle"),
            "thinking" => new("大肥鱼正在想办法…", "read"),
            "reading" => new("大肥鱼翻翻资料…", "read"),
            "writing" => new("大肥鱼正忙着写…", "carry"),
            "searching" => new("大肥鱼正在找线索…", "read"),
            "drawing" => new("大肥鱼正在画图…", "carry"),
            "delegating" => new("大肥鱼叫来帮手…", "cheer"),
            "running" => new("大肥鱼正在忙活…", "carry"),
            "compact" => new("大肥鱼整理思路中…", "read"),
            "oops" => new("大肥鱼遇到点麻烦。", "sit"),
            _ => null
        };
    }

    private static bool IsKnownEvent(string activityEvent) => activityEvent is
        "SessionStart" or "UserPromptSubmit" or "PreInvocation" or
        "PreToolUse" or "PostToolUse" or "PermissionRequest" or
        "SubagentStart" or "SubagentStop" or "PreCompact" or
        "PostCompact" or "Stop";
}
