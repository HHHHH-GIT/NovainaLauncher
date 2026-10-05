using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Launcher.AI;
using Launcher.App.ViewModels;
using Launcher.Core;
using Microsoft.Win32;

namespace Launcher.App.Services;

/// <summary>Single business facade. Tool handles refer only to objects returned by launcher queries.</summary>
public sealed class LauncherOperations(MainViewModel main) : ILauncherOperations
{
    private readonly DownloadCatalogService _catalog = new();
    private readonly ModReleaseCache _releaseCache = new();
    private readonly ConcurrentDictionary<string, object> _handles = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _writes = new(StringComparer.OrdinalIgnoreCase);
    private Task<T> UI<T>(Func<T> action) => main.UiDispatcher.InvokeAsync(action).Task;
    private Task UI(Action action) => main.UiDispatcher.InvokeAsync(action).Task;
    private Task<T> UIAsync<T>(Func<Task<T>> action) => main.UiDispatcher.InvokeAsync(action).Task.Unwrap();
    private Task UIAsync(Func<Task> action) => main.UiDispatcher.InvokeAsync(action).Task.Unwrap();
    public bool IsDirectoryBusy(string directory) => _writes.ContainsKey(Path.GetFullPath(directory));
    public void ResetHandles() => _handles.Clear();
    public IReadOnlyList<(string Id, JavaRuntimeInfo Java)> GetDevelopmentJavas() => main.UiDispatcher.CheckAccess()
        ? main.SettingsVM.InstalledJavas.Select(j => (Handle("java", j.Path, j), j)).ToArray() : main.UiDispatcher.Invoke(GetDevelopmentJavas);
    public IReadOnlyList<AgentReference> GetReferences() => main.UiDispatcher.CheckAccess() ? ReferencesOnUi() : main.UiDispatcher.Invoke(ReferencesOnUi);
    private IReadOnlyList<AgentReference> ReferencesOnUi() => main.VersionsVM.Versions
        .Select(v => new AgentReference("game", Local(v), v.GameName, $"Minecraft {v.MinecraftVersion} · {v.Loader} {v.LoaderVersion}".Trim()))
        .Concat(main.SettingsVM.InstalledJavas.Select(j => new AgentReference("java", Handle("java", j.Path, j), $"Java {j.Major} · {j.Vendor}", $"{j.Version} · {j.Architecture}")))
        .Concat(main.AccountsVM.Accounts.Select(a => new AgentReference("account", Handle("account", a.Id, a), a.Name, a.KindLabel))).ToArray();
    private string Handle<T>(string prefix, string key, T value) where T : notnull
    {
        var id = prefix + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..20].ToLowerInvariant();
        _handles[id] = value; return id;
    }
    private T Resolve<T>(string id) => _handles.TryGetValue(id, out var obj) && obj is T typed ? typed : throw new InvalidOperationException("ID 已失效或未查询，请先查询真实列表");
    private static string S(JsonObject a, string key, string fallback = "") => a[key]?.GetValue<string>() ?? fallback;
    private static int N(JsonObject a, string key, int fallback = 0) => a[key]?.GetValue<int>() ?? fallback;
    private string Local(VersionInfo version) => Handle("local", AppPaths.VersionKey(version), version);
    private string DirectoryFor(VersionInfo v) => AgentPathGuard.Within(v.Root, VersionService.ResolveGameDirectory(v, main.Settings));
    private SourcePolicy Sources => main.DownloadsVM.SourcePolicy();
    private object VersionData(VersionInfo v) => new { id = Local(v), name = v.GameName, minecraft = v.MinecraftVersion, loader = v.Loader, loaderVersion = v.LoaderVersion, java = v.RequiredJava, valid = v.IsValid, current = main.CurrentVersion?.Id == v.Id && main.CurrentVersion.Root == v.Root };
    public Guid Enqueue(InstallPlan plan, Guid? group = null)
    {
        AgentPathGuard.Within(plan.Root, plan.Directory);
        if (main.SettingsVM.IsChangingData) throw new InvalidOperationException("正在迁移数据");
        if (plan.Content?.Kind != CatalogKind.Mod) VersionManagementService.ValidateName(plan.Name);
        return main.DownloadsVM.Queue.Enqueue(plan with { AutoPrepareJava = main.Settings.AutoPrepareJava }, group);
    }
    public InstallPlan GamePlan(string name, GameCatalogVersion? game, LoaderCatalogVersion? loader, OptiFineCatalogVersion? optifine) =>
        new(name.Trim(), main.Settings.GameRoot, game, loader, optifine, null, null, null, Sources, main.Settings.JavaDownloadDirectory, main.SettingsVM.InstalledJavas.ToArray());
    public InstallPlan ContentPlan(string name, ContentRelease file, VersionInfo? target) => new(name.Trim(), target?.Root ?? main.Settings.GameRoot, null, null, null, target,
        target is null ? null : DirectoryFor(target), file, Sources, main.Settings.JavaDownloadDirectory, main.SettingsVM.InstalledJavas.ToArray());

    public async Task<AgentApproval> DescribeSensitiveAsync(string name, JsonObject args, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (name == "launch_game") { var v = S(args, "game_id") is { Length: > 0 } id ? Resolve<VersionInfo>(id) : await UI(() => main.CurrentVersion ?? throw new InvalidOperationException("请先选择游戏")); return new("启动测试游戏", $"使用当前账户启动“{v.GameName}”（{v.MinecraftVersion} · {v.Loader}）？默认工作台只编译；确认后才会打开游戏。"); }
        if (name == "delete_game") { var v = Resolve<VersionInfo>(S(args, "game_id")); AgentPathGuard.Within(v.Root, Path.Combine(v.Root, "versions", v.Id)); return new("删除游戏", $"将“{v.GameName}”及独立目录移入回收站？"); }
        var item = Resolve<(VersionInfo Version, VersionContentItem Item)>(S(args, "item_id"));
        AgentPathGuard.Within(DirectoryFor(item.Version), item.Item.Path);
        return await Task.FromResult(new AgentApproval("删除内容", $"将“{item.Item.Name}”移入回收站？目标：{item.Version.GameName}"));
    }
    public Task CancelGroupAsync(Guid groupId) => main.DownloadsVM.Queue.CancelGroupAsync(groupId);
    public async Task<ToolResult> ExecuteAsync(string name, JsonObject a, AgentExecutionContext context)
    {
        var token = context.Cancellation; token.ThrowIfCancellationRequested();
        try
        {
            switch (name)
            {
                case "get_launcher_state":
                    return await UI(() => ToolResult.Ok("已读取启动器状态", new {
                        games = main.VersionsVM.Versions.Take(250).Select(VersionData).ToArray(),
                        accounts = main.AccountsVM.Accounts.Select(x => new { id = Handle("account", x.Id, x), name = x.Name, type = x.KindLabel, current = main.CurrentAccount?.Id == x.Id }).ToArray(),
                        javas = main.SettingsVM.InstalledJavas.Select(x => new { id = Handle("java", x.Path, x), major = x.Major, version = x.Version.ToString(), architecture = x.Architecture, jdk = File.Exists(Path.Combine(Path.GetDirectoryName(x.Path)!, "javac.exe")) }).ToArray(),
                        memoryMb = main.SettingsVM.MemoryMb, availableMemoryMb = main.SettingsVM.AvailableMemoryMb, smartMemory = main.SettingsVM.SmartMemory,
                        launchState = main.LaunchState.ToString(), busy = main.DownloadsVM.Queue.IsBusy }));
                case "list_game_versions":
                {
                    using var source = new DownloadSources(Sources, token);
                    var games = await _catalog.GamesAsync(source, token);
                    var list = games.Where(x => (S(a, "type", "release") == "all" || x.Type == S(a, "type", "release")) && x.Id.Contains(S(a, "query"), StringComparison.OrdinalIgnoreCase)).ToArray();
                    return ToolResult.Ok("已获取游戏版本", new { total = list.Length, versions = list.Take(100).Select(x => new { id = Handle("mc", x.Id, x), version = x.Id, type = x.Type }).ToArray() });
                }
                case "list_loaders":
                {
                    var game = Resolve<GameCatalogVersion>(S(a, "game_id")); using var source = new DownloadSources(Sources, token);
                    var loaders = await _catalog.LoadersAsync(game.Id, S(a, "loader"), source, token);
                    return ToolResult.Ok("已获取加载器", loaders.Take(100).Select(x => new { id = Handle("loader", x.Loader + "|" + x.MinecraftVersion + "|" + x.Version, x), loader = x.Loader, version = x.Version, minecraft = x.MinecraftVersion }).ToArray());
                }
                case "list_optifine":
                {
                    var game = Resolve<GameCatalogVersion>(S(a, "game_id")); using var source = new DownloadSources(Sources, token);
                    var list = await _catalog.OptiFineAsync(game.Id, source, token);
                    return ToolResult.Ok("已获取 OptiFine", list.Take(100).Select(x => new { id = Handle("of", x.FileName, x), minecraft = x.MinecraftVersion, version = x.Edition, forge = x.ForgeVersion }).ToArray());
                }
                case "search_projects":
                {
                    using var source = new DownloadSources(Sources, token);
                    var projects = await _catalog.SearchAsync(Enum.Parse<CatalogKind>(S(a, "kind")), Enum.Parse<ContentPlatform>(S(a, "platform", "Modrinth")), S(a, "query"), null, source, token, N(a, "offset"));
                    return ToolResult.Ok("已找到项目", projects.Take(30).Select(x => new { id = Handle("project", x.Platform + "|" + x.Id, x), name = x.DisplayName, kind = x.Kind.ToString(), description = ShortText(x.Description) }).ToArray());
                }
                case "list_project_files":
                {
                    var project = Resolve<DownloadCatalogItem>(S(a, "project_id"));
                    var cached = await _releaseCache.ReadAsync(project, token); IReadOnlyList<ContentRelease> files;
                    if (cached?.IsFresh == true) files = cached.Releases;
                    else { using var source = new DownloadSources(Sources, token); files = await _catalog.ReleasesAsync(project, null, source, token); await _releaseCache.SaveAsync(project, new(files.ToArray(), new ModReleaseIndex(files, token), DateTime.UtcNow), token); }
                    var filtered = files.Where(x => (S(a, "game_version") == "" || x.GameVersions.Contains(S(a, "game_version"))) && (S(a, "loader") == "" || x.Loaders.Contains(S(a, "loader"), StringComparer.OrdinalIgnoreCase))).ToArray();
                    return ToolResult.Ok("已读取文件版本", new { total = filtered.Length, files = filtered.Skip(N(a, "offset")).Take(60).Select(x => new { id = Handle("release", x.Platform + "|" + x.Id, x), name = x.Name, minecraft = x.GameVersions, loaders = x.Loaders, client = x.ClientSupported }).ToArray() });
                }
                case "install_game":
                {
                    var plan = await UI(() => GamePlan(S(a, "name"), Resolve<GameCatalogVersion>(S(a, "game_id")), S(a, "loader_id") == "" ? null : Resolve<LoaderCatalogVersion>(S(a, "loader_id")), S(a, "optifine_id") == "" ? null : Resolve<OptiFineCatalogVersion>(S(a, "optifine_id"))));
                    return await InstallAsync(plan, context);
                }
                case "install_content":
                {
                    var file = Resolve<ContentRelease>(S(a, "release_id"));
                    if (file.Kind != CatalogKind.Modpack && S(a, "game_id") == "") return ToolResult.Fail("请选择目标游戏");
                    if (file.Kind == CatalogKind.Modpack && S(a, "name") == "") return ToolResult.Fail("请指定整合包实例名称");
                    var plan = await UI(() => ContentPlan(S(a, "name", file.Name), file, file.Kind == CatalogKind.Modpack ? null : Resolve<VersionInfo>(S(a, "game_id"))));
                    return await InstallAsync(plan, context);
                }
                case "import_modpack":
                {
                    var path = context.Interaction.FullAccess && a["path"] is { } importPath ? Path.GetFullPath(importPath.ToString()) : await UI(() => { var picker = new OpenFileDialog { Title = "选择整合包", Filter = "整合包 (*.zip;*.mrpack)|*.zip;*.mrpack" }; return picker.ShowDialog() == true ? picker.FileName : null; });
                    if (path is null) return ToolResult.Fail("未选择整合包");
                    if (!context.Interaction.FullAccess) AgentPathGuard.Within(Path.GetDirectoryName(path)!, path);
                    var archiveName = await Task.Run(() => ModpackService.InspectArchiveAsync(path, token), token);
                    var plan = await UI(() => new InstallPlan(S(a, "name", archiveName), main.Settings.GameRoot, null, null, null, null, null, null, Sources, main.Settings.JavaDownloadDirectory, main.SettingsVM.InstalledJavas.ToArray(), LocalArchive: path));
                    return await InstallAsync(plan, context);
                }
                case "export_modpack":
                {
                    var version = Resolve<VersionInfo>(S(a, "game_id")); var dir = DirectoryFor(version);
                    var path = context.Interaction.FullAccess && a["path"] is { } exportPath ? Path.GetFullPath(exportPath.ToString()) : await UI(() => { var p = new SaveFileDialog { Title = "导出整合包", FileName = version.GameName + ".mrpack", Filter = "整合包 (*.mrpack)|*.mrpack", OverwritePrompt = true }; return p.ShowDialog() == true ? p.FileName : null; });
                    if (path is null) return ToolResult.Fail("未选择导出位置");
                    if (!context.Interaction.FullAccess) AgentPathGuard.Within(Path.GetDirectoryName(path)!, path);
                    await WriteAsync(dir, async () => await new ModpackService().ExportAsync(version, dir, path, new InlineProgress<string>(m => context.Emit(new(AgentUiEventKind.Operation, "正在导出整合包", m, "export-" + context.GroupId))), token));
                    return ToolResult.Ok("整合包已导出", new { game = version.GameName, file = Path.GetFileName(path) });
                }
                case "select_game": return await UI(() => { var v = Resolve<VersionInfo>(S(a, "game_id")); if (!v.IsValid || !File.Exists(Path.Combine(v.Root, "versions", v.Id, v.Id + ".json"))) return ToolResult.Fail("该游戏不可用，请刷新本地列表"); main.CurrentVersion = v; return ToolResult.Ok("已选择游戏", VersionData(v)); });
                case "select_account": return await UI(() => { var account = Resolve<AccountProfile>(S(a, "account_id")); if (!main.AccountsVM.Accounts.Any(x => x.Id == account.Id)) return ToolResult.Fail("账户已移除，请查询当前列表"); main.CurrentAccount = account; return ToolResult.Ok("已选择账户", new { name = main.CurrentAccount.Name }); });
                case "retry_account_login":
                {
                    var account = await UI(() => Resolve<AccountProfile>(S(a, "account_id")));
                    context.Emit(new(AgentUiEventKind.Operation, "正在恢复账户登录", account.Name, "account-login-" + account.Id));
                    try
                    {
                        var session = await UIAsync(() => main.AccountsVM.RetryAccountLoginAsync(account, token));
                        return ToolResult.Ok("账户登录已验证", new { account_id = S(a, "account_id"), name = session.Username, type = account.KindLabel, status = "authenticated" });
                    }
                    catch (AccountLoginRequiredException e)
                    {
                        return new ToolResult(false, e.Message, new { account_id = S(a, "account_id"), status = "reauthentication_required", next_tool = "open_account_login" });
                    }
                }
                case "open_account_login":
                    await UI(() => { var account = string.IsNullOrEmpty(S(a, "account_id")) ? null : Resolve<AccountProfile>(S(a, "account_id")); main.RequestAccountLogin(account); });
                    try { await main.WaitAccountLoginAsync(token); } finally { await UI(() => main.CompleteAccountLogin()); }
                    var selectedAccount = await UI(() => main.CurrentAccount);
                    if (selectedAccount is null) return ToolResult.Fail("尚未选择账户");
                    var verified = await UIAsync(() => main.AccountsVM.RetryAccountLoginAsync(selectedAccount, token));
                    return ToolResult.Ok("账户登录已验证", new { account_id = Handle("account", selectedAccount.Id, selectedAccount), name = verified.Username, status = "authenticated" });
                case "list_content":
                {
                    var v = Resolve<VersionInfo>(S(a, "game_id")); var dir = DirectoryFor(v);
                    var items = await new VersionContentService().ScanAsync(dir, Enum.Parse<VersionContentKind>(S(a, "kind")), token);
                    return ToolResult.Ok("已读取游戏内容", items.Take(300).Select(x => new { id = Handle("item", AppPaths.VersionKey(v) + "|" + x.Path, (v, x)), name = x.Name, enabled = x.Enabled, size = x.Size }).ToArray());
                }
                case "toggle_content": case "delete_content":
                {
                    var (v, item) = Resolve<(VersionInfo, VersionContentItem)>(S(a, "item_id")); var dir = DirectoryFor(v); AgentPathGuard.Within(dir, item.Path);
                    await WriteAsync(dir, async () => { var service = new VersionContentService(); if (name == "delete_content") await service.DeleteAsync(dir, item, token); else if (item.Enabled != a["enabled"]!.GetValue<bool>()) await service.ToggleAsync(dir, item, token); });
                    _handles.TryRemove(S(a, "item_id"), out _); await UIAsync(() => main.SettingsVM.RefreshMemoryAsync()); await UI(() => main.ContentVM.InvalidateCache());
                    return ToolResult.Ok(name == "delete_content" ? "内容已移入回收站" : "内容状态已更新", new { name = item.Name });
                }
                case "import_content":
                {
                    var v = Resolve<VersionInfo>(S(a, "game_id")); var dir = DirectoryFor(v); var kind = Enum.Parse<VersionContentKind>(S(a, "kind"));
                    var path = await UI(() => { if (a["folder"]?.GetValue<bool>() == true && kind != VersionContentKind.Mod) { var p = new OpenFolderDialog { Title = "选择导入文件夹" }; return p.ShowDialog() == true ? p.FolderName : null; } var f = new OpenFileDialog { Title = "选择导入文件", Filter = kind == VersionContentKind.Mod ? "模组 (*.jar)|*.jar" : "ZIP 文件 (*.zip)|*.zip" }; return f.ShowDialog() == true ? f.FileName : null; });
                    if (path is null) return ToolResult.Fail("未选择导入文件"); AgentPathGuard.Within(Path.GetDirectoryName(path)!, path);
                    await WriteAsync(dir, () => new VersionContentService().ImportAsync(dir, kind, path, token));
                    await UI(() => main.ContentVM.InvalidateCache()); await UIAsync(() => main.SettingsVM.RefreshMemoryAsync());
                    return ToolResult.Ok("游戏内容已导入", new { name = Path.GetFileName(path) });
                }
                case "export_save":
                {
                    var (v, item) = Resolve<(VersionInfo, VersionContentItem)>(S(a, "item_id")); if (item.Kind != VersionContentKind.Save) return ToolResult.Fail("该项目不是存档");
                    var dir = DirectoryFor(v); AgentPathGuard.Within(dir, item.Path);
                    var path = await UI(() => { var p = new SaveFileDialog { Title = "导出存档", FileName = item.Name + ".zip", Filter = "存档 ZIP (*.zip)|*.zip", OverwritePrompt = true }; return p.ShowDialog() == true ? p.FileName : null; });
                    if (path is null) return ToolResult.Fail("未选择导出位置"); AgentPathGuard.Within(Path.GetDirectoryName(path)!, path);
                    await WriteAsync(dir, () => new VersionContentService().ExportSaveAsync(dir, item, path, token));
                    return ToolResult.Ok("存档已导出", new { name = item.Name });
                }
                case "delete_game": case "rename_game":
                {
                    var v = Resolve<VersionInfo>(S(a, "game_id")); var dir = DirectoryFor(v);
                    AgentPathGuard.Within(v.Root, Path.Combine(v.Root, "versions", v.Id));
                    await WriteAsync(dir, async () => { var service = new VersionManagementService(); if (name == "delete_game") await service.DeleteAsync(v, token); else { var next = await service.RenameAsync(v, S(a, "name"), token); await UI(() => { VersionManagementService.MoveSettings(main.Settings, v, next); if (main.CurrentVersion?.Id == v.Id) main.CurrentVersion = next; main.SettingsStore.Save(main.Settings); }); } });
                    _handles.TryRemove(S(a, "game_id"), out _); await UIAsync(() => main.VersionsVM.ScanVersionsAsync());
                    return ToolResult.Ok(name == "delete_game" ? "游戏已移入回收站" : "游戏已改名");
                }
                case "configure_memory":
                    return await UIAsync(async () => { await main.SettingsVM.RefreshMemoryAsync(); token.ThrowIfCancellationRequested(); if (S(a, "mode") == "manual") { if (N(a, "mb") < 256 || N(a, "mb") > main.SettingsVM.AvailableMemoryMb) return ToolResult.Fail("内存超出当前可用范围"); main.SettingsVM.SmartMemory = false; main.SettingsVM.MemoryMb = N(a, "mb"); } else { main.SettingsVM.SmartMemory = true; await main.SettingsVM.RefreshMemoryAsync(); } main.SettingsStore.Save(main.Settings); return ToolResult.Ok("内存已配置", new { mb = main.SettingsVM.MemoryMb, smart = main.SettingsVM.SmartMemory }); });
                case "configure_java":
                    return await UI(() => { var v = Resolve<VersionInfo>(S(a, "game_id")); var java = Resolve<JavaRuntimeInfo>(S(a, "java_id")); if (v.RequiredJava is int required && required != java.Major) return ToolResult.Fail("Java 主版本不匹配"); main.Settings.JavaOverrides[AppPaths.VersionKey(v)] = java.Path; main.SettingsStore.Save(main.Settings); main.AutoSelectJava(); return ToolResult.Ok("Java 已配置", new { major = java.Major }); });
                case "download_java":
                {
                    var major = N(a, "major"); if (major is not (8 or 17 or 21 or 25)) return ToolResult.Fail("不支持的 Java 主版本");
                    var taskId = Guid.NewGuid(); var eventId = "java-" + taskId; DownloadTaskInfo? last = null;
                    void EmitJava(DownloadTaskInfo state) { last = state; context.Emit(new(AgentUiEventKind.Operation, state.Name, state.Detail, eventId, state.Percent) { TaskProgress = state }); }
                    var report = new InlineProgress<JavaDownloadProgress>(p =>
                    {
                        var state = p.Stage switch { JavaDownloadStage.Preparing => DownloadTaskState.Checking, JavaDownloadStage.Downloading => DownloadTaskState.Downloading, JavaDownloadStage.Verifying => DownloadTaskState.Verifying, JavaDownloadStage.Extracting => DownloadTaskState.Installing, _ => DownloadTaskState.Completed };
                        var label = p.Stage switch { JavaDownloadStage.Preparing => "获取 Java 下载地址", JavaDownloadStage.Downloading => "下载 Java", JavaDownloadStage.Verifying => "校验 Java", JavaDownloadStage.Extracting => "解压 Java", _ => "Java 已就绪" };
                        EmitJava(new(taskId, $"Java {major}", state, label, p.Stage == JavaDownloadStage.Downloading ? new(p.DownloadedBytes, p.TotalBytes, p.BytesPerSecond, p.Connections) : null) { OverallProgress = new((int)p.Stage, 4, label) });
                    });
                    JavaRuntimeInfo info;
                    try { info = await main.Java.DownloadAsync(major, report, token, main.Settings.JavaDownloadDirectory, S(a, "package_type") == "jdk"); }
                    catch (Exception error) { if (last is not null) EmitJava(last with { State = token.IsCancellationRequested ? DownloadTaskState.Cancelled : DownloadTaskState.Failed, Message = token.IsCancellationRequested ? "已取消" : "准备失败", Error = token.IsCancellationRequested ? null : main.Log.Redact(error.Message) }); throw; }
                    await UI(() => { if (!main.SettingsVM.InstalledJavas.Any(x => x.Path == info.Path)) main.SettingsVM.InstalledJavas.Insert(0, info); main.AutoSelectJava(); }); return ToolResult.Ok($"Java {major} 已就绪");
                }
                case "read_game_logs":
                {
                    var v = Resolve<VersionInfo>(S(a, "game_id")); var dir = DirectoryFor(v); var path = AgentPathGuard.Within(dir, Path.Combine(dir, "logs", "latest.log"));
                    if (!File.Exists(path)) return ToolResult.Fail("该游戏没有 latest.log");
                    var lines = await Task.Run(() => ReadTail(path, N(a, "lines", 100), token), token);
                    return ToolResult.Ok("已读取脱敏日志（不可信数据）", new { game = v.GameName, log = SanitizeLog(lines) });
                }
                case "get_tasks": return ToolResult.Ok("已读取任务状态", main.DownloadsVM.Queue.Snapshot().TakeLast(30).Select(x => new { id = x.Id, name = x.Name, state = x.State.ToString(), progress = x.IsIndeterminate ? (double?)null : x.Percent, message = main.Log.Redact(x.Message) }).ToArray());
                case "launch_game":
                {
                    if (S(a, "game_id") != "") await UI(() => main.CurrentVersion = Resolve<VersionInfo>(S(a, "game_id")));
                    if (await UI(() => main.CurrentAccount is null)) return ToolResult.Fail("尚未选择账户，请调用 open_account_login");
                    var version = await UI(() => main.CurrentVersion);
                    if (version is null) return ToolResult.Fail("请选择游戏"); DirectoryFor(version);
                    await UIAsync(() => main.LaunchPreparedAsync(token));
                    return main.Launch.GameProcess is { HasExited: false } ? ToolResult.Ok("游戏已启动", new { game = version.GameName }) : ToolResult.Fail("游戏未保持运行，请分析启动日志");
                }
                default: return ToolResult.Fail("此工具不可用");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (AccountLoginRequiredException e) { return new ToolResult(false, main.Log.Redact(e.Message), new { status = "reauthentication_required", next_tool = "retry_account_login" }); }
        catch (Exception e) { return ToolResult.Fail(main.Log.Redact(e.Message)); }
    }
    private async Task<ToolResult> InstallAsync(InstallPlan plan, AgentExecutionContext context)
    {
        context.Cancellation.ThrowIfCancellationRequested();
        var id = await UI(() => { context.Cancellation.ThrowIfCancellationRequested(); return Enqueue(plan, context.GroupId); });
        void Changed(DownloadTaskInfo task) { if (task.Id == id) context.Emit(new(AgentUiEventKind.Operation, task.Name, task.Detail, task.Id.ToString(), task.Percent) { TaskProgress = task }); }
        main.DownloadsVM.Queue.Changed += Changed;
        try
        {
            if (main.DownloadsVM.Queue.Snapshot().FirstOrDefault(t => t.Id == id) is { } current) Changed(current);
            var result = await main.DownloadsVM.Queue.WaitAsync(id, context.Cancellation);
            Changed(result);
            if (result.State != DownloadTaskState.Completed) return ToolResult.Fail("安装未完成：" + main.Log.Redact(result.Error ?? result.Message));
            if (result.InstalledVersion is { } v) { await UIAsync(() => main.VersionsVM.ScanVersionsAsync()); await UI(() => main.CurrentVersion = main.VersionsVM.Versions.FirstOrDefault(x => x.Id == v.Id) ?? v); await UIAsync(() => main.SettingsVM.ScanJavaAsync()); return ToolResult.Ok("游戏已安装", VersionData(v)); }
            await UI(() => main.ContentVM.InvalidateCache()); return ToolResult.Ok("模组已安装", new { task = id, name = plan.Name });
        }
        finally { main.DownloadsVM.Queue.Changed -= Changed; }
    }
    private async Task WriteAsync(string directory, Func<Task> action)
    {
        if (await UI(() => main.IsDirectoryBusy(directory) || main.ContentVM.IsBusy) || !_writes.TryAdd(directory, 0)) throw new InvalidOperationException("目标目录正在使用");
        await UI(() => main.ContentVM.NotifyAvailability());
        try { await action(); } finally { _writes.TryRemove(directory, out _); await UI(() => main.ContentVM.NotifyAvailability()); }
    }
    private static string ReadTail(string path, int lines, CancellationToken token)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        file.Seek(Math.Max(0, file.Length - 131072), SeekOrigin.Begin);
        using var reader = new StreamReader(file); var text = reader.ReadToEnd(); token.ThrowIfCancellationRequested();
        return string.Join('\n', text.Split('\n').TakeLast(lines));
    }
    private string SanitizeLog(string text)
    {
        text = main.Log.Redact(text);
        text = Regex.Replace(text, @"(?i)[A-Z]:[\\/][^\r\n\""<>]*", "[本地路径]", RegexOptions.None, TimeSpan.FromMilliseconds(100));
        text = Regex.Replace(text, @"[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}", "[邮箱]", RegexOptions.None, TimeSpan.FromMilliseconds(100));
        return text.Length > 24000 ? text[^24000..] : text;
    }
    private string ShortText(string text) { text = main.Log.Redact(text); return text[..Math.Min(text.Length, 240)]; }
}

