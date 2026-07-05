namespace ReScene.Core;

/// <summary>The result of comparing a produced volume set against the expected per-volume CRCs.</summary>
public sealed record VolumeMatchResult(
    bool AllMatch,
    IReadOnlyList<VolumeMatch> Volumes,
    VolumeMatch? FirstMismatch,
    bool CountMismatch);
