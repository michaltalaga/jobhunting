namespace JobHunting.Server;

public enum ProcessingStatus { Queued, Extracting, Tailoring, Rendering, Processed, Failed }

public enum ApplicationStatus { NotApplied, Applied, Interview, Rejected, Offer }

public enum JobSource { Extension, Paste }

public enum ChangeRequestStatus { Pending, Done, Failed }

/// <summary>Persisted as job.json in the job's folder.</summary>
public sealed class JobState
{
    public required string Id { get; init; }
    public JobSource Source { get; init; }
    public string? Url { get; init; }
    public string? PageTitle { get; init; }
    public DateTimeOffset CapturedAt { get; init; }

    // Filled in by extraction.
    public string? Company { get; set; }
    public string? Role { get; set; }
    public string? Location { get; set; }
    public Recruiter? Recruiter { get; set; }
    /// <summary>yyyy-MM-dd, when the advert says it was posted (relative dates resolved against the capture date).</summary>
    public string? PostedDate { get; set; }
    /// <summary>yyyy-MM-dd, the application deadline if the advert states one.</summary>
    public string? ClosingDate { get; set; }

    public ProcessingStatus Status { get; set; }
    public string? Error { get; set; }

    public ApplicationStatus ApplicationStatus { get; set; }

    public List<ChangeRequest> ChangeRequests { get; set; } = [];
    public List<ClaudeRun> Runs { get; set; } = [];

    /// <summary>Problems found by deterministic checks on the latest tailored resume.</summary>
    public List<string> Warnings { get; set; } = [];

    /// <summary>Append-only record of everything that happened to the job. See <see cref="TimelineEvents"/>.</summary>
    public List<TimelineEvent> Timeline { get; set; } = [];

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>The person the advert names as poster, hiring team member or contact.</summary>
public sealed record Recruiter(string? Name, string? Title, string? ProfileUrl, string? Email, string? Phone);

public sealed record TimelineEvent(DateTimeOffset At, string Event, string? Detail = null);

/// <summary>
/// Event names: "captured" / "pasted"; "processing.&lt;status&gt;" and "application.&lt;status&gt;" for every
/// status change (recorded automatically by <see cref="JobStore.Update"/>); "change.requested",
/// "retried", "regenerated", "reextracted".
/// </summary>
public static class TimelineEvents
{
    public static string Processing(ProcessingStatus s) => $"processing.{Json.Name(s)}";
    public static string Application(ApplicationStatus s) => $"application.{Json.Name(s)}";
}

public sealed class ChangeRequest
{
    public DateTimeOffset At { get; set; }
    public required string Text { get; set; }
    public ChangeRequestStatus Status { get; set; }
    public string? Error { get; set; }
}

public sealed record ClaudeRun(string Step, DateTimeOffset At, long DurationMs, decimal? CostUsd, string Model, string Effort);

// ---- API contracts ----

public sealed record CaptureFrame(string? Url, string? Title, string? Html, string? Text, bool IsTop);

public sealed record CaptureRequest(string? Url, string? Title, List<CaptureFrame>? Frames);

public sealed record PasteRequest(string? Text, string? Url);

public sealed record ChangeRequestBody(string? Text);

public sealed record ApplicationStatusBody(ApplicationStatus Status);

public sealed record CreatedResponse(string Id, IReadOnlyList<string> DuplicateOf);

/// <summary>What the dashboard shows on first run: anything that stops jobs from being processed.</summary>
public sealed record SetupStatus(IReadOnlyList<string> Problems, string MasterResumePath, IReadOnlyList<string> BackgroundFiles, string? ClaudeVersion);

public sealed record JobSummary(
    string Id,
    string Folder,
    JobSource Source,
    string? Url,
    string? PageTitle,
    string? Company,
    string? Role,
    string? Location,
    string? RecruiterName,
    DateTimeOffset CapturedAt,
    string? PostedDate,
    string? ClosingDate,
    ProcessingStatus Status,
    string? Error,
    ApplicationStatus ApplicationStatus,
    /// <summary>When the application status last changed, i.e. since when it has been what it is.</summary>
    DateTimeOffset? ApplicationStatusAt,
    /// <summary>When the job was first marked anything other than "not applied".</summary>
    DateTimeOffset? AppliedAt,
    int PendingChanges,
    bool HasResume,
    bool HasPdf,
    IReadOnlyList<string> DuplicateOf,
    DateTimeOffset UpdatedAt);

public sealed record JobDetail(
    JobSummary Job,
    Recruiter? Recruiter,
    string? Spec,
    string? Notes,
    string? ResumeJson,
    IReadOnlyList<ChangeRequest> ChangeRequests,
    IReadOnlyList<TimelineEvent> Timeline,
    IReadOnlyList<ClaudeRun> Runs,
    IReadOnlyList<string> Warnings);
