using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace JobHunting.Server;

public static class Api
{
    /// <summary>Files in a job folder the SPA may fetch.</summary>
    private static readonly Dictionary<string, string> JobFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["resume.pdf"] = "application/pdf",
        ["resume.json"] = "application/json; charset=utf-8",
        ["notes.md"] = "text/markdown; charset=utf-8",
        ["spec.md"] = "text/markdown; charset=utf-8",
        ["page.txt"] = "text/plain; charset=utf-8",
        ["page.html"] = "text/html; charset=utf-8",
    };

    public static void MapApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/setup", async (Paths paths, ClaudeRunner claude, ResumeRenderer renderer) =>
        {
            var problems = new List<string>();
            if (!File.Exists(paths.MasterResume))
                problems.Add($"No master resume at {paths.MasterResume}. Save your resume there in JSON Resume format " +
                             "(https://jsonresume.org/schema), or point JobHunting:MasterResumePath at it in data/settings.json.");
            var version = await claude.VersionAsync();
            if (version is null)
                problems.Add("The Claude CLI ('claude') was not found on PATH. Install Claude Code, run 'claude' once to log in, then restart this server.");
            if (renderer.Chrome is null)
                problems.Add("Chrome (or Edge) was not found, so no PDFs will be rendered. Install Chrome, or set JobHunting:Render:ChromePath in data/settings.json.");
            return new SetupStatus(problems, paths.MasterResume, [.. DataFolder.BackgroundFiles(paths).Select(f => Path.GetRelativePath(paths.Background, f))], version);
        });

        api.MapGet("/jobs", (JobStore store) => store.List());

        api.MapGet("/jobs/{id}", (string id, JobStore store) =>
            store.Detail(id) is { } detail ? Results.Ok(detail) : Results.NotFound());

        api.MapGet("/jobs/{id}/files/{name}", (string id, string name, bool? download, JobStore store, HttpContext http) =>
        {
            if (!JobFiles.TryGetValue(name, out var contentType) || store.Get(id) is not { } job) return Results.NotFound();
            var path = Path.Combine(job.Folder, name);
            if (!File.Exists(path)) return Results.NotFound();
            // The captured page is untrusted markup: never let it run script on this origin.
            if (name.Equals("page.html", StringComparison.OrdinalIgnoreCase))
                http.Response.Headers.ContentSecurityPolicy = "sandbox";
            return Results.File(path, contentType, download == true ? DownloadName(job.Folder, name) : null);
        });

        api.MapGet("/themes", (ThemeCatalog themes) => themes.List());

        api.MapGet("/settings/tailoring", (TailoringSettings tailoring) =>
            new TailoringSettingsResponse(TailoringSettings.Definitions, tailoring.Global()));

        api.MapPut("/settings/tailoring", (System.Text.Json.Nodes.JsonObject values, TailoringSettings tailoring) =>
        {
            tailoring.SaveGlobal(values);
            return new TailoringSettingsResponse(TailoringSettings.Definitions, tailoring.Global());
        });

        // Overrides apply from the next tailoring run; Regenerate starts that run now.
        api.MapPut("/jobs/{id}/tailoring", (string id, TailoringOverridesBody body, JobStore store, JobQueue queue) =>
        {
            if (store.Get(id) is not { } current) return Results.NotFound();
            if (body.Regenerate && IsBusy(current.State)) return Results.Conflict("The job is being processed.");
            var overrides = TailoringSettings.Clean(body.Overrides ?? [], keepNulls: false);
            var job = store.Update(id, s =>
            {
                s.Tailoring = overrides.Count > 0 ? overrides : null;
                s.Timeline.Add(new TimelineEvent(DateTimeOffset.Now, "tailoring.changed", overrides.ToJsonString()));
            });
            return body.Regenerate ? Redo(id, store, queue, "regenerated", ["resume.json", "notes.md", "resume.pdf"]) : Results.Ok(job);
        });

        // Only records the choice: an existing PDF stays (shown as out of date) until you render again.
        api.MapPost("/jobs/{id}/theme", (string id, ThemeBody body, JobStore store, ThemeCatalog themes) =>
        {
            if (body.Theme is not null && themes.Folder(body.Theme) is null) return Results.BadRequest($"Unknown theme '{body.Theme}'.");
            var job = store.Update(id, s =>
            {
                s.Theme = body.Theme;
                s.Timeline.Add(new TimelineEvent(DateTimeOffset.Now, "theme.changed", body.Theme));
            });
            return job is null ? Results.NotFound() : Results.Ok(job);
        });

        // Render the PDF: only on request, and only when there's a resume that isn't being rewritten right now.
        api.MapPost("/jobs/{id}/render", (string id, JobStore store, RenderQueue renders, ResumeRenderer renderer) =>
        {
            if (store.Get(id) is not { } current) return Results.NotFound();
            if (renderer.Chrome is null) return Results.Conflict("Chrome was not found, so PDFs can't be rendered.");
            if (!File.Exists(Path.Combine(current.Folder, "resume.json"))) return Results.Conflict("There's no tailored resume to render yet.");
            if (IsBusy(current.State)) return Results.Conflict("The resume is being written. Render once it's done.");
            if (current.State.Render is RenderState.Queued or RenderState.Rendering) return Results.Conflict("A PDF is already being rendered.");
            var job = store.Update(id, s =>
            {
                s.Render = RenderState.Queued;
                s.RenderError = null;
            });
            renders.Enqueue(id);
            return Results.Ok(job);
        });

        api.MapPost("/jobs/capture", (CaptureRequest request, JobStore store) =>
        {
            var frames = request.Frames ?? [];
            var top = frames.FirstOrDefault(f => f.IsTop) ?? frames.FirstOrDefault();
            var text = Capture.CombineText(frames);
            if (string.IsNullOrWhiteSpace(text)) return Results.BadRequest("The captured page has no text.");

            var job = store.Create(JobSource.Extension, Capture.CleanUrl(request.Url ?? top?.Url), request.Title ?? top?.Title, text,
                top?.Html is { } html ? Capture.StripHtml(html) : null, out var created);
            return Results.Ok(Created(job, created));
        });

        api.MapPost("/jobs/paste", (PasteRequest request, JobStore store) =>
        {
            if (string.IsNullOrWhiteSpace(request.Text)) return Results.BadRequest("Paste the job advert text.");
            var url = string.IsNullOrWhiteSpace(request.Url) ? null : Capture.CleanUrl(request.Url);
            var job = store.Create(JobSource.Paste, url, null, request.Text.Trim(), null, out var created);
            return Results.Ok(Created(job, created));
        });

        api.MapPost("/jobs/{id}/changes", (string id, ChangeRequestBody body, JobStore store, JobQueue queue) =>
        {
            if (string.IsNullOrWhiteSpace(body.Text)) return Results.BadRequest("Describe the change you want.");
            var job = store.Update(id, s =>
            {
                var now = DateTimeOffset.Now;
                s.ChangeRequests.Add(new ChangeRequest { At = now, Text = body.Text.Trim() });
                s.Timeline.Add(new TimelineEvent(now, "change.requested", body.Text.Trim()));
                if (s.Status is ProcessingStatus.Processed or ProcessingStatus.Failed)
                {
                    s.Status = ProcessingStatus.Queued;
                    s.Error = null;
                }
            });
            if (job is null) return Results.NotFound();
            // Enqueue even when the job is mid-flight: the worker re-checks for pending changes. A held job keeps
            // its change for when it's resumed.
            if (job.Status != ProcessingStatus.OnHold) queue.Enqueue(id);
            return Results.Ok(job);
        });

        // ---- queue management ----

        api.MapGet("/queue", (JobQueue queue) => queue.State);

        api.MapPost("/queue/pause", (JobQueue queue) =>
        {
            queue.SetPaused(true);
            return queue.State;
        });

        api.MapPost("/queue/resume", (JobQueue queue) =>
        {
            queue.SetPaused(false);
            return queue.State;
        });

        // Queued: taken out of the queue. Running: its Claude call is stopped. Either way it ends up on hold.
        api.MapPost("/jobs/{id}/hold", (string id, JobStore store, JobQueue queue) =>
        {
            if (store.Get(id) is not { } current) return Results.NotFound();
            if (queue.StopRunning(id)) return Results.Accepted(); // the worker puts it on hold once it has stopped
            var waiting = queue.Remove(id);
            if (!waiting && current.State.Status != ProcessingStatus.Queued)
                return Results.Conflict("Only queued or running jobs can be put on hold.");
            var job = store.Update(id, s =>
            {
                s.Status = ProcessingStatus.OnHold;
                s.Timeline.Add(new TimelineEvent(DateTimeOffset.Now, "held"));
            });
            return Results.Ok(job);
        });

        api.MapPost("/jobs/{id}/resume", (string id, JobStore store, JobQueue queue) =>
        {
            if (store.Get(id) is not { } current) return Results.NotFound();
            if (current.State.Status != ProcessingStatus.OnHold) return Results.Conflict("The job isn't on hold.");
            var job = store.Update(id, s =>
            {
                s.Status = ProcessingStatus.Queued;
                s.Timeline.Add(new TimelineEvent(DateTimeOffset.Now, "resumed"));
            });
            queue.Enqueue(id);
            return Results.Ok(job);
        });

        // ---- application log ----

        api.MapPost("/jobs/{id}/events", (string id, ApplicationEventBody body, JobStore store) =>
        {
            if (body.Type is null || !ApplicationEventTypes.All.TryGetValue(body.Type, out var type))
                return Results.BadRequest($"Unknown event type. Use one of: {string.Join(", ", ApplicationEventTypes.All.Keys)}.");
            var note = string.IsNullOrWhiteSpace(body.Note) ? null : body.Note.Trim();
            if (body.Type == "note" && note is null) return Results.BadRequest("Write the note.");
            var job = store.Update(id, s =>
            {
                var at = body.At ?? DateTimeOffset.Now;
                s.ApplicationEvents.Add(new ApplicationEvent(System.Security.Cryptography.RandomNumberGenerator.GetString("abcdefghijklmnopqrstuvwxyz0123456789", 8), at, body.Type, note));
                // The employer doesn't respond to an application that was never sent.
                if (type.Response && s.ApplicationStatus == ApplicationStatus.NotApplied) s.ApplicationStatus = ApplicationStatus.Applied;
                if (type.Status is { } status) s.ApplicationStatus = status;
            });
            return job is null ? Results.NotFound() : Results.Ok(job);
        });

        api.MapDelete("/jobs/{id}/events/{eventId}", (string id, string eventId, JobStore store) =>
            store.Update(id, s => s.ApplicationEvents.RemoveAll(e => e.Id == eventId)) is { } job ? Results.Ok(job) : Results.NotFound());

        // The go-ahead for a scored job's first tailoring.
        api.MapPost("/jobs/{id}/tailor", (string id, JobStore store, JobQueue queue) =>
        {
            if (store.Get(id) is not { } current) return Results.NotFound();
            if (IsBusy(current.State)) return Results.Conflict("The job is being processed.");
            var job = store.Update(id, s =>
            {
                s.TailorApproved = true;
                s.Status = ProcessingStatus.Queued;
                s.Error = null;
                s.Timeline.Add(new TimelineEvent(DateTimeOffset.Now, "tailor.requested"));
            });
            queue.Enqueue(id);
            return Results.Ok(job);
        });

        // (Re)compute the match score, e.g. for jobs captured before scoring existed or after editing your resume.
        api.MapPost("/jobs/{id}/score", (string id, JobStore store, JobQueue queue) =>
        {
            if (store.Get(id) is not { } current) return Results.NotFound();
            if (IsBusy(current.State)) return Results.Conflict("The job is being processed.");
            var hasResume = File.Exists(Path.Combine(current.Folder, "resume.json"));
            var job = store.Update(id, s =>
            {
                s.Match = null;
                if (!hasResume) s.TailorApproved = false; // Score only scores; tailoring waits for Tailor
                if (s.Status is ProcessingStatus.New or ProcessingStatus.OnHold or ProcessingStatus.Processed or ProcessingStatus.ReadyToTailor or ProcessingStatus.Failed)
                    s.Status = ProcessingStatus.Queued;
                s.Error = null;
            });
            queue.Enqueue(id);
            return Results.Ok(job);
        });

        api.MapPost("/jobs/{id}/prioritize", (string id, JobQueue queue) =>
            queue.MoveToFront(id) ? Results.Ok(queue.State) : Results.Conflict("The job isn't waiting in the queue."));

        api.MapPost("/jobs/{id}/retry", (string id, JobStore store, JobQueue queue) =>
        {
            if (store.Get(id) is not { } current) return Results.NotFound();
            if (current.State.Status != ProcessingStatus.Failed) return Results.Conflict("Only failed jobs can be retried.");
            var job = store.Update(id, s =>
            {
                foreach (var c in s.ChangeRequests.Where(c => c.Status == ChangeRequestStatus.Failed))
                {
                    c.Status = ChangeRequestStatus.Pending;
                    c.Error = null;
                }
                s.Timeline.Add(new TimelineEvent(DateTimeOffset.Now, "retried"));
                s.Status = ProcessingStatus.Queued;
                s.Error = null;
            });
            queue.Enqueue(id);
            return Results.Ok(job);
        });

        // Tailor again from scratch: the pipeline sees no resume.json and redoes that step.
        api.MapPost("/jobs/{id}/regenerate", (string id, JobStore store, JobQueue queue) =>
            Redo(id, store, queue, "regenerated", ["resume.json", "notes.md", "resume.pdf"]));

        // Extract again (e.g. to pick up recruiter and dates); the tailored resume is kept.
        api.MapPost("/jobs/{id}/reextract", (string id, JobStore store, JobQueue queue) =>
            Redo(id, store, queue, "reextracted", ["spec.md"]));

        api.MapPost("/jobs/{id}/application-status", (string id, ApplicationStatusBody body, JobStore store, JobQueue queue) =>
        {
            if (store.Get(id) is not { } current) return Results.NotFound();
            if (body.Status != ApplicationStatus.Dropped)
                return Results.Ok(store.Update(id, s => s.ApplicationStatus = body.Status));

            // Dropping: never process it again unless you start it yourself.
            if (IsBusy(current.State)) return Results.Conflict("The job is being processed. Stop it first.");
            queue.Remove(id);
            var folder = current.Folder;
            var job = store.Update(id, s =>
            {
                s.ApplicationStatus = ApplicationStatus.Dropped;
                if (s.Status is ProcessingStatus.Queued or ProcessingStatus.OnHold)
                    s.Status = File.Exists(Path.Combine(folder, "resume.json")) ? ProcessingStatus.Processed
                        : s.Match is not null ? ProcessingStatus.ReadyToTailor
                        : ProcessingStatus.New;
            });
            return Results.Ok(job);
        });

        api.MapDelete("/jobs/{id}", (string id, JobStore store, JobQueue queue) =>
        {
            if (store.Get(id) is not { } current) return Results.NotFound();
            if (IsBusy(current.State)) return Results.Conflict("The job is being processed. Stop it first.");
            queue.Remove(id);
            store.Delete(id);
            return Results.NoContent();
        });

        api.MapGet("/events", async (HttpContext http, JobEvents events, CancellationToken ct) =>
        {
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            await http.Response.WriteAsync(": connected\n\n", ct);
            await http.Response.Body.FlushAsync(ct);
            try
            {
                await foreach (var e in events.Subscribe(ct))
                {
                    await http.Response.WriteAsync($"event: {e.Type}\ndata: {e.Data}\n\n", ct);
                    await http.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                // browser went away
            }
        });
    }

    /// <summary>A new job is not queued: it waits as New until you click Score or Tailor. A known advert is reported as a duplicate.</summary>
    private static CreatedResponse Created(JobSummary job, bool created) =>
        created ? new CreatedResponse(job.Id, true, job.DuplicateOf) : new CreatedResponse(job.Id, false, [job.Id]);

    /// <summary>Deletes a step's outputs and re-queues the job, which makes the pipeline redo that step.</summary>
    private static IResult Redo(string id, JobStore store, JobQueue queue, string timelineEvent, string[] outputs)
    {
        if (store.Get(id) is not { } current) return Results.NotFound();
        if (IsBusy(current.State)) return Results.Conflict("The job is being processed.");
        foreach (var name in outputs) File.Delete(Path.Combine(current.Folder, name));
        var job = store.Update(id, s =>
        {
            s.Timeline.Add(new TimelineEvent(DateTimeOffset.Now, timelineEvent));
            s.Status = ProcessingStatus.Queued;
            s.Error = null;
            if (outputs.Contains("resume.json"))
            {
                s.Warnings = [];
                s.TailorApproved = true; // Regenerate is itself the go-ahead
            }
        });
        queue.Enqueue(id);
        return Results.Ok(job);
    }

    /// <summary>Theme files (CSS, fonts, images), personal themes first. Outside /api so relative url()s in CSS work.</summary>
    public static void MapThemeFiles(this WebApplication app) =>
        app.MapGet("/themes/{id}/{**file}", (string id, string file, ThemeCatalog themes) =>
        {
            if (themes.Folder(id) is not { } folder) return Results.NotFound();
            var path = Path.GetFullPath(Path.Combine(folder, file));
            if (!path.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                return Results.NotFound();
            return ContentTypes.TryGetContentType(path, out var type) ? Results.File(path, type) : Results.NotFound();
        });

    private static readonly Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider ContentTypes = new();

    /// <summary>"Jane_Doe_CV.pdf" rather than "resume.pdf" when you save it to send.</summary>
    private static string? DownloadName(string folder, string file)
    {
        if (!file.Equals("resume.pdf", StringComparison.OrdinalIgnoreCase)) return file;
        try
        {
            var resume = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "resume.json")));
            var name = Slug.Make(resume?["basics"]?["name"]?.GetValue<string>(), 60, '_', lowercase: false);
            return name.Length > 0 ? $"{name}_CV.pdf" : file;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return file;
        }
    }

    private static bool IsBusy(JobState s) =>
        s.Status is ProcessingStatus.Extracting or ProcessingStatus.Scoring or ProcessingStatus.Tailoring
            or ProcessingStatus.Rendering or ProcessingStatus.Reviewing;
}

public static partial class Capture
{
    /// <summary>The top frame's text, plus any substantial embedded frame (ATS widgets often live in iframes).</summary>
    public static string CombineText(IReadOnlyList<CaptureFrame> frames)
    {
        var sb = new StringBuilder();
        foreach (var frame in frames.OrderByDescending(f => f.IsTop))
        {
            var text = frame.Text?.Trim();
            if (string.IsNullOrEmpty(text)) continue;
            if (!frame.IsTop)
            {
                if (text.Length < 200) continue;
                sb.AppendLine().AppendLine($"----- embedded frame: {frame.Url} -----");
            }
            sb.AppendLine(text);
        }
        return sb.ToString();
    }

    private static readonly HashSet<string> TrackingParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "trackingId", "refId", "trk", "trkInfo", "eBP", "alternateChannel", "lipi", "fbclid", "gclid", "msclkid", "mc_cid", "mc_eid",
    };

    /// <summary>
    /// The stable form of an advert URL: tracking parameters removed, and LinkedIn's many job URL shapes
    /// (search pages with currentJobId, view pages with tracking tokens) reduced to /jobs/view/&lt;id&gt;/.
    /// </summary>
    public static string? CleanUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return url;
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);

        if (uri.Host.EndsWith("linkedin.com", StringComparison.OrdinalIgnoreCase))
        {
            var match = LinkedInJobPath().Match(uri.AbsolutePath);
            var jobId = match.Success ? match.Groups[1].Value : query["currentJobId"];
            if (!string.IsNullOrEmpty(jobId)) return $"https://www.linkedin.com/jobs/view/{jobId}/";
        }

        var kept = query.AllKeys
            .Where(k => k is not null && !k.StartsWith("utm_", StringComparison.OrdinalIgnoreCase) && !TrackingParameters.Contains(k))
            .Select(k => $"{Uri.EscapeDataString(k!)}={Uri.EscapeDataString(query[k] ?? "")}");
        return new UriBuilder(uri) { Query = string.Join('&', kept), Fragment = "" }.Uri.ToString();
    }

    [GeneratedRegex(@"/jobs/view/(?:[^/]*?-)?(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex LinkedInJobPath();

    /// <summary>
    /// "text → href" for every person-profile, mailto: and tel: link on the page, so extraction can
    /// attach a profile URL or address to a recruiter it finds in the visible text.
    /// </summary>
    public static string ContactLinks(string html)
    {
        var links = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // href → longest anchor text
        foreach (Match m in Anchors().Matches(html))
        {
            var href = WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
            var isProfile = href.Contains("linkedin.com/in/", StringComparison.OrdinalIgnoreCase);
            if (!isProfile && !href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) && !href.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
                continue;
            if (isProfile && href.IndexOf('?') is var q and >= 0) href = href[..q];
            var text = WebUtility.HtmlDecode(Whitespace().Replace(Tags().Replace(m.Groups[2].Value, " "), " ")).Trim();
            if (!links.TryGetValue(href, out var existing) || text.Length > existing.Length) links[href] = text;
        }
        return string.Join('\n', links.Take(150).Select(l => $"{(l.Value.Length > 0 ? l.Value : "(no text)")} → {l.Key}"));
    }

    [GeneratedRegex(@"<a\b[^>]*?\shref\s*=\s*""([^""]*)""[^>]*>(.*?)</a\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Anchors();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>Drops scripts, styles, SVGs and inline data so the archived page stays small.</summary>
    public static string StripHtml(string html) =>
        DataUris().Replace(Comments().Replace(NoiseElements().Replace(html, ""), ""), "data:");

    [GeneratedRegex(@"<(script|style|noscript|svg|template)\b[^>]*>.*?</\1\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex NoiseElements();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comments();

    [GeneratedRegex(@"data:[^""'\s)]{256,}")]
    private static partial Regex DataUris();
}
