namespace Launcher.Core;

public enum ContentPlatform { Modrinth, CurseForge }
public enum CatalogKind { Game, Mod, ResourcePack, Shader, Modpack }
public enum DownloadTaskState { Queued, Checking, Java, Downloading, Verifying, Installing, Committing, Completed, Cancelled, Failed }
public sealed record DownloadCatalogItem(string Id, string Name, string Description, CatalogKind Kind, ContentPlatform Platform = ContentPlatform.Modrinth, string? IconUrl = null, string? WebUrl = null)
{
    public string? ChineseName { get; init; }
    public string DisplayName => string.IsNullOrWhiteSpace(ChineseName) || Name.Contains(ChineseName, StringComparison.OrdinalIgnoreCase) ? Name : $"{ChineseName}({Name})";
    public override string ToString() => DisplayName;
}
public sealed record GameCatalogVersion(string Id, string Type, string Url, string Sha1)
{
    public override string ToString() => Id;
}
public sealed record LoaderCatalogVersion(string Version, string MinecraftVersion, string Loader)
{
    public override string ToString() => Version;
}
public sealed record OptiFineCatalogVersion(string MinecraftVersion, string Type, string Patch, string FileName, string? ForgeVersion)
{
    public string Edition => Type + "_" + Patch;
    public override string ToString() => Edition;
}
public sealed record ContentDependency(string? VersionId, string? ProjectId, string Kind);
public sealed record ContentFile(string Name, string Url, long Size, string? Sha1, string? Sha512 = null);
public sealed record ContentRelease(string Id, string ProjectId, string Name, ContentPlatform Platform, CatalogKind Kind, IReadOnlyList<string> GameVersions, IReadOnlyList<string> Loaders, bool ClientSupported, IReadOnlyList<ContentFile> Files, IReadOnlyList<ContentDependency> Dependencies)
{
    public string VersionLabel => string.Join(" · ", new[] { string.Join(" / ", Loaders), string.Join(", ", GameVersions.Take(4)) + (GameVersions.Count > 4 ? "…" : "") }.Where(s => s.Length > 0));
    public override string ToString() => Name;
}
public sealed record CompatibilityIssue(string Code, string Message);
public sealed record SourcePolicy(bool GameMirror, bool ContentMirror, string? CurseForgeKey = null);
public sealed record InstallPlan(string Name, string Root, GameCatalogVersion? Game, LoaderCatalogVersion? Loader, OptiFineCatalogVersion? OptiFine, VersionInfo? Target, string? TargetDirectory, ContentRelease? Content, SourcePolicy Sources, string JavaDirectory, IReadOnlyList<JavaRuntimeInfo> Javas, int? TargetJavaMajor = null, IReadOnlyList<ContentRelease>? DependencyVersions = null, string? LocalArchive = null)
{
    public string Directory => TargetDirectory ?? System.IO.Path.Combine(Root, "versions", Name);
}
public sealed record FileDownloadProgress(long DownloadedBytes, long? TotalBytes, double BytesPerSecond, IReadOnlyList<DownloadConnectionProgress> Connections)
{
    public double Percent => TotalBytes is > 0 ? Math.Clamp(DownloadedBytes * 100d / TotalBytes.Value, 0, 100) : 0;
}
/// <summary>Completed workflow milestones, independent of the installer's changing file batches.</summary>
public sealed record InstallationWorkProgress(int CompletedSteps, int TotalSteps, string CurrentStep)
{
    public double Percent => TotalSteps > 0 ? Math.Clamp(CompletedSteps * 100d / TotalSteps, 0, 100) : 0;
    public static int StepsFor(InstallPlan plan) => plan.Content?.Kind == CatalogKind.Modpack || plan.LocalArchive is not null ? 7 : plan.Game is not null ? 4 : 3;
}
internal sealed class InstallationProgressTracker(int total, Action<InstallationWorkProgress>? report)
{
    private int _completed;
    public void Start(string step) => report?.Invoke(new(_completed, total, step));
    public void Complete(string next) { _completed = Math.Min(total, _completed + 1); Start(next); }
}
public sealed record DownloadTaskInfo(Guid Id, string Name, DownloadTaskState State, string Message, FileDownloadProgress? Progress = null, string? Error = null, VersionInfo? InstalledVersion = null)
{
    public InstallationWorkProgress? OverallProgress { get; init; }
    public bool IsActive => State is not (DownloadTaskState.Completed or DownloadTaskState.Failed or DownloadTaskState.Cancelled);
    public bool CanRetry => State is DownloadTaskState.Failed or DownloadTaskState.Cancelled;
    public bool CanRemove => !IsActive;
    public bool IsIndeterminate => IsActive && OverallProgress is null;
    public double Percent => State == DownloadTaskState.Completed ? 100 : OverallProgress?.Percent ?? 0;
    public string OverallCaption => OverallProgress is { } p ? $"整体阶段进度 {Percent:F0}% · {p.CompletedSteps}/{p.TotalSteps}" : State == DownloadTaskState.Completed ? "整体进度 100%" : "整体进度";
    public bool HasStageProgress => State == DownloadTaskState.Downloading && Progress?.TotalBytes is > 0;
    public double StagePercent => Progress?.Percent ?? 0;
    public bool StageIndeterminate => IsActive && !HasStageProgress;
    public string StageLabel => State switch { DownloadTaskState.Queued => "等待中", DownloadTaskState.Checking => "准备", DownloadTaskState.Java => "准备 Java", DownloadTaskState.Downloading => "当前下载批次", DownloadTaskState.Verifying => "校验", DownloadTaskState.Installing => "安装处理", DownloadTaskState.Committing => "提交", DownloadTaskState.Completed => "已完成", DownloadTaskState.Cancelled => "已取消", _ => "失败" };
    public string StageDetail => HasStageProgress && Progress is { } p ? $"{p.Percent:F0}% · {p.DownloadedBytes / 1048576d:F1}/{p.TotalBytes / 1048576d:F1} MB · {p.BytesPerSecond / 1048576d:F2} MB/s" : Message;
    public string Detail => OverallProgress is { } p && IsActive ? p.CurrentStep + (State == DownloadTaskState.Downloading && Progress is { } bytes ? $" · {bytes.BytesPerSecond / 1048576d:F2} MB/s" : "") : Message;
}
public sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
