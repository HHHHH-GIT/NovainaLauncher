namespace Launcher.AI;

/// <summary>One user turn, including delegated agents and compaction. Never multiplies the call allowance.</summary>
public sealed class AgentModelBudget(int limit = 24)
{
    private readonly int _limit = Math.Clamp(limit, 1, 24);
    private int _calls, _output;
    public int Remaining => Math.Max(0, _limit - Volatile.Read(ref _calls));
    public int OutputTokens => Volatile.Read(ref _output);
    public bool TryStartCall()
    {
        while (true) { var current = Volatile.Read(ref _calls); if (current >= _limit) return false; if (Interlocked.CompareExchange(ref _calls, current + 1, current) == current) return true; }
    }
    public void RecordOutput(int tokens) => Interlocked.Add(ref _output, Math.Max(0, tokens));
    public void ResetOutput() => Interlocked.Exchange(ref _output, 0);
}
