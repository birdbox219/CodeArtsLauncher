using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Launcher.Publisher.Publishing;

/// <summary>
/// One push, with its event log kept in memory so the browser can reconnect (or be reloaded)
/// mid-upload and still see everything that happened.
/// </summary>
public sealed class PushJob
{
    private readonly object _gate = new();
    private readonly List<PushEvent> _events = new();
    private readonly List<Channel<PushEvent>> _subscribers = new();

    public PushJob(string id, PushRequest request)
    {
        Id = id;
        Request = request;
        StartedUtc = DateTimeOffset.UtcNow;
        Cts = new CancellationTokenSource();
    }

    public string Id { get; }
    public PushRequest Request { get; }
    public DateTimeOffset StartedUtc { get; }
    public CancellationTokenSource Cts { get; }

    public bool Completed { get; private set; }
    public PushOutcome? Outcome { get; private set; }

    /// <summary>Latest progress event, so a reconnecting page can restore the bar.</summary>
    public PushEvent? LastProgress { get; private set; }

    public IReadOnlyList<PushEvent> Events
    {
        get { lock (_gate) return _events.ToArray(); }
    }

    public void Append(PushEvent e)
    {
        lock (_gate)
        {
            if (e.Kind == "progress") LastProgress = e;

            // Progress events arrive many times a second; keeping every one would make the replay
            // log useless and grow without bound. Only the latest is kept in the backlog.
            if (e.Kind != "progress") _events.Add(e);

            foreach (var s in _subscribers) s.Writer.TryWrite(e);
        }
    }

    public void Complete(PushOutcome outcome)
    {
        lock (_gate)
        {
            if (Completed) return;
            Completed = true;
            Outcome = outcome;

            foreach (var s in _subscribers) s.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Replays what already happened, then streams the rest. Backlog and subscription are taken
    /// under one lock so an event landing between the two is not lost.
    /// </summary>
    public (IReadOnlyList<PushEvent> Backlog, ChannelReader<PushEvent> Live) Subscribe()
    {
        var channel = Channel.CreateUnbounded<PushEvent>();

        lock (_gate)
        {
            var backlog = _events.ToArray();
            if (Completed) channel.Writer.TryComplete();
            else _subscribers.Add(channel);
            return (backlog, channel.Reader);
        }
    }
}

/// <summary>
/// Holds the running push and the recent ones.
///
/// Exactly one push runs at a time on purpose: two concurrent pushes to the same channel race for
/// the head build, and butler's own diff is computed against whatever the head was when it started.
/// </summary>
public sealed class PushJobStore
{
    private readonly ConcurrentDictionary<string, PushJob> _jobs = new();
    private readonly object _gate = new();
    private PushJob? _current;

    public PushJob? Current
    {
        get { lock (_gate) return _current is { Completed: false } ? _current : null; }
    }

    public bool TryStart(PushRequest request, out PushJob? job, out string? error)
    {
        lock (_gate)
        {
            if (_current is { Completed: false })
            {
                job = null;
                error = $"A push is already running ({_current.Request.Slug}). Wait for it or cancel it.";
                return false;
            }

            job = new PushJob(Guid.NewGuid().ToString("n")[..12], request);
            _current = job;
            _jobs[job.Id] = job;
            Trim();
            error = null;
            return true;
        }
    }

    public PushJob? Get(string id) => _jobs.TryGetValue(id, out var job) ? job : null;

    private void Trim()
    {
        if (_jobs.Count <= 20) return;

        foreach (var old in _jobs.Values
                     .Where(j => j.Completed)
                     .OrderBy(j => j.StartedUtc)
                     .Take(_jobs.Count - 20))
        {
            _jobs.TryRemove(old.Id, out _);
        }
    }
}
