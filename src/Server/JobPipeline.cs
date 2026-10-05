using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace JobHunting.Server;

/// <summary>
/// Moves one job forward until there is nothing left to do. The next step is always derived from
/// what is on disk, so a job interrupted by a crash or restart resumes where it stopped.
/// </summary>
public sealed class JobPipeline(
    JobStore store,
    ClaudeRunner claude,
    TailoringSettings tailoring,
    Paths paths,
    IOptions<JobHuntingOptions> options,
    ILogger<JobPipeline> logger)
{
    private const int MaxCapturedChars = 300_000;

    private enum Step { None, Extract, Match, Tailor, Review }

    private sealed record ExtractMeta(
        bool Found,
        string? Reason,
        string? Company,
        string? Role,
        string? Location,
        Recruiter? Recruiter,
        string? PostedDate,
        string? ClosingDate);

    public async Task RunAsync(string id, CancellationToken ct)
    {
        while (true)
        {
            var job = store.Get(id);
            if (job is null) return; // deleted while queued
            var (state, folder) = job.Value;
            if (state.Status == ProcessingStatus.OnHold) return; // held after it was picked up

            var step = NextStep(state, folder);
            if (step == Step.None)
            {
                // Nothing left: either finished, or scored and waiting for the Tailor button.
                var done = File.Exists(Path.Combine(folder, "resume.json")) ? ProcessingStatus.Processed : ProcessingStatus.ReadyToTailor;
                if (state.Status != done || state.Error is not null)
                    store.Update(id, s => { s.Status = done; s.Error = null; });
                return;
            }

            logger.LogInformation("Job {Id} ({Company}): {Step} started", id, state.Company ?? "?", step);
            try
            {
                switch (step)
                {
                    case Step.Extract: await ExtractAsync(id, state, folder, ct); break;
                    case Step.Match: await MatchAsync(id, state, folder, ct); break;
                    case Step.Tailor: await TailorAsync(id, state, folder, ct); break;
                    case Step.Review: await ReviewAsync(id, state, folder, ct); break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // stopped from the dashboard or shutting down; the worker decides which
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Job {Id} failed at {Step}", id, step);
                store.Update(id, s =>
                {
                    s.Status = ProcessingStatus.Failed;
                    s.Error = $"{step}: {ex.Message}";
                    if (step == Step.Tailor)
                        foreach (var c in s.ChangeRequests.Where(c => c.Status == ChangeRequestStatus.Pending))
                        {
                            c.Status = ChangeRequestStatus.Failed;
                            c.Error = ex.Message;
                        }
                });
                return;
            }
        }
    }

    private Step NextStep(JobState s, string folder)
    {
        if (!File.Exists(Path.Combine(folder, "spec.md"))) return Step.Extract;
        if (s.Match is null) return Step.Match;
        // A first tailoring needs your go-ahead (the Tailor button, Regenerate, or tailorAutomatically).
        if (!File.Exists(Path.Combine(folder, "resume.json"))) return s.TailorApproved ? Step.Tailor : Step.None;
        if (s.ChangeRequests.Any(c => c.Status == ChangeRequestStatus.Pending)) return Step.Tailor;
        if (s.ReviewPending) return Step.Review;
        return Step.None; // PDFs are rendered only on request, in their own queue (RenderWorker)
    }

    private async Task ExtractAsync(string id, JobState s, string folder, CancellationToken ct)
    {
        store.Update(id, x => { x.Status = ProcessingStatus.Extracting; x.Error = null; });

        var captured = await File.ReadAllTextAsync(Path.Combine(folder, "page.txt"), ct);
        if (captured.Length > MaxCapturedChars) captured = captured[..MaxCapturedChars];
        // Visible text has names but not profile URLs or mail addresses; those live in the HTML's links.
        var html = Files.ReadIfExists(Path.Combine(folder, "page.html"));
        var links = html is null ? "" : Capture.ContactLinks(html);

        var input = new StringBuilder()
            .AppendLine($"<page_url>{s.Url ?? "(none: the job advert was pasted as text)"}</page_url>")
            .AppendLine($"<page_title>{s.PageTitle}</page_title>")
            .AppendLine($"<captured_at>{s.CapturedAt:yyyy-MM-dd}</captured_at>")
            .AppendLine("<captured_text>")
            .AppendLine(captured)
            .AppendLine("</captured_text>")
            .AppendLine("<contact_links>")
            .AppendLine(links)
            .AppendLine("</contact_links>")
            .ToString();

        var step = options.Value.Claude.Extract;
        var result = await claude.RunAsync(paths.Prompt("extract.md"), input, step, ct);
        RecordRun(id, "extract", step, result);

        var metaJson = TaggedOutput.Get(result.Text, "meta")
            ?? throw new InvalidOperationException("Claude's answer had no <meta> block.");
        var meta = JsonSerializer.Deserialize<ExtractMeta>(metaJson, Json.Files)
            ?? throw new InvalidOperationException("Claude's <meta> block was empty.");
        if (!meta.Found)
            throw new InvalidOperationException($"No job advert found in the capture. {meta.Reason}".Trim());
        var spec = TaggedOutput.Get(result.Text, "spec");
        if (string.IsNullOrWhiteSpace(spec))
            throw new InvalidOperationException("Claude's answer had no <spec> block.");

        store.Update(id, x =>
        {
            x.Company = meta.Company;
            x.Role = meta.Role;
            x.Location = meta.Location;
            x.Recruiter = meta.Recruiter is { Name: not null } or { Email: not null } ? meta.Recruiter : null;
            x.PostedDate = IsoDate(meta.PostedDate);
            x.ClosingDate = IsoDate(meta.ClosingDate);
        });
        await Files.WriteAtomicAsync(Path.Combine(folder, "spec.md"), spec.Trim() + "\n", ct);
        store.MoveToFinalFolder(id);
    }

    private static string? IsoDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", out var date) ? date.ToString("yyyy-MM-dd") : null;

    /// <summary>First tailoring, or a revision of the current resume when change requests are pending.</summary>
    private async Task TailorAsync(string id, JobState s, string folder, CancellationToken ct)
    {
        store.Update(id, x => { x.Status = ProcessingStatus.Tailoring; x.Error = null; });

        var resumePath = Path.Combine(folder, "resume.json");
        var notesPath = Path.Combine(folder, "notes.md");
        var master = Files.ReadIfExists(paths.MasterResume)
            ?? throw new InvalidOperationException($"No master resume at {paths.MasterResume}. See the setup notes on the dashboard.");
        var spec = await File.ReadAllTextAsync(Path.Combine(folder, "spec.md"), ct);
        var current = Files.ReadIfExists(resumePath);
        var pending = s.ChangeRequests.Where(c => c.Status == ChangeRequestStatus.Pending).ToList();
        var settings = tailoring.Effective(s.Tailoring);

        // Stable inputs first, so the prompt cache is reused from one job to the next.
        var input = new StringBuilder();
        await AppendSourcesAsync(input, master, ct);
        input
            .AppendLine("<settings>").Append(TailoringSettings.ToPrompt(settings)).AppendLine("</settings>").AppendLine()
            .AppendLine($"<job_advert company=\"{s.Company}\" role=\"{s.Role}\" location=\"{s.Location}\" url=\"{s.Url}\">")
            .AppendLine(spec)
            .AppendLine("</job_advert>").AppendLine();

        if (current is not null && pending.Count > 0)
        {
            input.AppendLine("<current_resume>").AppendLine(SingleLine(current)).AppendLine("</current_resume>").AppendLine()
                 .AppendLine("<current_notes>").AppendLine(Files.ReadIfExists(notesPath)).AppendLine("</current_notes>").AppendLine();
        }
        if (pending.Count > 0)
        {
            input.AppendLine("<change_requests>");
            foreach (var c in pending)
                input.AppendLine(c.FromReview ? "<change_request source=\"review\">" : "<change_request>").AppendLine(c.Text).AppendLine("</change_request>");
            input.AppendLine("</change_requests>").AppendLine();
        }
        input.AppendLine(current is not null && pending.Count > 0
            ? "Revise the current resume according to the change requests."
            : "Tailor the resume to this job advert.");

        var step = options.Value.Claude.Tailor;
        var result = await claude.RunAsync(paths.Prompt("tailor.md"), input.ToString(), step, ct);
        RecordRun(id, current is null ? "tailor" : "revise", step, result);

        var resumeJson = TaggedOutput.Get(result.Text, "resume_json")
            ?? throw new InvalidOperationException("Claude's answer had no <resume_json> block.");
        var notes = TaggedOutput.Get(result.Text, "notes") ?? "";
        // A number may come from anything the candidate wrote, but not from the reviewer's suggestions.
        var sources = DataFolder.BackgroundFiles(paths).Select(File.ReadAllText)
            .Append(settings["instructions"]?.GetValue<string>() ?? "")
            .Concat(s.ChangeRequests.Where(c => !c.FromReview).Select(c => c.Text));
        var (resume, warnings) = ResumeChecks.Apply(resumeJson, master, sources);

        File.Delete(Path.Combine(folder, "resume.pdf")); // no longer matches the resume; render again on request
        await Files.WriteAtomicAsync(resumePath, resume + "\n", ct);
        await Files.WriteAtomicAsync(notesPath, notes.Trim() + "\n", ct);

        var included = pending.Select(c => c.At).ToHashSet();
        // Only a first tailoring (new job or Regenerate) gets the pre-send review; revisions don't loop back into it.
        var review = current is null && settings["review"]?.GetValue<string>() == "revise";
        store.Update(id, x =>
        {
            x.Warnings = warnings;
            x.ReviewPending = review;
            x.PdfPages = null;
            x.PdfTheme = null;
            foreach (var c in x.ChangeRequests.Where(c => c.Status == ChangeRequestStatus.Pending && included.Contains(c.At)))
                c.Status = ChangeRequestStatus.Done;
        });
    }

    /// <summary>
    /// The pre-send review: an HR-consultant pass over the rendered resume, fact-checked against the sources.
    /// Its fixes become a hidden change request, which the next step applies once.
    /// </summary>
    private async Task ReviewAsync(string id, JobState s, string folder, CancellationToken ct)
    {
        store.Update(id, x => { x.Status = ProcessingStatus.Reviewing; x.Error = null; });

        var master = Files.ReadIfExists(paths.MasterResume)
            ?? throw new InvalidOperationException($"No master resume at {paths.MasterResume}.");
        var input = new StringBuilder();
        await AppendSourcesAsync(input, master, ct);
        input
            .AppendLine("<settings>").Append(TailoringSettings.ToPrompt(tailoring.Effective(s.Tailoring))).AppendLine("</settings>").AppendLine()
            .AppendLine("<job_advert>").AppendLine(await File.ReadAllTextAsync(Path.Combine(folder, "spec.md"), ct)).AppendLine("</job_advert>").AppendLine()
            .AppendLine($"<rendered_pages>{s.PdfPages?.ToString() ?? "unknown"}</rendered_pages>").AppendLine()
            .AppendLine("<resume>").AppendLine(SingleLine(await File.ReadAllTextAsync(Path.Combine(folder, "resume.json"), ct))).AppendLine("</resume>").AppendLine();
        if (s.Warnings.Count > 0)
        {
            input.AppendLine("<server_checks>");
            foreach (var warning in s.Warnings) input.AppendLine($"- {warning}");
            input.AppendLine("</server_checks>").AppendLine();
        }
        input.AppendLine("Review this resume for this job.");

        var step = options.Value.Claude.Review;
        var result = await claude.RunAsync(paths.Prompt("review.md"), input.ToString(), step, ct);
        RecordRun(id, "review", step, result);

        var review = TaggedOutput.Get(result.Text, "review")
            ?? throw new InvalidOperationException("Claude's review had no <review> block.");
        var score = TaggedOutput.Get(result.Text, "score")?.Trim();
        var revise = !string.Equals(TaggedOutput.Get(result.Text, "verdict")?.Trim(), "ship", StringComparison.OrdinalIgnoreCase);
        await Files.WriteAtomicAsync(Path.Combine(folder, "review.md"),
            $"# Pre-send review\n\nScore: {score ?? "?"}/100 · Verdict: {(revise ? "revise" : "ship")}\n\n{review.Trim()}\n", ct);

        store.Update(id, x =>
        {
            var now = DateTimeOffset.Now;
            x.ReviewPending = false;
            x.Timeline.Add(new TimelineEvent(now, "reviewed", score is null ? null : $"score {score}/100"));
            if (revise) x.ChangeRequests.Add(new ChangeRequest { At = now, Text = review.Trim(), FromReview = true });
        });
    }

    /// <summary>How well the candidate's profile fits the advert, before any tailoring.</summary>
    private async Task MatchAsync(string id, JobState s, string folder, CancellationToken ct)
    {
        store.Update(id, x => { x.Status = ProcessingStatus.Scoring; x.Error = null; });

        var master = Files.ReadIfExists(paths.MasterResume)
            ?? throw new InvalidOperationException($"No master resume at {paths.MasterResume}. See the setup notes on the dashboard.");
        var input = new StringBuilder();
        await AppendSourcesAsync(input, master, ct);
        input.AppendLine($"<job_advert company=\"{s.Company}\" role=\"{s.Role}\" location=\"{s.Location}\">")
             .AppendLine(await File.ReadAllTextAsync(Path.Combine(folder, "spec.md"), ct))
             .AppendLine("</job_advert>").AppendLine()
             .AppendLine("Score the candidate's fit for this job.");

        var step = options.Value.Claude.Match;
        var result = await claude.RunAsync(paths.Prompt("match.md"), input.ToString(), step, ct);
        RecordRun(id, "match", step, result);

        if (!int.TryParse(TaggedOutput.Get(result.Text, "score")?.Trim(), out var score))
            throw new InvalidOperationException("Claude's answer had no numeric <score>.");
        var json = TaggedOutput.Get(result.Text, "match_json")
            ?? throw new InvalidOperationException("Claude's answer had no <match_json> block.");
        var detail = JsonSerializer.Deserialize<MatchDetail>(json, Json.Files)
            ?? throw new InvalidOperationException("Claude's <match_json> block was empty.");

        var auto = tailoring.Effective(s.Tailoring)["tailorAutomatically"]?.GetValue<string>() == "on";
        store.Update(id, x =>
        {
            x.Match = new JobMatch(Math.Clamp(score, 0, 100), detail.Summary, detail.Requirements ?? [],
                detail.Seniority, detail.Domain, detail.Location, detail.BiggestGap, DateTimeOffset.Now);
            x.Timeline.Add(new TimelineEvent(DateTimeOffset.Now, "matched", $"{x.Match.Score}/100"));
            if (auto) x.TailorApproved = true;
        });
    }

    private sealed record MatchDetail(
        string? Summary, List<MatchRequirement>? Requirements, string? Seniority, string? Domain, string? Location, string? BiggestGap);

    /// <summary>The candidate's sources: background documents, then the master resume as Markdown (much shorter than its JSON).</summary>
    private async Task AppendSourcesAsync(StringBuilder input, string master, CancellationToken ct)
    {
        input.AppendLine("<background>");
        foreach (var file in DataFolder.BackgroundFiles(paths))
            input.AppendLine($"<document name=\"{Path.GetRelativePath(paths.Background, file).Replace('\\', '/')}\">")
                 .AppendLine(await File.ReadAllTextAsync(file, ct))
                 .AppendLine("</document>");
        input.AppendLine("</background>").AppendLine()
             .AppendLine("<master_resume>").Append(MasterMarkdown.Render(master)).AppendLine("</master_resume>").AppendLine();
    }

    /// <summary>A tailored resume as single-line JSON: the same content in fewer tokens.</summary>
    private static string SingleLine(string json) => JsonNode.Parse(json)?.ToJsonString(Json.Prompt) ?? json;

    private void RecordRun(string id, string name, ClaudeStepOptions step, ClaudeResult result) =>
        store.Update(id, x => x.Runs.Add(new ClaudeRun(name, DateTimeOffset.Now, result.DurationMs, result.CostUsd, step.Model, step.Effort)));
}

public sealed class JobWorker(
    JobStore store, JobQueue queue, JobPipeline pipeline, IOptions<JobHuntingOptions> options, ILogger<JobWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken ct)
    {
        // On start, only jobs that were queued or mid-step resume. Nothing else is started automatically:
        // finished, ready-to-tailor, failed and held jobs wait for you.
        foreach (var job in store.List()
                     .Where(j => j.Status is ProcessingStatus.Queued or ProcessingStatus.Extracting or ProcessingStatus.Scoring
                         or ProcessingStatus.Tailoring or ProcessingStatus.Rendering or ProcessingStatus.Reviewing)
                     .OrderBy(j => j.CapturedAt))
        {
            if (job.Status != ProcessingStatus.Queued)
                store.Update(job.Id, s => s.Status = ProcessingStatus.Queued); // interrupted by the restart
            queue.Enqueue(job.Id);
        }

        // Several workers share the queue; it never hands the same job to two of them.
        var workers = Math.Max(1, options.Value.Workers);
        logger.LogInformation("Starting {Workers} worker(s)", workers);
        return Task.WhenAll(Enumerable.Range(1, workers).Select(n => WorkAsync(n, ct)));
    }

    private async Task WorkAsync(int worker, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string id;
            CancellationToken jobToken;
            try
            {
                (id, jobToken) = await queue.NextAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }

            var started = DateTimeOffset.Now;
            logger.LogInformation("Worker {Worker} picked up job {Id}", worker, id);
            try
            {
                await pipeline.RunAsync(id, jobToken);
                logger.LogInformation("Worker {Worker} finished job {Id} in {Seconds:0}s", worker, id, (DateTimeOffset.Now - started).TotalSeconds);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return; // shutting down; the job resumes on next start
            }
            catch (OperationCanceledException)
            {
                // Stopped from the dashboard: keep everything done so far and wait for Resume.
                logger.LogInformation("Job {Id} stopped from the dashboard after {Seconds:0}s", id, (DateTimeOffset.Now - started).TotalSeconds);
                store.Update(id, s =>
                {
                    s.Status = ProcessingStatus.OnHold;
                    s.Error = null;
                    s.Timeline.Add(new TimelineEvent(DateTimeOffset.Now, "stopped"));
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected failure while processing job {Id}", id);
            }
            finally
            {
                queue.Finished(id);
            }
        }
    }
}
