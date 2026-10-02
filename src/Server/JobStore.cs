using System.Security.Cryptography;
using System.Text.Json;

namespace JobHunting.Server;

/// <summary>
/// Jobs live as folders under applications/ (or applications/_inbox/ until the company and role are known).
/// The folder is the source of truth; this class keeps an in-memory index of it.
/// </summary>
public sealed class JobStore(Paths paths, JobEvents events, ILogger<JobStore> logger)
{
    private const string StateFile = "job.json";
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _jobs = [];

    private sealed class Entry(JobState state, string folder)
    {
        public JobState State { get; } = state;
        public string Folder { get; set; } = folder;
    }

    public void Load()
    {
        Directory.CreateDirectory(paths.Inbox);
        lock (_gate)
        {
            foreach (var root in new[] { paths.Applications, paths.Inbox })
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var file = Path.Combine(dir, StateFile);
                if (!File.Exists(file)) continue;
                try
                {
                    var state = JsonSerializer.Deserialize<JobState>(File.ReadAllText(file), Json.Files)!;
                    var entry = new Entry(state, dir);
                    if (BackfillTimeline(state)) Save(entry);
                    _jobs[state.Id] = entry;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Skipping unreadable {File}", file);
                }
            }
        }
        logger.LogInformation("Loaded {Count} jobs from {Dir}", _jobs.Count, paths.Applications);

        // Jobs extracted earlier but left in _inbox (e.g. the folder was locked at the time) get their real name now.
        List<string> stranded;
        lock (_gate)
            stranded = _jobs.Values
                .Where(e => IsInInbox(e) && File.Exists(Path.Combine(e.Folder, "spec.md")))
                .Select(e => e.State.Id)
                .ToList();
        foreach (var id in stranded) MoveToFinalFolder(id);
    }

    /// <summary>
    /// Creates a job, unless one with the same advert URL already exists: then nothing is added and
    /// <paramref name="created"/> is false, with that existing job returned.
    /// </summary>
    public JobSummary Create(JobSource source, string? url, string? pageTitle, string text, string? html, out bool created)
    {
        lock (_gate)
        {
            var key = NormalizeUrl(url);
            if (key is not null && _jobs.Values.FirstOrDefault(e => NormalizeUrl(e.State.Url) == key) is { } existing)
            {
                created = false;
                return Summarize(existing);
            }
            created = true;

            string id;
            do id = RandomNumberGenerator.GetString("abcdefghijklmnopqrstuvwxyz0123456789", 6);
            while (_jobs.ContainsKey(id));

            var folder = Path.Combine(paths.Inbox, id);
            Directory.CreateDirectory(folder);
            Files.WriteAtomic(Path.Combine(folder, "page.txt"), text);
            if (html is not null) Files.WriteAtomic(Path.Combine(folder, "page.html"), html);

            var now = DateTimeOffset.Now;
            var entry = new Entry(new JobState
            {
                Id = id,
                Source = source,
                Url = url,
                PageTitle = pageTitle,
                CapturedAt = now,
                Status = ProcessingStatus.New, // nothing runs until you start it
                Timeline = [new TimelineEvent(now, source == JobSource.Paste ? "pasted" : "captured", url)],
                UpdatedAt = now,
            }, folder);
            _jobs[id] = entry;
            Save(entry);

            var summary = Summarize(entry);
            events.PublishJob(summary);
            foreach (var other in summary.DuplicateOf) events.PublishJob(Summarize(_jobs[other]));
            return summary;
        }
    }

    public IReadOnlyList<JobSummary> List()
    {
        lock (_gate) return _jobs.Values.Select(Summarize).OrderByDescending(j => j.CapturedAt).ToList();
    }

    /// <summary>A copy of the job's state, safe to read outside the lock.</summary>
    public (JobState State, string Folder)? Get(string id)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var e)) return null;
            var copy = JsonSerializer.Deserialize<JobState>(JsonSerializer.Serialize(e.State, Json.Compact), Json.Compact)!;
            return (copy, e.Folder);
        }
    }

    public JobDetail? Detail(string id)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var e)) return null;
            var s = e.State;
            return new JobDetail(
                Summarize(e),
                s.Recruiter,
                s.Match,
                [.. s.ApplicationEvents.OrderBy(e => e.At)],
                (System.Text.Json.Nodes.JsonObject?)s.Tailoring?.DeepClone() ?? [],
                Files.ReadIfExists(Path.Combine(e.Folder, "spec.md")),
                Files.ReadIfExists(Path.Combine(e.Folder, "notes.md")),
                Files.ReadIfExists(Path.Combine(e.Folder, "resume.json")),
                [.. s.ChangeRequests],
                [.. s.Timeline],
                [.. s.Runs],
                [.. s.Warnings]);
        }
    }

    /// <summary>
    /// Mutates and persists a job, then notifies listeners. Processing and application status changes
    /// are added to the timeline here, so no caller can forget to. Returns null if the job no longer exists.
    /// </summary>
    public JobSummary? Update(string id, Action<JobState> mutate)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var e)) return null;
            var s = e.State;
            var (status, application) = (s.Status, s.ApplicationStatus);

            mutate(s);

            var now = DateTimeOffset.Now;
            if (s.Status != status)
                s.Timeline.Add(new TimelineEvent(now, TimelineEvents.Processing(s.Status), s.Status == ProcessingStatus.Failed ? s.Error : null));
            if (s.ApplicationStatus != application)
                s.Timeline.Add(new TimelineEvent(now, TimelineEvents.Application(s.ApplicationStatus)));
            s.UpdatedAt = now;
            Save(e);
            var summary = Summarize(e);
            events.PublishJob(summary);
            return summary;
        }
    }

    /// <summary>Moves a job out of _inbox to its date-company-role folder once those are known.</summary>
    public void MoveToFinalFolder(string id)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var e) || !IsInInbox(e)) return;

            var s = e.State;
            var company = Slug.Make(s.Company, 40);
            var role = Slug.Make(s.Role, 60);
            var baseName = $"{s.CapturedAt:yyyy-MM-dd}-{(company.Length > 0 ? company : "unknown")}-{(role.Length > 0 ? role : "job")}";
            var name = baseName;
            for (var i = 2; Directory.Exists(Path.Combine(paths.Applications, name)); i++) name = $"{baseName}-{i}";

            try
            {
                var target = Path.Combine(paths.Applications, name);
                Directory.Move(e.Folder, target);
                e.Folder = target;
            }
            catch (IOException ex)
            {
                // Typically the folder is open in Explorer or an editor. The job keeps working from _inbox.
                logger.LogWarning(ex, "Could not move job {Id} to {Name}", id, name);
            }
            events.PublishJob(Summarize(e));
        }
    }

    public bool Delete(string id)
    {
        lock (_gate)
        {
            if (!_jobs.Remove(id, out var e)) return false;
            Directory.Delete(e.Folder, recursive: true);
            events.PublishDeleted(id);
            var url = NormalizeUrl(e.State.Url);
            foreach (var other in _jobs.Values.Where(o => url is not null && NormalizeUrl(o.State.Url) == url))
                events.PublishJob(Summarize(other));
            return true;
        }
    }

    /// <summary>Reconstructs a timeline for jobs created before timelines existed. Returns true if it added one.</summary>
    private static bool BackfillTimeline(JobState s)
    {
        if (s.Timeline.Count > 0) return false;
        s.Timeline.Add(new TimelineEvent(s.CapturedAt, s.Source == JobSource.Paste ? "pasted" : "captured", s.Url));
        foreach (var run in s.Runs)
        {
            var status = run.Step == "extract" ? ProcessingStatus.Extracting : ProcessingStatus.Tailoring;
            s.Timeline.Add(new TimelineEvent(run.At - TimeSpan.FromMilliseconds(run.DurationMs), TimelineEvents.Processing(status)));
        }
        if (s.Status is ProcessingStatus.Processed or ProcessingStatus.Failed)
            s.Timeline.Add(new TimelineEvent(s.Runs.LastOrDefault()?.At ?? s.UpdatedAt, TimelineEvents.Processing(s.Status), s.Error));
        if (s.ApplicationStatus != ApplicationStatus.NotApplied)
            s.Timeline.Add(new TimelineEvent(s.UpdatedAt, TimelineEvents.Application(s.ApplicationStatus)));
        return true;
    }

    private bool IsInInbox(Entry e) =>
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(e.Folder)), paths.Inbox, StringComparison.OrdinalIgnoreCase);

    private static void Save(Entry e) =>
        Files.WriteAtomic(Path.Combine(e.Folder, StateFile), JsonSerializer.Serialize(e.State, Json.Files));

    private JobSummary Summarize(Entry e)
    {
        var s = e.State;
        var url = NormalizeUrl(s.Url);
        var duplicates = url is null
            ? []
            : _jobs.Values.Where(o => o != e && NormalizeUrl(o.State.Url) == url).Select(o => o.State.Id).ToList();

        return new JobSummary(
            s.Id,
            Path.GetRelativePath(paths.Applications, e.Folder).Replace('\\', '/'),
            s.Source,
            s.Url,
            s.PageTitle,
            s.Company,
            s.Role,
            s.Location,
            s.Recruiter?.Name,
            s.CapturedAt,
            s.PostedDate,
            s.ClosingDate,
            s.Status,
            s.Error,
            s.ApplicationStatus,
            s.Timeline.LastOrDefault(t => t.Event.StartsWith("application.", StringComparison.Ordinal))?.At,
            s.Timeline.FirstOrDefault(t => t.Event.StartsWith("application.", StringComparison.Ordinal)
                                           && t.Event != TimelineEvents.Application(ApplicationStatus.NotApplied)
                                           && t.Event != TimelineEvents.Application(ApplicationStatus.Dropped))?.At,
            s.ApplicationEvents
                .Where(e => ApplicationEventTypes.All.TryGetValue(e.Type, out var t) && t.Response)
                .Select(e => (DateTimeOffset?)e.At)
                .Max(),
            s.ChangeRequests.Count(c => c.Status == ChangeRequestStatus.Pending && !c.FromReview),
            s.Theme,
            s.Match?.Score,
            File.Exists(Path.Combine(e.Folder, "resume.json")),
            File.Exists(Path.Combine(e.Folder, "resume.pdf")),
            s.PdfPages,
            s.PdfTheme,
            s.Render,
            s.RenderError,
            duplicates,
            s.UpdatedAt);
    }

    private static string? NormalizeUrl(string? url) =>
        string.IsNullOrWhiteSpace(url) ? null : Capture.CleanUrl(url)!.TrimEnd('/').ToLowerInvariant();
}
