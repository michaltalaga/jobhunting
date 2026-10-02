using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace JobHunting.Server;

/// <summary>Jobs waiting for a PDF. Separate from the Claude queue, and worked through one at a time.</summary>
public sealed class RenderQueue
{
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>();
    private readonly Lock _gate = new();
    private readonly HashSet<string> _queued = [];

    public bool Enqueue(string id)
    {
        lock (_gate)
            if (!_queued.Add(id)) return false;
        _channel.Writer.TryWrite(id);
        return true;
    }

    public async IAsyncEnumerable<string> ReadAllAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var id in _channel.Reader.ReadAllAsync(ct))
        {
            lock (_gate) _queued.Remove(id);
            yield return id;
        }
    }
}

/// <summary>Renders requested PDFs, one at a time. Only ever started by the Render button.</summary>
public sealed class RenderWorker(
    JobStore store, RenderQueue queue, ResumeRenderer renderer, TailoringSettings tailoring, ILogger<RenderWorker> logger) : BackgroundService
{
    private const string PageWarning = "The PDF is ";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Renders that were requested or running when the server stopped pick up again.
        foreach (var job in store.List().Where(j => j.Render is RenderState.Queued or RenderState.Rendering))
        {
            store.Update(job.Id, s => s.Render = RenderState.Queued);
            queue.Enqueue(job.Id);
        }

        await foreach (var id in queue.ReadAllAsync(ct))
        {
            if (store.Get(id) is not { } job) continue; // deleted while queued
            var (state, folder) = job;
            if (!File.Exists(Path.Combine(folder, "resume.json")))
            {
                store.Update(id, s => { s.Render = RenderState.Failed; s.RenderError = "There's no tailored resume to render."; });
                continue;
            }

            store.Update(id, s => { s.Render = RenderState.Rendering; s.RenderError = null; });
            try
            {
                var (pages, theme) = await renderer.RenderAsync(id, folder, state.Theme, ct);
                var max = tailoring.Effective(state.Tailoring)["maxPages"]!.GetValue<int>();
                store.Update(id, s =>
                {
                    s.Render = RenderState.None;
                    s.PdfPages = pages;
                    s.PdfTheme = theme;
                    s.Timeline.Add(new TimelineEvent(DateTimeOffset.Now, "rendered", $"{pages} pages, {theme}"));
                    s.Warnings.RemoveAll(w => w.StartsWith(PageWarning, StringComparison.Ordinal));
                    if (pages > max)
                        s.Warnings.Add($"{PageWarning}{pages} pages; the limit is {max}. Request a change such as \"Cut it to {max} pages\".");
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return; // shutting down; the render resumes on next start
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Rendering job {Id} failed", id);
                store.Update(id, s => { s.Render = RenderState.Failed; s.RenderError = ex.Message; });
            }
        }
    }
}
