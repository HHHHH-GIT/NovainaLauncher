using CmlLib.Core.Auth;
using Launcher.Core;

namespace Launcher.App.ViewModels;

public sealed partial class AccountsViewModel
{
    private CancellationTokenSource _sessionLifetime = new();
    private Task? _sessionMaintenanceTask;
    private Task? _accountRenewalTask;
    private Task? _accountRetryTask;

    public void StartSessionMaintenance()
    {
        if (_sessionMaintenanceTask is { IsCompleted: false }) return;
        if (_sessionLifetime.IsCancellationRequested) { _sessionLifetime.Dispose(); _sessionLifetime = new(); }
        var token = _sessionLifetime.Token;
        _sessionMaintenanceTask = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
                do
                {
                    await Main.UiDispatcher.InvokeAsync(() => RenewCurrentAccountAsync(token)).Task.Unwrap();
                } while (await timer.WaitForNextTickAsync(token));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }, token);
    }

    private async Task RenewCurrentAccountAsync(CancellationToken token)
    {
        // Launch awaits this task before capturing its credentials. Never rotate a game's token
        // after preparation has begun or while Minecraft is running.
        if (Main.IsLaunching || Main.SettingsVM.IsChangingData || ShowLittleSkinModal || _accountRetryTask is { IsCompleted: false }
            || Main.CurrentAccount is not { Kind: AccountKind.LittleSkin } account || token.IsCancellationRequested) return;
        _accountRenewalTask = Task.Run(async () =>
        {
            if (Main.Accounts.IsLittleSkinRenewalDue(account)) await Main.Accounts.SessionAsync(account, Main.Settings.MicrosoftClientId, token);
        }, token);
        try { await _accountRenewalTask; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { Main.Log.Write("LittleSkin 自动续期：" + Main.Log.Redact(error.Message), LogLevel.Warning); }
    }

    public async Task WaitForSessionMaintenanceAsync(CancellationToken token)
    {
        foreach (var task in new[] { _accountRenewalTask, _accountRetryTask })
        {
            if (task is null) continue;
            try { await task.WaitAsync(token); }
            catch (Exception) when (!token.IsCancellationRequested) { /* Launch performs its own verification. */ }
        }
    }

    public async Task<MSession> RetryAccountLoginAsync(AccountProfile account, CancellationToken token)
    {
        await WaitForSessionMaintenanceAsync(token);
        token.ThrowIfCancellationRequested();
        if (Main.IsLaunching) throw new InvalidOperationException("游戏正在准备或运行，退出游戏后再刷新登录");
        if (Main.SettingsVM.IsChangingData || !Accounts.Any(a => a.Id == account.Id)) throw new InvalidOperationException("账户不可用，请查询当前账户列表");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _sessionLifetime.Token);
        var clientId = Main.Settings.MicrosoftClientId;
        var task = Task.Run(() => Main.Accounts.RetryLoginAsync(account, clientId, lifetime.Token), lifetime.Token);
        _accountRetryTask = task;
        return await task;
    }

    private void ApplyUpdatedProfile(AccountProfile profile)
    {
        Main.UiDispatcher.InvokeAsync(() =>
        {
            var index = Accounts.ToList().FindIndex(a => a.Id == profile.Id);
            if (index < 0) return;
            Accounts[index] = profile;
            if (Main.CurrentAccount?.Id == profile.Id) Main.CurrentAccount = profile;
        });
    }

    private async Task StopSessionMaintenanceAsync()
    {
        _sessionLifetime.Cancel();
        foreach (var task in new[] { _sessionMaintenanceTask, _accountRenewalTask, _accountRetryTask })
        {
            if (task is null) continue;
            try { await task; } catch (OperationCanceledException) { } catch (Exception e) { Main.Log.Write("登录维护已停止：" + Main.Log.Redact(e.Message), LogLevel.Warning); }
        }
    }
}
