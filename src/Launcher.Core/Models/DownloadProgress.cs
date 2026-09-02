using System;

namespace Launcher.Core.Models;

/// <summary>
/// A progress tick from a content source.
///
/// <paramref name="Fraction"/> is 0..1, not 0..100. The previous build mixed the two conventions
/// and also invented byte counts from a hardcoded 100 MB total, so the numbers on screen were
/// fiction. Every field here comes from the transfer itself; a source that genuinely does not know
/// a value reports 0 (or null for <paramref name="Eta"/>) and the formatter says so.
/// </summary>
public record DownloadProgress(
    double Fraction,
    long BytesTransferred,
    long TotalBytes,
    double SpeedBytesPerSecond,
    TimeSpan? Eta,
    string Stage
)
{
    public static DownloadProgress Empty => new(0, 0, 0, 0, null, "Idle");

    /// <summary>0..100, for progress bars and text.</summary>
    public double Percent => Math.Clamp(Fraction, 0, 1) * 100;

    public string FormattedSpeed =>
        SpeedBytesPerSecond <= 0 ? "—" : $"{FormatBytes((long)SpeedBytesPerSecond)}/s";

    public string FormattedProgress =>
        TotalBytes <= 0
            ? FormatBytes(BytesTransferred)
            : $"{FormatBytes(BytesTransferred)} / {FormatBytes(TotalBytes)}";

    public string FormattedEta
    {
        get
        {
            if (Eta is not { } eta || eta.TotalSeconds <= 0) return "—";
            if (eta.TotalHours >= 1) return $"{(int)eta.TotalHours}h {eta.Minutes}m left";
            if (eta.TotalMinutes >= 1) return $"{eta.Minutes}m {eta.Seconds}s left";
            return $"{eta.Seconds}s left";
        }
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 MB";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit <= 1 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }
}
