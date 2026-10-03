using System.Text.Json.Serialization;

namespace Launcher.Core;

public enum AccountKind { Offline, Microsoft, LittleSkin }
public enum DanmakuMode { Off, Selected, All }
public enum DanmakuStyle { Glass, Ink, Rainbow }
public enum DanmakuMatchKind { Exact, Regex }
[Flags] public enum DanmakuBlockedPresets { None = 0, Log4j = 1, AuthlibInjector = 2, Warn = 4, Error = 8 }
public sealed record DanmakuBlockRule(bool Enabled, DanmakuMatchKind Kind, string Pattern);
public enum IsolationMode { Auto, Shared, Isolated }
public enum LogLevel { Info, Warning, Error }
public enum LaunchState { Idle, Preparing, Installing, Starting, Running, Exited, Failed }
public enum UiAnimationMode { Off, Performance, Calm }
public enum MemoryAllocationMode { Smart, Manual }
public enum VersionContentKind { Mod, Save, ResourcePack, ShaderPack }
public sealed record VersionContentItem(string Path, string Name, VersionContentKind Kind, bool Enabled, bool IsDirectory, long Size, DateTime Modified)
{
    public string Detail => Kind == VersionContentKind.Save ? Modified.ToString("yyyy-MM-dd HH:mm") : $"{Size / 1048576d:F1} MB · {(Enabled ? "已启用" : "已禁用")}";
    public string ToggleLabel => Enabled ? "禁用" : "启用";
}
public enum JavaDownloadStage { Preparing, Downloading, Verifying, Extracting, Completed }
public sealed record DownloadConnectionProgress(int Id, long DownloadedBytes, long? TotalBytes, double BytesPerSecond, string State, string? StableId = null, string? FileName = null)
{
    public double Percent => TotalBytes is > 0 ? Math.Clamp(100d * DownloadedBytes / TotalBytes.Value, 0, 100) : 0;
    public string Label => $"连接 {Id}";
    public string Detail => (TotalBytes is > 0 ? $"{Percent:F0}% · " : "") + $"{DownloadedBytes / 1048576d:F1} MB · {BytesPerSecond / 1048576d:F2} MB/s · {State}";
}
public sealed record JavaDownloadProgress(JavaDownloadStage Stage, long DownloadedBytes, long? TotalBytes, double BytesPerSecond, IReadOnlyList<DownloadConnectionProgress> Connections)
{
    public double Percent => TotalBytes is > 0 ? Math.Clamp(100d * DownloadedBytes / TotalBytes.Value, 0, 100) : 0;
}

public sealed record AccountProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public AccountKind Kind { get; init; }
    public string Name { get; init; } = "";
    public string Uuid { get; init; } = "";
    public string LoginIdentifier { get; init; } = "";
    [JsonIgnore] public string KindLabel => Kind switch { AccountKind.Offline => "离线", AccountKind.Microsoft => "Microsoft", _ => "LittleSkin" };
    [JsonIgnore] public string Initial => Name.Length == 0 ? "?" : Name[..1].ToUpperInvariant();
    public override string ToString() => $"{Name} · {KindLabel}";
}

public sealed record VersionInfo(string Id, string Root, string Loader, int? RequiredJava, string? Error = null)
{
    public string GameName { get; init; } = Id;
    public string MinecraftVersion { get; init; } = "";
    public string LoaderVersion { get; init; } = "";
    public string DownloadTargetTag => IsValid ? $"{Loader} · {(string.IsNullOrWhiteSpace(MinecraftVersion) ? "版本未知" : MinecraftVersion)}" : "损坏";
    public string? ParentId { get; init; }
    public string VersionType { get; init; } = "release";
    public string TagLabel => IsValid ? Loader : "损坏";
    public bool IsValid => Error is null;
    public string Detail => Error ?? $"{Loader}  ·  {(RequiredJava is int j ? $"Java {j}" : "Java 待指定")}";
    public override string ToString() => GameName;
}

public sealed record JavaRuntimeInfo(string Path, int Major, Version Version, string Architecture, string Vendor)
{
    public string Label => $"Java {Major} · {Architecture} · {Vendor}";
    public override string ToString() => Label;
}

public sealed record LaunchRequest(AccountProfile Account, VersionInfo Version, JavaRuntimeInfo Java, int MemoryMb, IsolationMode Isolation, string? ResolvedGameDirectory = null);
public sealed record LauncherLogEvent(DateTimeOffset Time, LogLevel Level, string Message, bool Featured = false)
{
    public string Text => $"{Time:HH:mm:ss}  [{Level}]  {Message}";
}
public sealed record DeviceLoginInfo(string Code, string Url, DateTimeOffset Expires);
public sealed record LittleSkinProfile(string Name, string Id)
{
    public override string ToString() => Name;
}
public sealed record LittleSkinLogin(string AccessToken, string ClientToken, IReadOnlyList<LittleSkinProfile> Profiles)
{
    public LittleSkinProfile? SelectedProfile { get; init; }
}
public sealed record LittleSkinTokens(string AccessToken, string ClientToken)
{
    public DateTimeOffset LastRenewedUtc { get; init; }
    public string? ProfileName { get; init; }
}
public sealed class AccountLoginRequiredException(string message) : InvalidOperationException(message);

public sealed class LauncherSettings
{
    public string AiModelId { get; set; } = "";
    public string AiReasoningEffort { get; set; } = "high";
    public bool AiSetupCompleted { get; set; }
    public bool PreferGameMirror { get; set; } = true;
    public bool PreferContentMirror { get; set; } = true;
    public string GameRoot { get; set; } = AppPaths.DefaultGameRoot;
    public string? SelectedAccountId { get; set; }
    public string? SelectedVersionId { get; set; }
    public bool SidebarExpanded { get; set; } = true;
    public string Theme { get; set; } = "System";
    public UiAnimationMode UiAnimationMode { get; set; } = UiAnimationMode.Calm;
    public DanmakuMode Danmaku { get; set; } = DanmakuMode.Selected;
    public DanmakuStyle DanmakuStyle { get; set; } = DanmakuStyle.Glass;
    public DanmakuBlockedPresets DanmakuBlockedPresets { get; set; }
    public List<DanmakuBlockRule> DanmakuBlockRules { get; set; } = new();
    public int MemoryMb { get; set; } = 4096;
    public MemoryAllocationMode MemoryAllocationMode { get; set; } = MemoryAllocationMode.Smart;
    public string JavaDownloadDirectory { get; set; } = AppPaths.Runtime;
    public int WindowTransparencyPercent { get; set; }
    public string MicrosoftClientId { get; set; } = "e1e383f9-59d9-4aa2-bf5e-73fe83b15ba0";
    public Dictionary<string, string> JavaOverrides { get; set; } = new();
    public Dictionary<string, IsolationMode> IsolationOverrides { get; set; } = new();
}
