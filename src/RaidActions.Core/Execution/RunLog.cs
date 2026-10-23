using System.Text.Json;
using System.Text.Json.Serialization;

namespace RaidActions.Execution;

/// <param name="Result">"started", "ok", "failed", "aborted", "info".</param>
public sealed record LogEntry(DateTimeOffset Time, string RunId, string PlanId, int Step, string Action, string Input, string Result, string? Detail);

/// <summary>
/// The local log of a run: one JSON object per line (time, step, action, the input it stands for, result, verification detail),
/// written and flushed as it happens, so it survives a crash. output/logs/&lt;stamp&gt;-&lt;planId&gt;.jsonl.
/// </summary>
public sealed class RunLog : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly StreamWriter? _writer;
    private readonly object _lock = new();
    private readonly List<LogEntry> _entries = [];

    public string RunId { get; }
    public string PlanId { get; }
    public string? Path { get; }
    public IReadOnlyList<LogEntry> Entries { get { lock (_lock) return _entries.ToList(); } }

    /// <summary>A log in <paramref name="folder"/>, or in memory only when folder is null (tests).</summary>
    public RunLog(string? folder, string planId, DateTimeOffset now)
    {
        PlanId = planId;
        RunId = $"{now:yyyyMMdd-HHmmss}-{planId[..Math.Min(8, planId.Length)]}";
        if (folder is null) return;
        Directory.CreateDirectory(folder);
        Path = System.IO.Path.Combine(folder, RunId + ".jsonl");
        _writer = new StreamWriter(new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
    }

    public LogEntry Write(int step, string action, string input, string result, string? detail = null)
    {
        var e = new LogEntry(DateTimeOffset.Now, RunId, PlanId, step, action, input, result, detail);
        lock (_lock)
        {
            _entries.Add(e);
            _writer?.WriteLine(JsonSerializer.Serialize(e, Json));
        }
        return e;
    }

    public void Dispose()
    {
        lock (_lock) _writer?.Dispose();
    }
}
