namespace JobHunting.Server;

/// <summary>New: captured, nothing has run; every step after that is started by you (Score, Tailor).</summary>
public enum ProcessingStatus { New, Queued, Extracting, Scoring, ReadyToTailor, Tailoring, Rendering, Reviewing, Processed, Failed, OnHold }

/// <summary>Dropped: you're not pursuing it. The job is kept, but hidden from the default list and never processed.</summary>
public enum ApplicationStatus { NotApplied, Applied, Interview, Rejected, Offer, Dropped }

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

    /// <summary>Your log of the application process: confirmations, contacts, interviews, follow-ups, notes.</summary>
    public List<ApplicationEvent> ApplicationEvents { get; set; } = [];

    /// <summary>PDF theme picked for this job; null means the configured default.</summary>
    public string? Theme { get; set; }

    /// <summary>Page count of the current resume.pdf.</summary>
    public int? PdfPages { get; set; }

    /// <summary>The theme the current resume.pdf was rendered with; a different theme now means it's out of date.</summary>
    public string? PdfTheme { get; set; }

    /// <summary>PDF rendering, which runs only when you ask for it, in its own queue.</summary>
    public RenderState Render { get; set; }

    public string? RenderError { get; set; }

    /// <summary>Set after a first tailoring: the pre-send review (and its one revision) is still to run.</summary>
    public bool ReviewPending { get; set; }

    /// <summary>How well the candidate's profile fits the advert; computed right after extraction.</summary>
    public JobMatch? Match { get; set; }

    /// <summary>Tailoring may run. Set by the Tailor button (or the tailorAutomatically setting).</summary>
    public bool TailorApproved { get; set; }

    /// <summary>This job's overrides of the global tailoring settings (data/tailoring.json).</summary>
    public System.Text.Json.Nodes.JsonObject? Tailoring { get; set; }

    public List<ChangeRequest> ChangeRequests { get; set; } = [];
    public List<ClaudeRun> Runs { get; set; } = [];

    /// <summary>Problems found by deterministic checks on the latest tailored resume.</summary>
    public List<string> Warnings { get; set; } = [];

    /// <summary>Append-only record of everything that happened to the job. See <see cref="TimelineEvents"/>.</summary>
    public List<TimelineEvent> Timeline { get; set; } = [];

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Something that happened in the application process, logged by you.</summary>
public sealed record ApplicationEvent(string Id, DateTimeOffset At, string Type, string? Note);

public sealed record ApplicationEventBody(string? Type, string? Note, DateTimeOffset? At);

public static class ApplicationEventTypes
{
    /// <summary>Type → (did the employer respond?, status it moves the application to).</summary>
    public static readonly IReadOnlyDictionary<string, (bool Response, ApplicationStatus? Status)> All =
        new Dictionary<string, (bool, ApplicationStatus?)>
        {
            ["confirmed"] = (true, null),
            ["contacted"] = (true, null),
            ["interviewScheduled"] = (true, ApplicationStatus.Interview),
            ["interviewDone"] = (true, ApplicationStatus.Interview),
            ["followedUp"] = (false, null),
            ["rejected"] = (true, ApplicationStatus.Rejected),
            ["offer"] = (true, ApplicationStatus.Offer),
            ["note"] = (false, null),
        };
}

public sealed record MatchRequirement(string Requirement, string Status, string? Evidence);

/// <summary>The fit of the candidate's profile to one advert. Status values: met, partial, missing.</summary>
public sealed record JobMatch(
    int Score,
    string? Summary,
    IReadOnlyList<MatchRequirement> Requirements,
    string? Seniority,
    string? Domain,
    string? Location,
    string? BiggestGap,
    DateTimeOffset At);

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
    /// <summary>Written by the pre-send review rather than the candidate; not shown in the UI.</summary>
    public bool FromReview { get; set; }
    public string? Error { get; set; }
}

public sealed record ClaudeRun(string Step, DateTimeOffset At, long DurationMs, decimal? CostUsd, string Model, string Effort);

// ---- API contracts ----

public sealed record CaptureFrame(string? Url, string? Title, string? Html, string? Text, bool IsTop);

public sealed record CaptureRequest(string? Url, string? Title, List<CaptureFrame>? Frames);

public sealed record PasteRequest(string? Text, string? Url);

public sealed record ChangeRequestBody(string? Text);

public sealed record ApplicationStatusBody(ApplicationStatus Status);

public sealed record ThemeBody(string? Theme);

/// <summary>None: idle (a PDF may or may not exist). Queued/Rendering: in the PDF queue. Failed: see RenderError.</summary>
public enum RenderState { None, Queued, Rendering, Failed }

/// <summary>A job's tailoring overrides; a null value removes that override.</summary>
public sealed record TailoringOverridesBody(System.Text.Json.Nodes.JsonObject? Overrides, bool Regenerate);

public sealed record TailoringSettingsResponse(IReadOnlyList<SettingDef> Definitions, System.Text.Json.Nodes.JsonObject Values);

/// <summary>
/// The capture's job. Created = false means the advert's URL was already in the list: nothing was added,
/// and Id is the existing job (DuplicateOf repeats it, which the extension shows as its "dup" badge).
/// </summary>
public sealed record CreatedResponse(string Id, bool Created, IReadOnlyList<string> DuplicateOf);

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
    /// <summary>The latest logged event in which the employer responded (confirmation, contact, interview, rejection, offer).</summary>
    DateTimeOffset? LastResponseAt,
    int PendingChanges,
    string? Theme,
    int? MatchScore,
    bool HasResume,
    bool HasPdf,
    int? PdfPages,
    string? PdfTheme,
    RenderState Render,
    string? RenderError,
    IReadOnlyList<string> DuplicateOf,
    DateTimeOffset UpdatedAt);

public sealed record JobDetail(
    JobSummary Job,
    Recruiter? Recruiter,
    JobMatch? Match,
    IReadOnlyList<ApplicationEvent> ApplicationEvents,
    System.Text.Json.Nodes.JsonObject TailoringOverrides,
    string? Spec,
    string? Notes,
    string? ResumeJson,
    IReadOnlyList<ChangeRequest> ChangeRequests,
    IReadOnlyList<TimelineEvent> Timeline,
    IReadOnlyList<ClaudeRun> Runs,
    IReadOnlyList<string> Warnings);
