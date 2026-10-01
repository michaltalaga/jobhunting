using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace JobHunting.Server;

/// <summary>
/// Moves one job forward until there is nothing left to do. The next step is always derived from
/// what is on disk, so a job interrupted by a crash or restart resumes where it stopped.
/// </summary>
public sealed class JobPipeline(
    JobStore store,
    ClaudeRunner claude,
    ResumeRenderer renderer,
    Paths paths,
    IOptions<JobHuntingOptions> options,
    ILogger<JobPipeline> logger)
{
    private const int MaxCapturedChars = 300_000;

    private enum Step { None, Extract, Tailor, Render }

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

            var step = NextStep(state, folder);
            if (step == Step.None)
            {
                store.Update(id, s => { s.Status = ProcessingStatus.Processed; s.Error = null; });
                return;
            }

            try
            {
                switch (step)
                {
                    case Step.Extract: await ExtractAsync(id, state, folder, ct); break;
                    case Step.Tailor: await TailorAsync(id, state, folder, ct); break;
                    case Step.Render: await RenderAsync(id, folder, ct); break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // shutting down; the job resumes on next start
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
        if (!File.Exists(Path.Combine(folder, "resume.json"))) return Step.Tailor;
        if (s.ChangeRequests.Any(c => c.Status == ChangeRequestStatus.Pending)) return Step.Tailor;
        if (renderer.IsAvailable && !File.Exists(Path.Combine(folder, "resume.pdf"))) return Step.Render;
        return Step.None;
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
        var highlights = Files.ReadIfExists(paths.Highlights) ?? "";
        var spec = await File.ReadAllTextAsync(Path.Combine(folder, "spec.md"), ct);
        var current = Files.ReadIfExists(resumePath);
        var pending = s.ChangeRequests.Where(c => c.Status == ChangeRequestStatus.Pending).ToList();

        // Stable inputs first, so the prompt cache is reused from one job to the next.
        var input = new StringBuilder()
            .AppendLine("<career_highlights>").AppendLine(highlights).AppendLine("</career_highlights>").AppendLine()
            .AppendLine("<master_resume>").AppendLine(master).AppendLine("</master_resume>").AppendLine()
            .AppendLine($"<job_advert company=\"{s.Company}\" role=\"{s.Role}\" location=\"{s.Location}\" url=\"{s.Url}\">")
            .AppendLine(spec)
            .AppendLine("</job_advert>").AppendLine();

        if (current is not null && pending.Count > 0)
        {
            input.AppendLine("<current_resume>").AppendLine(current).AppendLine("</current_resume>").AppendLine()
                 .AppendLine("<current_notes>").AppendLine(Files.ReadIfExists(notesPath)).AppendLine("</current_notes>").AppendLine();
        }
        if (pending.Count > 0)
        {
            input.AppendLine("<change_requests>");
            foreach (var c in pending) input.AppendLine("<change_request>").AppendLine(c.Text).AppendLine("</change_request>");
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
        var (resume, warnings) = ResumeChecks.Apply(resumeJson, master);

        File.Delete(Path.Combine(folder, "resume.pdf")); // stale now; re-rendered next
        await Files.WriteAtomicAsync(resumePath, resume + "\n", ct);
        await Files.WriteAtomicAsync(notesPath, notes.Trim() + "\n", ct);

        var included = pending.Select(c => c.At).ToHashSet();
        store.Update(id, x =>
        {
            x.Warnings = warnings;
            foreach (var c in x.ChangeRequests.Where(c => c.Status == ChangeRequestStatus.Pending && included.Contains(c.At)))
                c.Status = ChangeRequestStatus.Done;
        });
    }

    private async Task RenderAsync(string id, string folder, CancellationToken ct)
    {
        store.Update(id, x => { x.Status = ProcessingStatus.Rendering; x.Error = null; });
        await renderer.RenderAsync(folder, ct);
    }

    private void RecordRun(string id, string name, ClaudeStepOptions step, ClaudeResult result) =>
        store.Update(id, x => x.Runs.Add(new ClaudeRun(name, DateTimeOffset.Now, result.DurationMs, result.CostUsd, step.Model, step.Effort)));
}

public sealed class JobWorker(JobStore store, JobQueue queue, JobPipeline pipeline, ILogger<JobWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Anything that was queued or mid-flight when the server stopped picks up where it left off.
        foreach (var job in store.List()
                     .Where(j => j.Status is not (ProcessingStatus.Processed or ProcessingStatus.Failed))
                     .OrderBy(j => j.CapturedAt))
            queue.Enqueue(job.Id);

        await foreach (var id in queue.ReadAllAsync(ct))
        {
            try
            {
                await pipeline.RunAsync(id, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected failure while processing job {Id}", id);
            }
        }
    }
}
