namespace Launcher.Core.Models;

/// <summary>
/// What a content source is currently offering for a game.
/// BuildId is opaque and compared against GameInfo.InstalledBuildId.
/// PatchBytes equals TotalBytes when no delta path exists.
/// </summary>
public record RemoteVersionInfo(
    string BuildId,
    string Version,
    long TotalBytes,
    long PatchBytes,
    bool DeltaAvailable,
    string SourceRef = ""
)
{
    /// <summary>Bytes saved by patching instead of a full re-download.</summary>
    public long BytesSaved => DeltaAvailable && TotalBytes > PatchBytes ? TotalBytes - PatchBytes : 0;

    /// <summary>Fraction of a full download this update costs, e.g. 0.04 for a 4% patch.</summary>
    public double PatchRatio => TotalBytes > 0 ? (double)PatchBytes / TotalBytes : 1.0;
}

/// <summary>Outcome of an install or update.</summary>
public record InstallResult(
    bool Success,
    string BuildId,
    string Version,
    long BytesTransferred,
    bool WasDelta,
    string? Error = null
);

/// <summary>Outcome of a verify pass.</summary>
public record VerifyResult(
    bool Healthy,
    int FilesChecked,
    int FilesRepaired,
    long BytesRepaired,
    string? Error = null
);
