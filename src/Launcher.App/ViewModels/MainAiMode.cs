using CommunityToolkit.Mvvm.ComponentModel;
using Launcher.Core;

namespace Launcher.App.ViewModels;

public sealed partial class MainViewModel
{
    [ObservableProperty] private bool _isAiMode;
    [ObservableProperty] private bool _accountLoginRequested;
    private TaskCompletionSource? _accountLogin;
    public bool NormalNavigationVisible => !IsAiMode;
    public bool NormalSurfaceVisible => !IsAiMode || AccountLoginRequested;
    public bool AiSurfaceVisible => IsAiMode && !AccountLoginRequested;
    public bool TopNavigationVisible => !IsAiMode && !SidebarExpanded;
    public bool EffectiveSidebarExpanded => !IsAiMode && SidebarExpanded;
    public Task ModeChangeTask { get; private set; } = Task.CompletedTask;
    private string? _beforeLoginPage;
    partial void OnIsAiModeChanged(bool value)
    {
        OnPropertyChanged(nameof(NormalNavigationVisible)); OnPropertyChanged(nameof(TopNavigationVisible)); OnPropertyChanged(nameof(EffectiveSidebarExpanded));
        OnPropertyChanged(nameof(NormalSurfaceVisible)); OnPropertyChanged(nameof(AiSurfaceVisible));
        AccountsVM.CloseAllModals();
        ModeChangeTask = AgentVM.ModeChangedAsync(value);
    }
    partial void OnAccountLoginRequestedChanged(bool value) { OnPropertyChanged(nameof(NormalSurfaceVisible)); OnPropertyChanged(nameof(AiSurfaceVisible)); }
    public void RequestAccountLogin(AccountProfile? account = null)
    {
        _beforeLoginPage = CurrentPage; CurrentPage = "Accounts"; AccountLoginRequested = true;
        _accountLogin = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (account?.Kind == AccountKind.LittleSkin) { AccountsVM.OpenLittleSkinModal(); AccountsVM.LittleSkinEmail = account.LoginIdentifier; }
        else if (account?.Kind == AccountKind.Microsoft) AccountsVM.OpenMicrosoftModal();
    }
    public Task WaitAccountLoginAsync(CancellationToken cancellation) => (_accountLogin?.Task ?? Task.CompletedTask).WaitAsync(cancellation);
    public void CompleteAccountLogin() { AccountLoginRequested = false; _accountLogin?.TrySetResult(); if (_beforeLoginPage is { } page) { _beforeLoginPage = null; CurrentPage = page; } AccountsVM.CloseAllModals(); }
}
