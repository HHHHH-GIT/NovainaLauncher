using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.Core;

namespace Launcher.App.ViewModels;

public sealed partial class AccountsViewModel : ViewModelBase
{
    public MainViewModel Main { get; }
    private CancellationTokenSource? _msCts;
    private CancellationTokenSource? _skinCts;
    private LittleSkinLogin? _currentSkinLogin;

    public ObservableCollection<AccountProfile> Accounts { get; } = new();

    // Dialog state
    [ObservableProperty]
    private bool _showOfflineModal = false;

    [ObservableProperty]
    private bool _showMicrosoftModal = false;

    [ObservableProperty]
    private bool _showLittleSkinModal = false;

    // Offline form
    [ObservableProperty]
    private string _offlineName = "";

    // Microsoft form
    [ObservableProperty]
    private string _deviceCode = "";

    [ObservableProperty]
    private string _verificationUrl = "https://microsoft.com/link";

    [ObservableProperty]
    private string _microsoftStatusText = "正在请求微软设备代码...";

    [ObservableProperty]
    private bool _isWaitingMicrosoft = false;

    // LittleSkin form
    [ObservableProperty]
    private string _littleSkinEmail = "";

    [ObservableProperty]
    private string _littleSkinPassword = "";

    [ObservableProperty]
    private string _littleSkinStatusText = "";

    [ObservableProperty]
    private bool _isAuthenticatingLittleSkin = false;

    [ObservableProperty]
    private bool _showLittleSkinProfilePicker = false;

    public ObservableCollection<LittleSkinProfile> LittleSkinProfiles { get; } = new();

    [ObservableProperty]
    private LittleSkinProfile? _selectedLittleSkinProfile;

    public AccountsViewModel(MainViewModel main)
    {
        Main = main;
        Main.Accounts.ProfileUpdated += ApplyUpdatedProfile;
    }

    public void LoadAccounts()
    {
        Accounts.Clear();
        var list = Main.Accounts.Load();
        foreach (var acc in list)
        {
            Accounts.Add(acc);
        }
    }

    private void SaveAccounts()
    {
        Main.Accounts.Save(Accounts);
    }

    [RelayCommand]
    public void SelectAccount(AccountProfile? account)
    {
        if (account is not null)
        {
            Main.CurrentAccount = account;
        }
    }

    [RelayCommand]
    public void SelectAndReturnHome(AccountProfile? account)
    {
        if (account is null) return;
        SelectAccount(account);
        Main.Navigate("Launch");
    }

    [RelayCommand]
    public async Task DeleteAccountAsync(AccountProfile? account)
    {
        if (account is null) return;
        var result = Launcher.App.Controls.AppDialog.Show($"确定要移除账户 \"{account.Name}\" 吗？", "移除确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            await Main.Accounts.RemoveAsync(account, CancellationToken.None);
        Accounts.Remove(account);
            SaveAccounts();

            if (Main.CurrentAccount?.Id == account.Id)
            {
                Main.CurrentAccount = Accounts.FirstOrDefault();
            }

            Main.Log.Write($"已移除账户: {account.Name}", LogLevel.Info, true);
        }
        catch (Exception ex)
        {
            Launcher.App.Controls.AppDialog.Show($"移除账户失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // --- Offline Login ---
    [RelayCommand]
    public void OpenOfflineModal()
    {
        CloseAllModals();
        OfflineName = "";
        ShowOfflineModal = true;
    }

    [RelayCommand]
    public void ConfirmOfflineLogin()
    {
        var name = OfflineName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            Launcher.App.Controls.AppDialog.Show("请输入玩家名", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var profile = AccountService.Offline(name);
            var existing = Accounts.FirstOrDefault(a => a.Kind == AccountKind.Offline && a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                Accounts.Remove(existing);
            }

            Accounts.Insert(0, profile);
            SaveAccounts();
            Main.CurrentAccount = profile;
            ShowOfflineModal = false;
            Main.Log.Write($"已添加离线账户: {name}", LogLevel.Info, true);
        }
        catch (Exception ex)
        {
            Launcher.App.Controls.AppDialog.Show(ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // --- Microsoft Login ---
    [RelayCommand]
    public void OpenMicrosoftModal()
    {
        CloseAllModals();
        DeviceCode = "";
        VerificationUrl = "https://microsoft.com/link";
        MicrosoftStatusText = "正在请求微软设备代码...";
        IsWaitingMicrosoft = true;
        ShowMicrosoftModal = true;

        _msCts?.Cancel();
        _msCts = new CancellationTokenSource();

        _microsoftTask = RunMicrosoftLoginAsync(_msCts.Token);
    }

    private async Task RunMicrosoftLoginAsync(CancellationToken token)
    {
        try
        {
            var clientId = Main.Settings.MicrosoftClientId;
            Main.Log.Write("正在启动微软设备码登录流程...", LogLevel.Info, true);

            var profile = await Main.Accounts.LoginMicrosoftAsync(clientId, info =>
            {
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    token.ThrowIfCancellationRequested();
                    DeviceCode = info.Code;
                    VerificationUrl = info.Url;
                    MicrosoftStatusText = $"请在浏览器中访问并在页面输入上方代码以完成授权";
                });
                return Task.CompletedTask;
            }, token);

            Application.Current?.Dispatcher.Invoke(() =>
            {
                token.ThrowIfCancellationRequested();
                var existing = Accounts.FirstOrDefault(a => a.Kind == AccountKind.Microsoft && a.Uuid == profile.Uuid);
                if (existing is not null) Accounts.Remove(existing);
                Accounts.Insert(0, profile);
                SaveAccounts();
                Main.CurrentAccount = profile;
                ShowMicrosoftModal = false;
                IsWaitingMicrosoft = false;
                Main.Log.Write($"微软账户登录成功: {profile.Name}", LogLevel.Info, true);
            });
        }
        catch (OperationCanceledException)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (_msCts?.Token != token) return;
                ShowMicrosoftModal = false;
                IsWaitingMicrosoft = false;
            });
        }
        catch (Exception ex)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (token.IsCancellationRequested || _msCts?.Token != token) return;
                IsWaitingMicrosoft = false;
                MicrosoftStatusText = "登录失败: " + ex.Message;
                Main.Log.Write($"微软登录失败: {ex.Message}", LogLevel.Error, true);
            });
        }
    }

    [RelayCommand]
    public void CopyDeviceCode()
    {
        if (!string.IsNullOrEmpty(DeviceCode))
        {
            Clipboard.SetText(DeviceCode);
            MicrosoftStatusText = "验证码已复制";
        }
    }

    [RelayCommand]
    public void OpenVerificationUrl()
    {
        if (!string.IsNullOrEmpty(VerificationUrl))
        {
            Process.Start(new ProcessStartInfo(VerificationUrl) { UseShellExecute = true });
        }
    }

    [RelayCommand]
    public void CancelMicrosoftLogin()
    {
        _msCts?.Cancel();
        ShowMicrosoftModal = false;
        IsWaitingMicrosoft = false;
    }

    // --- LittleSkin Login ---
    [RelayCommand]
    public void OpenLittleSkinModal()
    {
        CloseAllModals();
        LittleSkinEmail = "";
        LittleSkinPassword = "";
        LittleSkinStatusText = "";
        IsAuthenticatingLittleSkin = false;
        ShowLittleSkinProfilePicker = false;
        LittleSkinProfiles.Clear();
        _currentSkinLogin = null;
        ShowLittleSkinModal = true;
    }

    [RelayCommand]
    public async Task StartLittleSkinLoginAsync()
    {
        if (string.IsNullOrWhiteSpace(LittleSkinEmail) || string.IsNullOrWhiteSpace(LittleSkinPassword))
        {
            LittleSkinStatusText = "请输入邮箱和密码";
            return;
        }

        IsAuthenticatingLittleSkin = true;
        LittleSkinStatusText = "正在连接 LittleSkin...";

        try
        {
            _skinCts?.Cancel();
            _skinCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var token = _skinCts.Token;
            var login = await Main.Accounts.LoginLittleSkinAsync(LittleSkinEmail.Trim(), LittleSkinPassword, token);
            token.ThrowIfCancellationRequested();
            _currentSkinLogin = login;

            if (login.Profiles.Count == 1)
            {
                await SelectAndFinishLittleSkinAsync(login.Profiles[0]);
            }
            else
            {
                LittleSkinProfiles.Clear();
                foreach (var p in login.Profiles) LittleSkinProfiles.Add(p);
                SelectedLittleSkinProfile = LittleSkinProfiles.FirstOrDefault();
                ShowLittleSkinProfilePicker = true;
                LittleSkinStatusText = "请选择游戏角色";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!ShowLittleSkinModal) return;
            LittleSkinStatusText = ex.Message;
            Main.Log.Write($"LittleSkin 登录失败: {ex.Message}", LogLevel.Error, true);
        }
        finally
        {
            LittleSkinPassword = "";
            IsAuthenticatingLittleSkin = false;
        }
    }

    [RelayCommand]
    public async Task ConfirmLittleSkinProfileAsync()
    {
        if (SelectedLittleSkinProfile is null || _currentSkinLogin is null) return;
        await SelectAndFinishLittleSkinAsync(SelectedLittleSkinProfile);
    }

    private async Task SelectAndFinishLittleSkinAsync(LittleSkinProfile profile)
    {
        try
        {
            IsAuthenticatingLittleSkin = true;
            LittleSkinStatusText = "正在绑定角色...";
            _skinCts?.Cancel();
            _skinCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var token = _skinCts.Token;
            var account = await Main.Accounts.SelectLittleSkinAsync(LittleSkinEmail.Trim(), _currentSkinLogin!, profile, token);
            token.ThrowIfCancellationRequested();

            var existing = Accounts.FirstOrDefault(a => a.Kind == AccountKind.LittleSkin && a.Uuid == account.Uuid);
            if (existing is not null) Accounts.Remove(existing);
            Accounts.Insert(0, account);
            SaveAccounts();
            Main.CurrentAccount = account;
            ShowLittleSkinModal = false;
            Main.Log.Write($"LittleSkin 账户添加成功: {account.Name}", LogLevel.Info, true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!ShowLittleSkinModal) return;
            LittleSkinStatusText = ex.Message;
        }
        finally
        {
            LittleSkinPassword = "";
            IsAuthenticatingLittleSkin = false;
        }
    }

    [RelayCommand]
    public void CloseAllModals()
    {
        _msCts?.Cancel();
        _skinCts?.Cancel();
        LittleSkinPassword = "";
        _currentSkinLogin = null;
        ShowOfflineModal = false;
        ShowMicrosoftModal = false;
        ShowLittleSkinModal = false;
        IsWaitingMicrosoft = false;
        IsAuthenticatingLittleSkin = false;
    }

    private Task? _microsoftTask;
    public async Task CancelAndWaitAsync()
    {
        CloseAllModals();
        await StopSessionMaintenanceAsync();
        await StopSkinsAsync();
        var tasks = new[] { _microsoftTask, StartLittleSkinLoginCommand.ExecutionTask, ConfirmLittleSkinProfileCommand.ExecutionTask, DeleteAccountCommand.ExecutionTask };
        foreach (var task in tasks) if (task is not null) await task;
    }
}
