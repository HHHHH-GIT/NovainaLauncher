using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.Core;
using Microsoft.Win32;

namespace Launcher.App.ViewModels;

public sealed partial class AccountsViewModel
{
    private CancellationTokenSource? _previewCancellation;
    private CancellationTokenSource? _skinOperationCancellation;
    private Task? _previewTask;
    private bool _settingModel;
    [ObservableProperty] private AccountSkinInfo? _previewSkin;
    [ObservableProperty] private bool _hasImportedSkin;
    [ObservableProperty] private bool _isSkinLoading;
    [ObservableProperty] private bool _isSkinOperation;
    [ObservableProperty] private string _skinStatus = "";
    [ObservableProperty] private string _skinModelKey = "Classic";
    public Func<Task>? StopPreviewAsync { get; set; }
    public string? PreviewTexturePath => PreviewSkin is null ? null : Main.Skins.TextureFile(PreviewSkin);
    public SkinModel PreviewModel => SkinModelKey == "Slim" ? SkinModel.Slim : SkinModel.Classic;
    public bool HasAccount => Main.CurrentAccount is not null;
    public bool ShowSkinEditor => HasAccount && !IsLittleSkinAccount;
    public bool CanImportSkin => ShowSkinEditor && !IsSkinOperation;
    public bool CanApplySkin => HasImportedSkin && !IsSkinOperation && Main.CurrentAccount?.Kind is AccountKind.Microsoft or AccountKind.Offline;
    public bool ShowApplySkin => HasImportedSkin && Main.CurrentAccount?.Kind is AccountKind.Microsoft or AccountKind.Offline;
    public string ApplySkinLabel => Main.CurrentAccount?.Kind == AccountKind.Microsoft ? "上传到 Minecraft" : "使用此皮肤";
    public bool IsLittleSkinAccount => Main.CurrentAccount?.Kind == AccountKind.LittleSkin;
    public bool PreviewPaused => ShowOfflineModal || ShowMicrosoftModal || ShowLittleSkinModal;
    public bool CanChooseSlim => PreviewSkin is not null && IsModernSkin();
    private bool IsModernSkin()
    {
        try { using var input = File.OpenRead(PreviewTexturePath!); input.Position = 20; Span<byte> size = stackalloc byte[4]; input.ReadExactly(size); return System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(size) == 64; }
        catch (IOException) { return false; }
    }
    partial void OnPreviewSkinChanged(AccountSkinInfo? value)
    {
        _settingModel = true; SkinModelKey = value?.Model == SkinModel.Slim ? "Slim" : "Classic"; _settingModel = false;
        OnPropertyChanged(nameof(PreviewTexturePath)); OnPropertyChanged(nameof(PreviewModel)); OnPropertyChanged(nameof(CanChooseSlim));
    }
    partial void OnHasImportedSkinChanged(bool value) { OnPropertyChanged(nameof(CanApplySkin)); OnPropertyChanged(nameof(ShowApplySkin)); }
    partial void OnIsSkinOperationChanged(bool value) { OnPropertyChanged(nameof(CanApplySkin)); OnPropertyChanged(nameof(CanImportSkin)); }
    partial void OnSkinModelKeyChanged(string value)
    {
        OnPropertyChanged(nameof(PreviewModel));
        if (_settingModel || PreviewSkin is null) return;
        HasImportedSkin = true;
    }
    partial void OnShowOfflineModalChanged(bool value) => OnPropertyChanged(nameof(PreviewPaused));
    partial void OnShowMicrosoftModalChanged(bool value) => OnPropertyChanged(nameof(PreviewPaused));
    partial void OnShowLittleSkinModalChanged(bool value) => OnPropertyChanged(nameof(PreviewPaused));
    [RelayCommand] private void SelectSkinModel(string key) { if (ShowSkinEditor && (key == "Classic" || key == "Slim" && CanChooseSlim)) SkinModelKey = key; }
    public void AccountChanged(AccountProfile? account)
    {
        _previewCancellation?.Cancel(); _skinOperationCancellation?.Cancel();
        HasImportedSkin = false; SkinStatus = ""; PreviewSkin = null;
        foreach (var property in new[] { nameof(HasAccount), nameof(ShowSkinEditor), nameof(CanImportSkin), nameof(CanApplySkin), nameof(ShowApplySkin), nameof(ApplySkinLabel), nameof(IsLittleSkinAccount) }) OnPropertyChanged(property);
        var cts = new CancellationTokenSource(); _previewCancellation = cts;
        _previewTask = LoadSkinAsync(account, false, cts);
    }
    private async Task LoadSkinAsync(AccountProfile? account, bool force, CancellationTokenSource cts)
    {
        IsSkinLoading = true;
        try
        {
            var cached = await Main.Skins.GetCachedAsync(account, cts.Token);
            cts.Token.ThrowIfCancellationRequested(); PreviewSkin = cached;
            if (account is not null)
            {
                var refreshed = await Main.Skins.RefreshAsync(account, force, cts.Token);
                if (!cts.IsCancellationRequested && Main.CurrentAccount?.Id == account.Id) PreviewSkin = refreshed;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested) { SkinStatus = "暂时无法刷新皮肤"; Main.Log.Write("皮肤刷新：" + ex.Message, LogLevel.Warning); }
        }
        finally
        {
            if (_previewCancellation == cts) { IsSkinLoading = false; _previewCancellation = null; }
            cts.Dispose();
        }
    }
    [RelayCommand] private async Task RefreshSkinAsync()
    {
        if (IsSkinOperation) return;
        _previewCancellation?.Cancel(); HasImportedSkin = false; SkinStatus = "";
        var cts = new CancellationTokenSource(); _previewCancellation = cts;
        _previewTask = LoadSkinAsync(Main.CurrentAccount, true, cts); await _previewTask;
    }
    private async Task SkinOperationAsync(Func<AccountProfile, CancellationToken, Task> action)
    {
        if (Main.CurrentAccount is not { } account || IsSkinOperation) return;
        var cts = new CancellationTokenSource(); _skinOperationCancellation = cts;
        IsSkinOperation = true; SkinStatus = "";
        try { await action(account, cts.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!cts.IsCancellationRequested) { SkinStatus = ex.Message; Main.Log.Write("皮肤操作：" + ex.Message, LogLevel.Warning); } }
        finally { if (_skinOperationCancellation == cts) { _skinOperationCancellation = null; IsSkinOperation = false; } cts.Dispose(); }
    }
    [RelayCommand] private async Task ImportSkinAsync()
    {
        if (!CanImportSkin) return;
        var picker = new OpenFileDialog { Title = "导入皮肤", Filter = "PNG 皮肤|*.png" };
        if (picker.ShowDialog() != true) return;
        _previewCancellation?.Cancel();
        await SkinOperationAsync(async (account, token) =>
        {
            var imported = await Main.Skins.ImportAsync(picker.FileName, token); token.ThrowIfCancellationRequested();
            if (Main.CurrentAccount?.Id != account.Id) return;
            PreviewSkin = imported; HasImportedSkin = true; SkinStatus = "";
        });
    }
    [RelayCommand] private async Task ApplySkinAsync()
    {
        if (!CanApplySkin || PreviewSkin is null) return;
        var chosen = PreviewSkin with { Model = PreviewModel };
        await SkinOperationAsync(async (account, token) =>
        {
            var applied = account.Kind == AccountKind.Microsoft
                ? await Main.Skins.UploadMicrosoftAsync(account, chosen, Main.Settings.MicrosoftClientId, token)
                : await Main.Skins.ApplyOfflineAsync(account, chosen, token);
            token.ThrowIfCancellationRequested(); if (Main.CurrentAccount?.Id != account.Id) return;
            PreviewSkin = applied; HasImportedSkin = false; SkinStatus = "皮肤已更新";
        });
    }
    [RelayCommand] private async Task ExportSkinAsync()
    {
        if (PreviewSkin is not { } skin || IsSkinOperation) return;
        var picker = new SaveFileDialog { Title = "导出皮肤", Filter = "PNG 皮肤|*.png", FileName = "skin.png" };
        if (picker.ShowDialog() != true) return;
        await SkinOperationAsync(async (_, token) => { await Main.Skins.ExportAsync(skin, picker.FileName, token); SkinStatus = "已导出"; });
    }
    [RelayCommand] private void OpenSkinWebsite() => Process.Start(new ProcessStartInfo("https://littleskin.cn") { UseShellExecute = true });
    [RelayCommand] private void CancelSkinOperation() => _skinOperationCancellation?.Cancel();
    private async Task StopSkinsAsync()
    {
        _previewCancellation?.Cancel(); _skinOperationCancellation?.Cancel();
        foreach (var task in new[] { _previewTask, ImportSkinCommand.ExecutionTask, ApplySkinCommand.ExecutionTask, ExportSkinCommand.ExecutionTask, RefreshSkinCommand.ExecutionTask })
            if (task is not null) await task;
        if (StopPreviewAsync is { } stop) await stop();
        await Main.Skins.CancelAndWaitAsync();
    }
}
