using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
namespace Launcher.App.ViewModels;
public sealed partial class SettingsViewModel
{
    [ObservableProperty] private bool _preferGameMirror;
    [ObservableProperty] private bool _preferContentMirror;
    [ObservableProperty] private string _curseForgeKeyStatus = "";
    public string PendingCurseForgeKey { private get; set; } = "";
    partial void OnPreferGameMirrorChanged(bool value) { Main.Settings.PreferGameMirror = value; ScheduleSave(); }
    partial void OnPreferContentMirrorChanged(bool value) { Main.Settings.PreferContentMirror = value; ScheduleSave(); }
    [RelayCommand] private void SaveCurseForgeKey()
    {
        var key = PendingCurseForgeKey.Trim();
        if (key.Length == 0) return;
        Main.Secrets.WriteJson("curseforge-key", key); Main.Log.RegisterSecret(key); PendingCurseForgeKey = ""; CurseForgeKeyStatus = "已保存";
    }
    [RelayCommand] private void RemoveCurseForgeKey() { Main.Secrets.Delete("curseforge-key"); CurseForgeKeyStatus = "未配置"; }
}
