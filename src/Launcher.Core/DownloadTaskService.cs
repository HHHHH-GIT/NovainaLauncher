using System.Threading.Channels;

namespace Launcher.Core;

/// <summary>Sequential installation queue. Plans, source policy and targets are frozen at enqueue.</summary>
public sealed class DownloadTaskService
{
    private sealed record Job(InstallPlan Plan, CancellationTokenSource Cancellation, TaskCompletionSource Done, Guid? GroupId)
    { public DownloadTaskInfo? Result { get; set; } }
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<Guid, Job> _jobs = new();
    private readonly Dictionary<Guid, DownloadTaskInfo> _states = new();
    private readonly object _gate = new();
    private long _lastUpdate;
    private readonly DownloadInstallService _installer;
    private readonly Func<string, bool> _directoryBusy;
    public event Action<DownloadTaskInfo>? Changed;
    public event Action<Guid>? Removed;
    public bool IsBusy { get { lock (_gate) return _states.Values.Any(s => s.IsActive); } }
    public bool IsDirectoryBusy(string directory) { lock (_gate) return _states.Values.Any(s => s.IsActive && string.Equals(Path.GetFullPath(_jobs[s.Id].Plan.Directory), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase)); }
    public DownloadTaskService(DownloadInstallService installer, Func<string, bool> directoryBusy)
    {
        _installer = installer; _directoryBusy = directoryBusy;
        _ = Task.Run(WorkerAsync);
    }
    public IReadOnlyList<DownloadTaskInfo> Snapshot() { lock (_gate) return _states.Values.ToArray(); }
    public async Task<DownloadTaskInfo> WaitAsync(Guid id, CancellationToken cancellation = default)
    {
        Job job; lock (_gate) job = _jobs.TryGetValue(id, out var found) ? found : throw new InvalidOperationException("任务不存在");
        await job.Done.Task.WaitAsync(cancellation);
        return job.Result ?? throw new InvalidOperationException("任务没有最终结果");
    }
    public async Task CancelGroupAsync(Guid groupId)
    {
        Task[] pending;
        lock (_gate) { var owned = _jobs.Where(p => p.Value.GroupId == groupId).ToArray(); foreach (var p in owned.Where(p => _states[p.Key].IsActive)) p.Value.Cancellation.Cancel(); pending = owned.Select(p => p.Value.Done.Task).ToArray(); }
        await Task.WhenAll(pending);
    }
    public Guid Enqueue(InstallPlan plan, Guid? groupId = null)
    {
        var id = Guid.NewGuid(); var state = new DownloadTaskInfo(id, plan.Name, DownloadTaskState.Queued, "等待中") { OverallProgress = new(0, InstallationWorkProgress.StepsFor(plan), "等待中") };
        lock (_gate)
        {
            if (_directoryBusy(plan.Directory) || IsDirectoryBusy(plan.Directory)) throw new InvalidOperationException("目标游戏正在启动、运行或处理文件");
            _jobs.Add(id, new(plan, new CancellationTokenSource(), new(TaskCreationOptions.RunContinuationsAsynchronously), groupId));
            _states.Add(id, state);
        }
        Changed?.Invoke(state); _queue.Writer.TryWrite(id); return id;
    }
    public Guid Retry(Guid id) { InstallPlan plan; lock (_gate) { if (!_states[id].CanRetry) throw new InvalidOperationException("此任务不能重试"); plan = _jobs[id].Plan; } return Enqueue(plan); }
    public bool Remove(Guid id)
    {
        Job job;
        lock (_gate)
        {
            if (!_states.TryGetValue(id, out var state) || !state.CanRemove) return false;
            _states.Remove(id);
            job = _jobs[id]; _jobs.Remove(id);
        }
        // The final UI event can arrive just before the worker's finally block.
        // Do not dispose the cancellation source until the worker has released it.
        _ = DisposeJobAsync(job);
        Removed?.Invoke(id);
        return true;
    }
    private static async Task DisposeJobAsync(Job job)
    {
        await job.Done.Task.ConfigureAwait(false);
        job.Cancellation.Dispose();
    }
    public void Cancel(Guid id) { lock (_gate) if (_jobs.TryGetValue(id, out var job) && _states[id].IsActive) { job.Cancellation.Cancel(); Set(id, _states[id] with { Message = "正在取消" }); } }
    public async Task CancelAndWaitAsync()
    {
        Task[] pending;
        lock (_gate) { foreach (var pair in _jobs.Where(p => _states[p.Key].IsActive)) pair.Value.Cancellation.Cancel(); pending = _jobs.Values.Select(j => j.Done.Task).ToArray(); }
        await Task.WhenAll(pending);
    }
    private void Set(Guid id, DownloadTaskInfo? state = null, InstallationWorkProgress? overall = null)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue(id, out var previous) || !previous.IsActive) return;
            overall ??= previous.OverallProgress;
            if (previous.OverallProgress is { } old && overall is { } next && next.Percent < old.Percent) overall = old;
            state = (state ?? previous) with { OverallProgress = overall };
            _states[id] = state;
            if (!state.IsActive) _jobs[id].Result = state;
            var now = Environment.TickCount64;
            if (previous.State == state.State && previous.OverallProgress == state.OverallProgress && state.IsActive && state.Message != "正在取消" && now - _lastUpdate < 100) return;
            _lastUpdate = now;
        }
        Changed?.Invoke(state);
    }
    private async Task WorkerAsync()
    {
        await foreach (var id in _queue.Reader.ReadAllAsync())
        {
            Job job; lock (_gate) job = _jobs[id];
            try
            {
                job.Cancellation.Token.ThrowIfCancellationRequested();
                if (_directoryBusy(job.Plan.Directory)) throw new InvalidOperationException("目标目录正在使用，请稍后重试");
                var version = await _installer.InstallAsync(job.Plan, (stage, message, progress) => Set(id, new(id, job.Plan.Name, stage, message, progress)), job.Cancellation.Token, p => Set(id, overall: p));
                Set(id, new(id, job.Plan.Name, DownloadTaskState.Completed, version is null ? "内容已下载" : "游戏已就绪", InstalledVersion: version));
            }
            catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested) { Set(id, new(id, job.Plan.Name, DownloadTaskState.Cancelled, "已取消")); }
            catch (Exception error) { Set(id, new(id, job.Plan.Name, DownloadTaskState.Failed, "安装失败", Error: error.Message)); }
            finally { job.Done.TrySetResult(); }
        }
    }
}
