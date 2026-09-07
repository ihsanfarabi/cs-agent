using System.Collections.Concurrent;
using CsAgent.Core;
using Microsoft.AspNetCore.Mvc;

namespace CsAgent.Http;

public enum AskJobStatus
{
    Running,
    Done,
    Errored,
}

/// <summary>
/// One job per ask. In-process ConcurrentDictionary — no eviction, no
/// persistence: a restart loses every id (poll → 404), resubmit to recover.
/// Documented v1 limitation (design increment D18).
/// </summary>
public sealed record AskJob(
    string Id,
    AskJobStatus Status,
    AskResult? Result,
    ProblemDetails? Error,
    int? ErrorStatus);

/// <summary>
/// Job executor for the 202 upgrade: submit → background execution → poll.
/// Execution serializes behind one SemaphoreSlim(1) — the singleton pipeline
/// (eng-review D2) and its single non-thread-safe SqliteConnection are shared
/// across jobs; queued jobs run one at a time.
/// </summary>
public sealed class AskJobStore
{
    private readonly ConcurrentDictionary<string, AskJob> _jobs = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AskPipeline _pipeline;
    private readonly CancellationToken _shutdown;

    public AskJobStore(AskPipeline pipeline, CancellationToken shutdown = default)
    {
        _pipeline = pipeline;
        _shutdown = shutdown;
    }

    public AskJob? Get(string id) => _jobs.TryGetValue(id, out var job) ? job : null;

    public string Submit(string question)
    {
        var id = Guid.NewGuid().ToString("N");
        _jobs[id] = new AskJob(id, AskJobStatus.Running, null, null, null);
        _ = RunAsync(id, question);
        return id;
    }

    private async Task RunAsync(string id, string question)
    {
        AskJob completed;
        try
        {
            await _gate.WaitAsync(_shutdown);
            try { completed = Execute(id, question); }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException)
        {
            // shutdown cancelled a queued or running job — recorded honestly;
            // every id dies with the process anyway (in-memory store)
            var (status, problem) = ProblemMapping.Cancelled();
            completed = new AskJob(id, AskJobStatus.Errored, null, problem, status);
        }
        _jobs[id] = completed;
    }

    private AskJob Execute(string id, string question)
    {
        try
        {
            // verdict-before-output holds: Done is only recorded after the
            // resolve/escalate verdict exists inside AskResult
            var result = _pipeline.Run(question, _shutdown);
            return new AskJob(id, AskJobStatus.Done, result, null, null);
        }
        catch (Exception ex)
        {
            var (status, problem) = ProblemMapping.Map(ex);
            return new AskJob(id, AskJobStatus.Errored, null, problem, status);
        }
    }
}