using System.Net;
using System.Text;

namespace Launcher.Core;

/// <summary>Local names/aliases resolve Chinese queries before the platform search.</summary>
public sealed class ChineseModSearchIndex
{
    public const string NameTableUrl = "https://raw.githubusercontent.com/HMCL-dev/HMCL/main/HMCL/src/main/resources/assets/mod_data.txt";
    private readonly string _cache;
    private readonly HttpMessageHandler? _transport;
    private volatile Entry[] _entries = Seeds();
    private volatile IReadOnlyDictionary<string, string> _displayNames = NameLookup(Seeds());
    private Task? _warm;
    private readonly object _gate = new();
    public int Count => _entries.Length;
    public ChineseModSearchIndex(string? directory = null, HttpMessageHandler? transport = null)
    { _cache = Path.Combine(directory ?? Path.Combine(AppPaths.Data, "downloads"), "chinese-mod-names.txt"); _transport = transport; }
    public static bool ContainsChinese(string value) => value.Any(c => c is >= '\u3400' and <= '\u9fff');
    public Task WarmAsync(CancellationToken token)
    { lock (_gate) { if (_warm is { IsCanceled: true } or { IsFaulted: true }) _warm = null; return _warm ??= LoadAsync(token); } }
    private async Task LoadAsync(CancellationToken token)
    {
        try
        {
            if (File.Exists(_cache))
            {
                var text = await File.ReadAllTextAsync(_cache, token).ConfigureAwait(false);
                await Task.Run(() => SetTable(text), token).ConfigureAwait(false);
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(_cache) < TimeSpan.FromDays(7)) return;
            }
            using var http = new HttpClient(_transport ?? new DownloadConnectionBudget.Handler(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip }), disposeHandler: _transport is null)
                { Timeout = TimeSpan.FromSeconds(6) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("NovainaLauncher/1.0");
            var table = await http.GetStringAsync(NameTableUrl, token).ConfigureAwait(false);
            var entries = await Task.Run(() => ParseTable(table), token).ConfigureAwait(false);
            if (entries.Length == 0) return;
            SetEntries(entries);
            Directory.CreateDirectory(Path.GetDirectoryName(_cache)!);
            var temporary = _cache + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { await File.WriteAllTextAsync(temporary, table, token).ConfigureAwait(false); token.ThrowIfCancellationRequested(); File.Move(temporary, _cache, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException) { }
    }
    private void SetTable(string text)
    {
        var parsed = ParseTable(text);
        if (parsed.Length > 0) SetEntries(parsed);
    }
    private void SetEntries(Entry[] entries)
    {
        var combined = Seeds().Concat(entries).ToArray();
        _displayNames = NameLookup(combined); _entries = combined;
    }
    private static IReadOnlyDictionary<string, string> NameLookup(IEnumerable<Entry> entries) => entries
        .Where(entry => !string.IsNullOrWhiteSpace(entry.Chinese)).GroupBy(entry => DisplayKey(entry.English))
        .ToDictionary(group => group.Key, group => group.First().Chinese!, StringComparer.Ordinal);
    private static string DisplayKey(string name) => Normalize(System.Text.RegularExpressions.Regex.Replace(name, @"\s*[（(].*[)）]\s*$", ""));
    private static Entry[] ParseTable(string text) => text.Split('\n').Where(line => !line.StartsWith('#'))
        .Select(line => line.TrimEnd('\r').Split(';')).Where(fields => fields.Length == 6 && !string.IsNullOrWhiteSpace(fields[4]))
        .Select(fields => new Entry(fields[4].Trim(), ContainsChinese(fields[3]) ? fields[3].Trim() : null, new[] { fields[3], fields[4], fields[5] }.Concat(fields[2].Split(',')).Select(Normalize).Where(s => s.Length > 0).ToArray())).ToArray();
    public string? ChineseName(string english)
    {
        if (ContainsChinese(english)) return null;
        var key = DisplayKey(english);
        return _displayNames.GetValueOrDefault(key);
    }
    public async Task<IReadOnlyList<string>> ResolveAsync(string query, CancellationToken token)
    {
        var matches = await Task.Run(() => ResolveLocal(query), token).ConfigureAwait(false);
        if (matches.Count > 0) return matches;
        await WarmAsync(token).WaitAsync(token).ConfigureAwait(false);
        return await Task.Run(() => ResolveLocal(query), token).ConfigureAwait(false);
    }
    public IReadOnlyList<string> ResolveLocal(string query)
    {
        var key = Normalize(query);
        if (key.Length == 0) return [];
        return _entries.Select(entry => (entry.English, Score: entry.Keys.Select(name => Match(key, name)).DefaultIfEmpty().Max()))
            .Where(match => match.Score > 0).OrderByDescending(match => match.Score)
            .Select(match => match.English).Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToArray();
    }
    private static int Match(string query, string name)
    {
        if (name == query) return 1000;
        if (name.StartsWith(query, StringComparison.Ordinal)) return 800 - Math.Min(100, name.Length - query.Length);
        if (name.Contains(query, StringComparison.Ordinal)) return 600 - Math.Min(100, name.Length - query.Length);
        return 0;
    }
    public static string Normalize(string value) => new(value.Normalize(NormalizationForm.FormKC).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private sealed record Entry(string English, string? Chinese, string[] Keys);
    private static Entry[] Seeds()
    {
        Entry Alias(string english, params string[] aliases) => new(english, aliases.FirstOrDefault(ContainsChinese), aliases.Append(english).Select(Normalize).ToArray());
        return [Alias("Sodium", "钠", "鈉", "钠优化"), Alias("Lithium", "锂", "鋰"), Alias("Iris Shaders", "鸢尾", "鸢尾光影", "光影加载器"),
            Alias("Entity Culling", "实体渲染优化", "实体剔除", "实体优化"), Alias("FerriteCore", "铁氧体磁芯", "内存优化"),
            Alias("Create", "机械动力"), Alias("The Twilight Forest", "暮色森林", "暮色"), Alias("Jade", "玉", "玉信息显示"),
            Alias("Just Enough Items", "JEI", "物品管理器", "物品管理"), Alias("Roughly Enough Items", "REI", "粗略物品管理器"),
            Alias("JourneyMap", "旅行地图", "旅途地图"), Alias("Xaero's Minimap", "Xaero小地图", "赛罗小地图", "小地图"),
            Alias("Xaero's World Map", "Xaero世界地图", "赛罗世界地图", "世界地图"), Alias("FTB Ultimine", "连锁破坏", "连锁挖矿"),
            Alias("Applied Energistics 2", "应用能源2", "应用能源", "AE2"), Alias("Refined Storage", "精致存储", "精致储存", "RS"),
            Alias("Industrial Craft 2", "工业时代2", "工业2", "IC2"), Alias("Mekanism", "通用机械", "通用机器"),
            Alias("Tinkers Construct", "匠魂"), Alias("Botania", "植物魔法"), Alias("Thermal Expansion", "热力膨胀"),
            Alias("Biomes O' Plenty", "超多生物群系"), Alias("Waystones", "传送石碑"), Alias("Sophisticated Backpacks", "精妙背包"),
            Alias("Mouse Tweaks", "鼠标手势"), Alias("Inventory Profiles Next", "一键整理", "背包整理"), Alias("AppleSkin", "苹果皮"),
            Alias("Litematica", "投影"), Alias("WorldEdit", "创世神", "世界编辑"), Alias("Chunky", "区块预生成"),
            Alias("ModernFix", "现代修复"), Alias("ImmediatelyFast", "立即优化"), Alias("Better Combat", "更好的战斗"),
            Alias("Distant Horizons", "遥远的地平线", "远景"), Alias("Cloth Config API", "布料配置"),
            Alias("Mod Menu", "模组菜单"), Alias("Fabric API", "Fabric前置", "织物API")];
    }
}
