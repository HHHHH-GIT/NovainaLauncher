using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Launcher.Core;

namespace Launcher.AI;

public sealed class WorkbenchOperations : ILauncherOperations
{
    private readonly Func<IReadOnlyList<(string Id, JavaRuntimeInfo Java)>> _javas;
    private readonly Func<string, string> _redact;
    private readonly Func<bool> _fullAccess;
    private readonly Dictionary<string, ModTemplate> _templates = new();
    private readonly Dictionary<string, string> _sourceJars = new();
    private readonly HashSet<string> _trustedBuilds = new();
    private readonly SemaphoreSlim _gate = new(1);
    private static readonly HttpClient DefaultHttp = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(2) };
    private readonly HttpClient _http;
    public string? Root { get; private set; }
    public WorkbenchOperations(Func<IReadOnlyList<(string Id, JavaRuntimeInfo Java)>> javas, Func<string, string> redact, Func<bool>? fullAccess = null, HttpClient? http = null)
    { _javas = javas; _redact = redact; _fullAccess = fullAccess ?? (() => false); _http = http ?? DefaultHttp; }
    public void Authorize(string root)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("项目目录不存在");
        if (!_fullAccess())
        {
            if (Path.GetPathRoot(root) == root || root == Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                || root.Equals(AppPaths.Data, StringComparison.OrdinalIgnoreCase) || root.Equals(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("请选择一个独立的 Mod 项目目录");
            AgentPathGuard.Within(root, root);
        }
        Root = root; _trustedBuilds.Clear(); _templates.Clear(); _sourceJars.Clear();
    }
    public void Revoke() { Root = null; _trustedBuilds.Clear(); _templates.Clear(); _sourceJars.Clear(); }
    public Task CancelGroupAsync(Guid groupId) => Task.CompletedTask;
    public Task<AgentApproval> DescribeSensitiveAsync(string name, JsonObject args, CancellationToken token) => Task.FromResult(new AgentApproval("修改项目", args.ToJsonString()));
    private string Workspace => Root is { } root ? _fullAccess() ? Path.GetFullPath(root) : AgentPathGuard.Within(root, root) : throw new InvalidOperationException("请在工作台点击“选择项目”或“新建项目”授权目录");
    private string ManagedPath(string root, string path) => _fullAccess() ? Path.GetFullPath(path, root) : AgentPathGuard.Within(root, path);
    private string ProjectPath(string root, string path) => _fullAccess() ? Path.GetFullPath(path, root) : SourcePath(root, path);
    private static readonly HashSet<string> Extensions = [".java", ".kt", ".gradle", ".kts", ".properties", ".json", ".toml", ".mcmeta", ".md", ".txt", ".xml", ".yaml", ".yml", ".cfg", ".accesswidener", ".gitignore", ".gitattributes"];
    public static string SourcePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') || relative.Contains('\0')) throw new UnauthorizedAccessException("必须使用项目相对路径");
        var segments = relative.Replace('\\', '/').Split('/');
        if (segments.Any(s => new[] { ".git", ".gradle", ".novaina", "build", "run", "runs" }.Contains(s, StringComparer.OrdinalIgnoreCase) || s.StartsWith(".env", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(s, "(?i)(secret|credential|account|password|token|apikey|api.key|keystore)|\\.(pem|key|pfx|protected)$"))) throw new UnauthorizedAccessException("缓存、运行数据与敏感文件不可访问");
        var path = AgentPathGuard.Within(root, Path.Combine(root, relative)); var name = Path.GetFileName(path);
        if (!Extensions.Contains(Path.GetExtension(path).ToLowerInvariant()) && name is not ".gitignore" and not ".gitattributes" and not "LICENSE") throw new UnauthorizedAccessException("只允许项目源码、资源及构建文本");
        return path;
    }
    private IEnumerable<string> Files()
    {
        var root = Workspace; var pending = new Stack<string>(); pending.Push(root); int count = 0;
        while (pending.TryPop(out var directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                var relative = Path.GetRelativePath(root, path);
                if (Directory.Exists(path)) { if (relative.Split(Path.DirectorySeparatorChar).Any(x => x.StartsWith('.') || x is "build" or "run" or "runs")) continue; pending.Push(path); }
                else { try { ProjectPath(root, relative); } catch (UnauthorizedAccessException) { continue; } yield return relative; if (++count >= 3000) yield break; }
            }
        }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private async Task<byte[]> ReadBytes(string path, CancellationToken token)
    { if (new FileInfo(path).Length > 256000) throw new IOException("文件过大，请按功能拆分后读取"); return await File.ReadAllBytesAsync(path, token); }
    public async Task<ToolResult> ExecuteAsync(string name, JsonObject args, AgentExecutionContext context)
    {
        await _gate.WaitAsync(context.Cancellation);
        try
        {
            var token = context.Cancellation;
            switch (name)
            {
                case "get_development_guide": return ToolResult.Ok("已读取内置开发指南", new { guide = ModDevelopmentGuides.Read(args["topic"]!.ToString()) });
                case "inspect_workspace":
                    return ToolResult.Ok(Root is null ? "尚未授权项目" : "已检查项目", new { authorized = Root is not null, directory = Root,
                        files = Root is null ? [] : Files().Take(120).ToArray(),
                        javas = _javas().Select(x => new { id = x.Id, major = x.Java.Major, vendor = x.Java.Vendor, jdk = File.Exists(Path.Combine(Path.GetDirectoryName(x.Java.Path)!, "javac.exe")) }) });
                case "list_workspace_files": return ToolResult.Ok("项目文件列表", new { files = Files().ToArray() });
                case "list_dependency_sources": return await Task.Run(() => DependencySources(args["query"]?.ToString() ?? "", token), token);
                case "read_dependency_source":
                    if (!_sourceJars.TryGetValue(args["source_id"]!.ToString(), out var sourceJar)) return ToolResult.Fail("源码 ID 未查询或已失效");
                    ManagedPath(Workspace, sourceJar); using (var zip = ZipFile.OpenRead(sourceJar))
                    {
                        var entry = zip.GetEntry(args["entry"]!.ToString());
                        if (entry is null || !entry.FullName.EndsWith(".java") || entry.Length > 256000) return ToolResult.Fail("未找到可读取的 Java 源码条目");
                        await using var input = entry.Open(); using var memory = new MemoryStream(); var buffer = new byte[8192]; int count;
                        while ((count = await input.ReadAsync(buffer, token)) > 0) { if (memory.Length + count > 256000) throw new IOException("源码条目过大"); await memory.WriteAsync(buffer.AsMemory(0, count), token); }
                        return ToolResult.Ok("已读取当前依赖源码", new { entry = entry.FullName, content = _redact(Encoding.UTF8.GetString(memory.ToArray())) });
                    }
                case "read_project_file":
                    var relative = args["path"]!.ToString(); var bytes = await ReadBytes(ProjectPath(Workspace, relative), token);
                    return ToolResult.Ok("已读取项目文件", new { path = relative, sha256 = Hash(bytes), content = _redact(Encoding.UTF8.GetString(bytes)) });
                case "search_project":
                    var query = args["query"]!.ToString(); if (query.Length == 0) return ToolResult.Fail("关键词不能为空");
                    var hits = new List<object>();
                    foreach (var file in Files())
                    {
                        token.ThrowIfCancellationRequested(); var path = ProjectPath(Workspace, file); if (new FileInfo(path).Length > 256000) continue;
                        var lines = await File.ReadAllLinesAsync(path, token);
                        for (int i = 0; i < lines.Length && hits.Count < 40; i++) if (lines[i].Contains(query, StringComparison.OrdinalIgnoreCase)) hits.Add(new { path = file, line = i + 1, text = _redact(lines[i][..Math.Min(lines[i].Length, 600)]) });
                        if (hits.Count >= 40) break;
                    }
                    return ToolResult.Ok("项目搜索完成", new { matches = hits });
                case "write_project_file":
                case "delete_project_file": return await ModifyAsync(name, args, context);
                case "list_mod_templates": return await TemplatesAsync(args, token);
                case "create_mod_project": return await CreateAsync(args["template_id"]!.ToString(), context);
                case "run_terminal": return await RunAsync(args, context);
                case "list_build_artifacts": return await ArtifactsAsync(token);
                default: return ToolResult.Fail("未知工作台操作");
            }
        }
        catch (OperationCanceledException) when (context.Cancellation.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return ToolResult.Fail("官方模板请求超时；尚未完成初始化，请检查 GitHub/官方 Maven 的网络连接后重试"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or HttpRequestException or ArgumentException)
        { return ToolResult.Fail(_redact(e.Message)); }
        finally { _gate.Release(); }
    }
    private async Task<ToolResult> ModifyAsync(string operation, JsonObject args, AgentExecutionContext context)
    {
        var relative = args["path"]!.ToString(); var root = Workspace; var path = ProjectPath(root, relative); var token = context.Cancellation;
        var expected = args["expected_sha256"]!.ToString(); var exists = File.Exists(path);
        var before = exists ? await ReadBytes(path, token) : [];
        if ((exists ? Hash(before) : "") != expected) return ToolResult.Fail("文件已变化或未读取，请重新读取后再修改");
        var content = args["content"]?.ToString() ?? ""; var bytes = Encoding.UTF8.GetBytes(content);
        if (operation == "delete_project_file" && !exists) return ToolResult.Fail("文件不存在");
        if (exists && operation == "write_project_file" && before.AsSpan().SequenceEqual(bytes)) return ToolResult.Ok("文件内容无需修改");
        if (exists)
        {
            var preview = _redact(content[..Math.Min(content.Length, 2400)]);
            if (!await context.Interaction.ApproveAsync(new(operation == "delete_project_file" ? "删除项目文件" : "修改项目文件", $"项目：{root}\n文件：{relative}\n原 SHA256：{expected}\n" + (operation == "delete_project_file" ? "删除后不可由启动器自动恢复。" : $"新 SHA256：{Hash(bytes)}\n新内容预览：\n{preview}")), token)) return ToolResult.Fail("用户未批准文件修改");
        }
        token.ThrowIfCancellationRequested(); ProjectPath(root, relative);
        // Recheck after approval; never replace a user's concurrent edit.
        if (File.Exists(path) != exists || (exists && Hash(await ReadBytes(path, token)) != expected)) return ToolResult.Fail("确认期间文件发生变化，请重新读取");
        if (operation == "delete_project_file") File.Delete(path);
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); ProjectPath(root, relative);
            var temporary = path + ".novaina-" + Guid.NewGuid().ToString("N") + ".tmp";
            try { await File.WriteAllBytesAsync(temporary, bytes, token); token.ThrowIfCancellationRequested(); ProjectPath(root, relative); File.Move(temporary, path, exists); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return ToolResult.Ok(operation == "delete_project_file" ? "已删除项目文件" : "已保存项目文件", new { path = relative, sha256 = operation == "delete_project_file" ? "" : Hash(bytes) });
    }
    private sealed record ModTemplate(string Id, string Loader, string Minecraft, string Source, string Url, string Commit);
    private async Task<HttpResponseMessage> GetOfficialAsync(string url, CancellationToken token)
    {
        static bool Official(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
            new[] { "api.github.com", "github.com", "codeload.github.com", "files.minecraftforge.net", "maven.minecraftforge.net" }.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
        var uri = new Uri(url); if (!Official(uri)) throw new IOException("非官方模板地址");
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                for (int redirect = 0; redirect < 4; redirect++)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, uri); request.Headers.UserAgent.ParseAdd("NovainaLauncher/1.0.0");
                    var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                    if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                    {
                        var next = new Uri(uri, location); response.Dispose(); if (!Official(next)) throw new IOException("官方模板跳转到了未允许的地址"); uri = next; continue;
                    }
                    if ((int)response.StatusCode is 408 or 429 or >= 500 && attempt < 2)
                    { var delay = Math.Clamp(response.Headers.RetryAfter?.Delta?.TotalSeconds ?? (attempt + 1) * 2, 1, 8); response.Dispose(); await Task.Delay(TimeSpan.FromSeconds(delay), token); break; }
                    if (!response.IsSuccessStatusCode) { var code = response.StatusCode; response.Dispose(); throw new HttpRequestException($"官方模板请求失败：{uri.Host} HTTP {(int)code}", null, code); }
                    return response;
                }
            }
            catch (HttpRequestException e) when (attempt < 2 && e.StatusCode is null) { await Task.Delay(TimeSpan.FromSeconds(attempt + 1), token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && attempt < 2) { await Task.Delay(TimeSpan.FromSeconds(attempt + 1), token); }
        }
        throw new IOException("官方模板暂时不可达，请检查网络后重试；未修改项目文件");
    }
    private async Task<JsonNode> GetJson(string url, CancellationToken token)
    {
        using var response = await GetOfficialAsync(url, token);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(token))!;
    }
    private async Task<ToolResult> TemplatesAsync(JsonObject args, CancellationToken token)
    {
        var loader = args["loader"]!.ToString(); var minecraft = args["minecraft"]!.ToString();
        if (!Regex.IsMatch(minecraft, "^[0-9]+(?:\\.[0-9]+){1,2}$")) return ToolResult.Fail("请输入明确的正式 Minecraft 版本");
        string url, source, commit;
        if (loader is "Fabric" or "NeoForge")
        {
            var repo = loader == "Fabric" ? "FabricMC/fabric-example-mod" : $"NeoForgeMDKs/MDK-{minecraft}-ModDevGradle";
            var branch = loader == "Fabric" ? minecraft : "main";
            JsonNode node;
            try { node = await GetJson($"https://api.github.com/repos/{repo}/commits/{Uri.EscapeDataString(branch)}", token); }
            catch (HttpRequestException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                if (loader != "NeoForge") return ToolResult.Fail($"官方没有 {loader} {minecraft} 模板分支，不能使用其他版本冒充");
                repo = $"NeoForgeMDKs/MDK-{minecraft}-NeoGradle";
                try { node = await GetJson($"https://api.github.com/repos/{repo}/commits/main", token); }
                catch (HttpRequestException missing) when (missing.StatusCode == System.Net.HttpStatusCode.NotFound) { return ToolResult.Fail($"官方没有 NeoForge {minecraft} 的 ModDevGradle 或 NeoGradle 模板，请选择官方支持的版本"); }
            }
                commit = node["sha"]!.ToString(); if (!Regex.IsMatch(commit, "^[a-f0-9]{40}$")) throw new IOException("官方模板返回的版本标识无效"); source = $"https://github.com/{repo}/tree/{commit}"; url = $"https://codeload.github.com/{repo}/zip/{commit}";
        }
        else
        {
            var promotions = await GetJson("https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json", token);
            var version = promotions["promos"]?[minecraft + "-recommended"]?.ToString() ?? promotions["promos"]?[minecraft + "-latest"]?.ToString();
            if (version is null || !Regex.IsMatch(version, "^[0-9.]+$")) return ToolResult.Fail("官方目录未返回对应 Forge MDK");
            commit = minecraft + "-" + version; source = "https://files.minecraftforge.net/";
            url = $"https://maven.minecraftforge.net/net/minecraftforge/forge/{commit}/forge-{commit}-mdk.zip";
        }
        var id = "template-" + Hash(Encoding.UTF8.GetBytes(url))[..24]; var template = new ModTemplate(id, loader, minecraft, source, url, commit); _templates[id] = template;
        return ToolResult.Ok("已查询官方模板", new { templates = new[] { new { template_id = id, loader, minecraft, source, revision = commit } } });
    }
    private async Task<ToolResult> CreateAsync(string id, AgentExecutionContext context)
    {
        var root = Workspace; if (!_templates.TryGetValue(id, out var template)) return ToolResult.Fail("模板 ID 未查询或已失效");
        if (Directory.EnumerateFileSystemEntries(root).Any()) return ToolResult.Fail("项目目录非空，禁止覆盖；请新建一个项目");
        var token = context.Cancellation; context.Emit(new(AgentUiEventKind.Operation, "正在获取官方模板", template.Loader + " · " + template.Minecraft));
        using var response = await GetOfficialAsync(template.Url, token);
        if (response.Content.Headers.ContentLength > 20 * 1024 * 1024) throw new IOException("模板过大");
        await using var incoming = await response.Content.ReadAsStreamAsync(token); using var memory = new MemoryStream();
        var buffer = new byte[81920]; int read; while ((read = await incoming.ReadAsync(buffer, token)) > 0) { if (memory.Length + read > 20 * 1024 * 1024) throw new IOException("模板过大"); await memory.WriteAsync(buffer.AsMemory(0, read), token); }
        memory.Position = 0; using var zip = new ZipArchive(memory, ZipArchiveMode.Read);
        if (zip.Entries.Count > 3000 || zip.Entries.Sum(x => x.Length) > 50 * 1024 * 1024) throw new IOException("模板解包大小超限");
        var prefix = template.Loader == "Forge" ? "" : zip.Entries.First(x => x.Name.Length > 0).FullName.Split('/')[0] + "/";
        var plan = zip.Entries.Where(x => x.Name.Length > 0).Select(x => (Entry: x, Relative: x.FullName[prefix.Length..])).ToArray();
        foreach (var (entry, relative) in plan)
        {
            if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal) || Path.IsPathRooted(relative) || relative.Contains(':') || relative.Split('/').Contains("..") || (entry.ExternalAttributes >> 16 & 0xF000) == 0xA000) throw new IOException("模板包含非法路径或链接");
            ManagedPath(root, Path.Combine(root, relative));
        }
        var created = new List<string>(); var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var (entry, relative) in plan)
            {
                token.ThrowIfCancellationRequested(); var path = ManagedPath(root, Path.Combine(root, relative));
                for (var directory = Path.GetDirectoryName(path)!; directory != root && !Directory.Exists(directory); directory = Path.GetDirectoryName(directory)!) directories.Add(ManagedPath(root, directory));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!); ManagedPath(root, path);
                await using var target = new FileStream(path, FileMode.CreateNew); created.Add(path); await using var source = entry.Open(); await source.CopyToAsync(target, token);
            }
            return ToolResult.Ok("官方 Mod 项目已初始化；尚未构建", new { loader = template.Loader, minecraft = template.Minecraft, source = template.Source, revision = template.Commit, files = Files().Take(80).ToArray() });
        }
        catch
        {
            foreach (var file in created) { ManagedPath(root, file); File.Delete(file); }
            foreach (var directory in directories.OrderByDescending(x => x.Length))
            { ManagedPath(root, directory); if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory); }
            throw;
        }
    }
    private async Task<ToolResult> RunAsync(JsonObject args, AgentExecutionContext context)
    {
        var root = Workspace; var program = args["program"]!.ToString(); var action = args["action"]!.ToString(); var token = context.Cancellation;
        var jdk = _javas().FirstOrDefault(x => x.Id == args["jdk_id"]?.ToString());
        if (args["jdk_id"] is not null && jdk.Java is null) return ToolResult.Fail("JDK ID 未查询或已失效，请先检查环境");
        var requiredJava = RequiredJava(root);
        if (jdk.Java is null) jdk = _javas().Where(x => File.Exists(Path.Combine(Path.GetDirectoryName(x.Java.Path)!, "javac.exe")) && (requiredJava is null || x.Java.Major == requiredJava)).OrderByDescending(x => x.Java.Major).FirstOrDefault();
        var info = new ProcessStartInfo { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        var home = jdk.Java is null ? "" : Path.GetDirectoryName(Path.GetDirectoryName(jdk.Java.Path))!;
        if (program == "gradle")
        {
            if (action is not ("build" or "check" or "test" or "classes" or "compileJava" or "tasks" or "runData" or "genSources")) return ToolResult.Fail("未允许的 Gradle 动作");
            if (requiredJava is { } required && jdk.Java?.Major != required) return ToolResult.Fail($"项目明确要求 JDK {required}；当前选择 {(jdk.Java is null ? "无匹配 JDK" : "JDK " + jdk.Java.Major)}。请查询/准备对应开发 JDK 后构建，不使用其他主版本代替");
            if (jdk.Java is null || !File.Exists(Path.Combine(home, "bin", "javac.exe"))) return ToolResult.Fail("没有开发 JDK，请选择/准备包含 javac 的 JDK，JRE 不足以开发");
            var wrapper = ManagedPath(root, Path.Combine(root, "gradle/wrapper/gradle-wrapper.jar"));
            if (!File.Exists(wrapper)) return ToolResult.Fail("项目缺少 Gradle Wrapper，先初始化官方模板");
            var trust = await BuildFingerprintAsync(root, wrapper, token);
            if (!_trustedBuilds.Contains(trust))
            {
                if (!await context.Interaction.ApproveAsync(new("允许执行项目构建", $"项目：{root}\n命令：Gradle Wrapper {action} --no-daemon\nJDK：{jdk.Java.Major} · {home}\n构建配置指纹：{trust}\nGradle 和插件能运行代码并访问系统；这是受信任项目的构建授权，不是操作系统沙箱。源码变更可继续构建，构建配置变化会再次询问。"), token)) return ToolResult.Fail("未授权执行构建");
                token.ThrowIfCancellationRequested();
                if (await BuildFingerprintAsync(root, wrapper, token) != trust) return ToolResult.Fail("确认期间构建配置发生变化，请重新检查并授权");
                _trustedBuilds.Add(trust);
            }
            info.FileName = jdk.Java.Path;
            // JVM/Gradle do not inherit the launcher's Windows proxy automatically.
            foreach (var scheme in new[] { "http", "https" })
            {
                var target = new Uri(scheme + "://maven.neoforged.net/"); var proxy = HttpClient.DefaultProxy.GetProxy(target);
                if (proxy is not null && proxy != target && proxy.Scheme is "http" or "https")
                { info.ArgumentList.Add($"-D{scheme}.proxyHost={proxy.Host}"); info.ArgumentList.Add($"-D{scheme}.proxyPort={proxy.Port}"); }
            }
            info.ArgumentList.Add("-Dorg.gradle.internal.http.connectionTimeout=60000"); info.ArgumentList.Add("-Dorg.gradle.internal.http.socketTimeout=120000");
            info.ArgumentList.Add("-classpath"); info.ArgumentList.Add(wrapper); info.ArgumentList.Add("org.gradle.wrapper.GradleWrapperMain"); info.ArgumentList.Add(action); info.ArgumentList.Add("--no-daemon"); info.ArgumentList.Add("--console=plain"); info.ArgumentList.Add("--stacktrace");
        }
        else if (program == "java") { if (action != "version" || jdk.Java is null) return ToolResult.Fail("仅支持已识别 Java 的版本查询"); info.FileName = jdk.Java.Path; info.ArgumentList.Add("-version"); }
        else if (program == "git")
        {
            if (action is not ("status" or "diff")) return ToolResult.Fail("仅允许 Git 状态/差异");
            var git = FindGit(); if (git is null) return ToolResult.Fail("未检测到 Git，请先安装 Git for Windows"); info.FileName = git;
            foreach (var arg in new[] { "-c", "core.fsmonitor=false", "-c", "core.hooksPath=NUL", "--no-pager", action }) info.ArgumentList.Add(arg);
            info.ArgumentList.Add(action == "status" ? "--short" : "--no-ext-diff"); if (action == "diff") info.ArgumentList.Add("--no-textconv");
        }
        else return ToolResult.Fail("未允许的开发程序");
        var cache = ManagedPath(root, Path.Combine(root, ".novaina")); Directory.CreateDirectory(cache); var temp = ManagedPath(root, Path.Combine(cache, "tmp")); Directory.CreateDirectory(temp);
        info.Environment.Clear(); info.Environment["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows); info.Environment["WINDIR"] = info.Environment["SystemRoot"];
        info.Environment["TEMP"] = info.Environment["TMP"] = temp; info.Environment["USERPROFILE"] = root; info.Environment["JAVA_HOME"] = home;
        info.Environment["PATH"] = Path.Combine(home, "bin") + ";" + Environment.GetFolderPath(Environment.SpecialFolder.System); info.Environment["GRADLE_USER_HOME"] = Path.Combine(cache, "gradle");
        using var process = new Process { StartInfo = info }; using var job = new DeveloperProcessJob();
        context.Emit(new(AgentUiEventKind.Operation, "正在执行开发终端", program + " " + action)); process.Start();
        try { job.Assign(process); } catch { process.Kill(true); await process.WaitForExitAsync(); throw; }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(15));
        using var registration = timeout.Token.Register(() => job.Terminate());
        var output = new StringBuilder(); var tail = new Queue<string>(); int tailLength = 0; var outputGate = new object(); int lines = 0; bool truncated = false;
        async Task Pump(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                line = _redact(line[..Math.Min(line.Length, 2000)]);
                lock (outputGate)
                {
                    if (output.Length + line.Length < 32000 && !truncated) output.AppendLine(line);
                    else { truncated = true; tail.Enqueue(line); tailLength += line.Length + 2; while (tailLength > 16000 && tail.Count > 0) tailLength -= tail.Dequeue().Length + 2; }
                }
                if (Interlocked.Increment(ref lines) % 12 == 0) context.Emit(new(AgentUiEventKind.Operation, "开发终端运行中", line));
            }
        }
        await Task.WhenAll(Pump(process.StandardOutput), Pump(process.StandardError), process.WaitForExitAsync());
        token.ThrowIfCancellationRequested(); if (timeout.IsCancellationRequested) return ToolResult.Fail("构建超过 15 分钟，已终止本次进程树；可检查原因后重试");
        var result = new { program, action, exit_code = process.ExitCode, output = output.ToString() + (truncated ? "\n[中间输出已截断，以下为末尾]\n" + string.Join('\n', tail) : ""), truncated };
        return process.ExitCode == 0 ? ToolResult.Ok("开发终端执行完成", result) : new ToolResult(false, DiagnoseBuildFailure(result.output), result);
    }
    public static int? RequiredJava(string root)
    {
        foreach (var name in new[] { "build.gradle", "build.gradle.kts" })
        {
            var path = Path.Combine(root, name); if (!File.Exists(path) || new FileInfo(path).Length > 256000) continue;
            var text = File.ReadAllText(path);
            var match = Regex.Match(text, @"JavaLanguageVersion\s*\.\s*of\s*\(\s*(\d+)\s*\)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var version)) return version;
        }
        return null;
    }
    public static string DiagnoseBuildFailure(string output)
    {
        if (Regex.IsMatch(output, "(?i)(timed? out|unknownhost|connectexception|connection reset|could not (get|head)|PKIX|SSLHandshake|HTTP.*(?:429|502|503))")) return "构建依赖网络/TLS 请求失败；请查看实际错误中的仓库地址，检查系统代理或网络后重试";
        if (Regex.IsMatch(output, "(?i)(Unsupported class file|No matching toolchains|Cannot find a Java installation|requires Java|Could not find java)")) return "构建的 JDK/Gradle 版本不匹配；请按项目工具链要求准备对应 JDK";
        return "开发终端执行失败，请根据实际编译或构建错误修复";
    }
    private async Task<string> BuildFingerprintAsync(string root, string wrapper, CancellationToken token)
    {
        var files = Files().Where(x => x.StartsWith("buildSrc/", StringComparison.OrdinalIgnoreCase) || x.StartsWith("buildSrc\\", StringComparison.OrdinalIgnoreCase)
            || x.StartsWith("build-logic", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".gradle", StringComparison.OrdinalIgnoreCase)
            || x.EndsWith(".kts", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".properties", StringComparison.OrdinalIgnoreCase)
            || x.EndsWith(".toml", StringComparison.OrdinalIgnoreCase)).Select(x => ProjectPath(root, x)).Append(wrapper).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
        var fingerprint = new StringBuilder();
        foreach (var file in files) { ManagedPath(root, file); fingerprint.Append(Path.GetRelativePath(root, file)).Append(Hash(await File.ReadAllBytesAsync(file, token))); }
        return Hash(Encoding.UTF8.GetBytes(fingerprint.ToString()));
    }
    private static string? FindGit() => new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git/cmd/git.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs/Git/cmd/git.exe") }.FirstOrDefault(File.Exists);
    private ToolResult DependencySources(string query, CancellationToken token)
    {
        var root = Workspace; var pending = new Stack<string>();
        foreach (var sub in new[] { ".gradle", ".novaina/gradle/caches" }) { var path = ManagedPath(root, Path.Combine(root, sub)); if (Directory.Exists(path)) pending.Push(path); }
        var found = new List<object>(); int inspected = 0;
        while (pending.TryPop(out var directory) && inspected < 10000 && found.Count < 20)
        {
            token.ThrowIfCancellationRequested(); ManagedPath(root, directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                if (Directory.Exists(path)) { pending.Push(path); continue; }
                if (++inspected > 10000 || !Path.GetFileName(path).EndsWith("sources.jar", StringComparison.OrdinalIgnoreCase)) continue;
                ManagedPath(root, path); using var zip = ZipFile.OpenRead(path);
                var entries = zip.Entries.Where(e => e.FullName.EndsWith(".java") && (query.Length == 0 || e.FullName.Contains(query, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path).Contains(query, StringComparison.OrdinalIgnoreCase))).Take(80).Select(e => e.FullName).ToArray();
                if (entries.Length == 0) continue;
                var id = "source-" + Hash(Encoding.UTF8.GetBytes(path))[..24]; _sourceJars[id] = path; found.Add(new { source_id = id, archive = Path.GetFileName(path), entries });
                if (found.Count >= 20) break;
            }
        }
        return ToolResult.Ok(found.Count == 0 ? "未发现匹配的依赖源码；先构建或按官方插件运行 genSources" : "已查询真实依赖源码", new { sources = found });
    }
    private async Task<ToolResult> ArtifactsAsync(CancellationToken token)
    {
        var root = Workspace; var directory = ManagedPath(root, Path.Combine(root, "build/libs")); var files = new List<object>();
        if (Directory.Exists(directory)) foreach (var file in Directory.EnumerateFiles(directory, "*.jar").Take(30))
        {
            ManagedPath(root, file); using var zip = ZipFile.OpenRead(file);
            var metadata = zip.Entries.Where(e => e.FullName is "fabric.mod.json" or "META-INF/mods.toml" or "META-INF/neoforge.mods.toml").Select(e => e.FullName).ToArray();
            await using var stream = File.OpenRead(file); var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
            files.Add(new { path = Path.GetRelativePath(root, file), bytes = new FileInfo(file).Length, sha256 = hash, metadata, production = metadata.Length > 0 && !Regex.IsMatch(Path.GetFileName(file), "(?i)-(sources|dev|javadoc)\\.jar$") });
        }
        return ToolResult.Ok(files.Count == 0 ? "尚无构建产物" : "已检查构建产物", new { files });
    }
}
