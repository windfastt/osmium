using Osmium.Desktop.Jobs;

namespace Osmium.Desktop.Tray;

/// <summary>Text for the notification-area icon and balloons (limits enforced by Windows).</summary>
public static class TrayText
{
    public const int TooltipMax = 127, BalloonTitleMax = 63, BalloonBodyMax = 255;

    public static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    public static string Bytes(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1 << 20 => $"{b / 1024.0:F1} KB",
        < 1 << 30 => $"{b / 1048576.0:F1} MB",
        _ => $"{b / 1073741824.0:F2} GB",
    };

    public static string Duration(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{t.TotalSeconds:F0}s" : t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes}m {t.Seconds:D2}s" : $"{(int)t.TotalHours}h {t.Minutes:D2}m";

    public static string Tooltip(IReadOnlyList<JobSnapshot> jobs)
    {
        var active = jobs.Where(j => j.State is JobState.Running or JobState.Paused).ToList();
        int queued = jobs.Count(j => j.State == JobState.Queued);
        if (active.Count == 0 && queued == 0) return "Osmium — idle";
        var j0 = active.FirstOrDefault();
        string s = j0 is null ? $"Osmium — {queued} queued"
            : $"Osmium — {(j0.State == JobState.Paused ? "paused" : j0.Phase.ToLowerInvariant())} {j0.Fraction:P0}: {j0.Name}";
        if (queued > 0 && j0 is not null) s += $" (+{queued} queued)";
        return Truncate(s, TooltipMax);
    }

    /// <summary>
    /// The tooltip with a problem that needs attention (e.g. an invalid cluster key) as a second line. The job
    /// line is shortened first, so the warning stays readable within <see cref="TooltipMax"/>.
    /// </summary>
    public static string Tooltip(IReadOnlyList<JobSnapshot> jobs, string? warning)
    {
        string tip = Tooltip(jobs);
        if (string.IsNullOrWhiteSpace(warning)) return tip;
        string w = Truncate(warning.Trim(), 70);
        return Truncate(tip, TooltipMax - 1 - w.Length) + "\n" + w;
    }

    public static (string Title, string Body, bool Error) Completion(JobSnapshot j) => j.State switch
    {
        JobState.Completed when j.Kind == JobKind.Compress => (
            Truncate("Compression finished", BalloonTitleMax),
            Truncate($"{j.Name} — {Bytes(j.BytesIn)} → {Bytes(j.BytesOut)} ({1 - j.Ratio:P0} smaller) in {Duration(j.Elapsed)}", BalloonBodyMax), false),
        JobState.Completed when j.Kind == JobKind.Test => ("Archive OK", Truncate($"{j.Name} — every file verified.", BalloonBodyMax), false),
        JobState.Completed => ("Extraction finished", Truncate($"{j.Name} — {Bytes(j.BytesIn)} extracted in {Duration(j.Elapsed)}", BalloonBodyMax), false),
        JobState.Canceled => ("Job canceled", Truncate(j.Name, BalloonBodyMax), false),
        _ => (Truncate($"{j.Kind} failed", BalloonTitleMax), Truncate($"{j.Name}: {j.Error}", BalloonBodyMax), true),
    };

    public static (string Title, string Body) Aggregate(IReadOnlyList<JobSnapshot> finished)
    {
        long saved = finished.Where(j => j.Kind == JobKind.Compress && j.State == JobState.Completed).Sum(j => Math.Max(0, j.BytesIn - j.BytesOut));
        int failed = finished.Count(j => j.State == JobState.Failed);
        string body = $"{finished.Count} jobs finished" + (saved > 0 ? $" — {Bytes(saved)} saved" : "") + (failed > 0 ? $", {failed} failed" : "");
        return ("Osmium", Truncate(body, BalloonBodyMax));
    }

    /// <summary>Ring progress bucket (0..25) used to pick a pre-rendered tray frame.</summary>
    public static int Bucket(double fraction) => (int)Math.Clamp(Math.Round(fraction * 25), 0, 25);
}
