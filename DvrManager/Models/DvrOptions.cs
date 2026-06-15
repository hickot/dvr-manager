namespace DvrManager.Models;

public sealed class DvrOptions
{
    public string StoragePath { get; set; } = "recordings";
    public string FfmpegPath { get; set; } = "ffmpeg";
    public double MaxStorageGb { get; set; } = 100;
    public int RetentionCheckMinutes { get; set; } = 10;
}

public sealed record RecordingStatus(
    string State,
    DateTimeOffset? StartedAt = null,
    string? CurrentFolder = null,
    string? Error = null);

public sealed record SystemStatus(
    string StoragePath,
    double UsedStorageGb,
    double MaxStorageGb,
    int ActiveRecordings,
    bool FfmpegAvailable);
