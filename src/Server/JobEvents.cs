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

    public void PublishQueue(QueueState state) =>
        Publish(new ServerEvent("queue", JsonSerializer.Serialize(state, Json.Compact)));

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

/// <summary>What the workers are running and what's waiting, in order.</summary>
public sealed record QueueState(bool Paused, IReadOnlyList<string> Running, IReadOnlyList<string> Waiting);

/// <summary>
/// The ordered list of job ids waiting for the workers, plus the jobs they're running. An id waits at most once,
/// and is never run twice at the same time: a running job may wait again (e.g. a change request arrived mid-run)
/// and is picked up once its current run ends. Pausing stops new jobs from starting; running ones finish.
/// Each running job can be stopped through its own cancellation token.
/// </summary>
public sealed class JobQueue(JobEvents events)
{
    private readonly Lock _gate = new();
    private readonly List<string> _waiting = [];
    private readonly Dictionary<string, CancellationTokenSource> _running = [];
    private bool _paused;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public QueueState State
    {
        get { lock (_gate) return Snapshot(); }
    }

    public void Enqueue(string id) => Change(() =>
    {
        if (_waiting.Contains(id)) return false;
        _waiting.Add(id);
        return true;
    });

    public bool Remove(string id) => Change(() => _waiting.Remove(id));

    public bool MoveToFront(string id) => Change(() =>
    {
        if (!_waiting.Remove(id)) return false;
        _waiting.Insert(0, id);
        return true;
    });

    /// <summary>Cancels the job if it's running. Its worker then puts it on hold.</summary>
    public bool StopRunning(string id)
    {
        lock (_gate)
        {
            if (!_running.TryGetValue(id, out var cts)) return false;
            cts.Cancel();
            return true;
        }
    }

    public void SetPaused(bool paused) => Change(() =>
    {
        if (_paused == paused) return false;
        _paused = paused;
        return true;
    });

    /// <summary>
    /// Waits for the next job that isn't already running (respecting pause) and marks it running.
    /// Its token is cancelled by Stop or shutdown. Each worker calls this in a loop.
    /// </summary>
    public async Task<(string Id, CancellationToken Token)> NextAsync(CancellationToken shutdown)
    {
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                var id = _paused ? null : _waiting.FirstOrDefault(w => !_running.ContainsKey(w));
                if (id is not null)
                {
                    _waiting.Remove(id);
                    var cts = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
                    _running[id] = cts;
                    Signal();
                    return (id, cts.Token);
                }
                changed = _changed.Task;
            }
            await changed.WaitAsync(shutdown);
        }
    }

    public void Finished(string id) => Change(() =>
    {
        if (!_running.Remove(id, out var cts)) return false;
        cts.Dispose();
        return true;
    });

    private QueueState Snapshot() => new(_paused, [.. _running.Keys], [.. _waiting]);

    private bool Change(Func<bool> mutate)
    {
        lock (_gate)
        {
            if (!mutate()) return false;
            Signal();
            return true;
        }
    }

    /// <summary>Wakes a waiting worker and tells the SPA. Call inside the lock.</summary>
    private void Signal()
    {
        _changed.TrySetResult();
        _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        events.PublishQueue(Snapshot());
    }
}
