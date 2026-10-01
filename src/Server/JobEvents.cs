using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

namespace JobHunting.Server;

public sealed record ServerEvent(string Type, string Data);

/// <summary>Fans job changes out to every connected SPA (server-sent events).</summary>
public sealed class JobEvents
{
    private readonly Lock _gate = new();
    private readonly List<Channel<ServerEvent>> _subscribers = [];

    public void PublishJob(JobSummary job) =>
        Publish(new ServerEvent("job", JsonSerializer.Serialize(job, Json.Compact)));

    public void PublishDeleted(string id) =>
        Publish(new ServerEvent("deleted", JsonSerializer.Serialize(new { id }, Json.Compact)));

    private void Publish(ServerEvent e)
    {
        lock (_gate)
            foreach (var subscriber in _subscribers) subscriber.Writer.TryWrite(e);
    }

    public async IAsyncEnumerable<ServerEvent> Subscribe([EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<ServerEvent>();
        lock (_gate) _subscribers.Add(channel);
        try
        {
            await foreach (var e in channel.Reader.ReadAllAsync(ct)) yield return e;
        }
        finally
        {
            lock (_gate) _subscribers.Remove(channel);
        }
    }
}

/// <summary>Job ids waiting for the worker. An id is queued at most once at a time.</summary>
public sealed class JobQueue
{
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>();
    private readonly Lock _gate = new();
    private readonly HashSet<string> _queued = [];

    public void Enqueue(string id)
    {
        lock (_gate)
            if (!_queued.Add(id)) return;
        _channel.Writer.TryWrite(id);
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
