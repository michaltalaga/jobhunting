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

        api.MapGet("/setup", async (Paths paths, ClaudeRunner claude) =>
        {
            var problems = new List<string>();
            if (!File.Exists(paths.MasterResume))
                problems.Add($"No master resume at {paths.MasterResume}. Save your resume there in JSON Resume format " +
                             "(https://jsonresume.org/schema), or point JobHunting:MasterResumePath at it in data/settings.json.");
            var version = await claude.VersionAsync();
            if (version is null)
                problems.Add("The Claude CLI ('claude') was not found on PATH. Install Claude Code, run 'claude' once to log in, then restart this server.");
            return new SetupStatus(problems, paths.MasterResume, File.Exists(paths.Highlights) ? paths.Highlights : null, version);
        });

        api.MapGet("/jobs", (JobStore store) => store.List());

        api.MapGet("/jobs/{id}", (string id, JobStore store) =>
            store.Detail(id) is { } detail ? Results.Ok(detail) : Results.NotFound());

        api.MapGet("/jobs/{id}/files/{name}", (string id, string name, JobStore store, HttpContext http) =>
        {
            if (!JobFiles.TryGetValue(name, out var contentType) || store.Get(id) is not { } job) return Results.NotFound();
            var path = Path.Combine(job.Folder, name);
            if (!File.Exists(path)) return Results.NotFound();
            // The captured page is untrusted markup: never let it run script on this origin.
            if (name.Equals("page.html", StringComparison.OrdinalIgnoreCase))
                http.Response.Headers.ContentSecurityPolicy = "sandbox";
            return Results.File(path, contentType);
        });

        api.MapPost("/jobs/capture", (CaptureRequest request, JobStore store, JobQueue queue) =>
        {
            var frames = request.Frames ?? [];
            var top = frames.FirstOrDefault(f => f.IsTop) ?? frames.FirstOrDefault();
            var text = Capture.CombineText(frames);
            if (string.IsNullOrWhiteSpace(text)) return Results.BadRequest("The captured page has no text.");

            var job = store.Create(JobSource.Extension, Capture.CleanUrl(request.Url ?? top?.Url), request.Title ?? top?.Title, text,
                top?.Html is { } html ? Capture.StripHtml(html) : null);
            queue.Enqueue(job.Id);
            return Results.Ok(new CreatedResponse(job.Id, job.DuplicateOf));
        });

        api.MapPost("/jobs/paste", (PasteRequest request, JobStore store, JobQueue queue) =>
        {
            if (string.IsNullOrWhiteSpace(request.Text)) return Results.BadRequest("Paste the job advert text.");
            var url = string.IsNullOrWhiteSpace(request.Url) ? null : Capture.CleanUrl(request.Url);
            var job = store.Create(JobSource.Paste, url, null, request.Text.Trim(), null);
            queue.Enqueue(job.Id);
            return Results.Ok(new CreatedResponse(job.Id, job.DuplicateOf));
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
            // Always enqueue: if the job is mid-flight the worker re-checks for pending changes anyway.
            queue.Enqueue(id);
            return Results.Ok(job);
        });

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

        api.MapPost("/jobs/{id}/application-status", (string id, ApplicationStatusBody body, JobStore store) =>
            store.Update(id, s => s.ApplicationStatus = body.Status) is { } job ? Results.Ok(job) : Results.NotFound());

        api.MapDelete("/jobs/{id}", (string id, JobStore store) =>
        {
            if (store.Get(id) is not { } current) return Results.NotFound();
            if (IsBusy(current.State)) return Results.Conflict("The job is being processed.");
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
            if (outputs.Contains("resume.json")) s.Warnings = [];
        });
        queue.Enqueue(id);
        return Results.Ok(job);
    }

    private static bool IsBusy(JobState s) =>
        s.Status is ProcessingStatus.Extracting or ProcessingStatus.Tailoring or ProcessingStatus.Rendering;
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
