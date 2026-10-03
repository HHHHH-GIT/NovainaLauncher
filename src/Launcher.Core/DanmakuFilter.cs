using System.Text.RegularExpressions;

namespace Launcher.Core;

public sealed class DanmakuFilter
{
    private readonly DanmakuBlockedPresets _presets;
    private readonly HashSet<string> _exact = new(StringComparer.OrdinalIgnoreCase);
    private readonly Regex[] _patterns;
    private readonly int[] _timedOut;
    public DanmakuFilter(DanmakuBlockedPresets presets, IEnumerable<DanmakuBlockRule> rules)
    {
        _presets = presets;
        var patterns = new List<Regex>();
        foreach (var rule in rules.Where(r => r is not null && r.Enabled && !string.IsNullOrEmpty(r.Pattern)))
        {
            if (rule.Kind == DanmakuMatchKind.Exact) _exact.Add(rule.Pattern);
            else if (rule.Kind == DanmakuMatchKind.Regex)
                try { patterns.Add(CreateRegex(rule.Pattern)); } catch (ArgumentException) { }
        }
        _patterns = patterns.ToArray();
        _timedOut = new int[_patterns.Length];
    }
    public bool IsBlocked(LauncherLogEvent entry)
    {
        if ((_presets.HasFlag(DanmakuBlockedPresets.Log4j) && entry.Message.Contains("log4j", StringComparison.OrdinalIgnoreCase))
            || (_presets.HasFlag(DanmakuBlockedPresets.AuthlibInjector) && entry.Message.Contains("authlib-injector", StringComparison.OrdinalIgnoreCase))
            || (_presets.HasFlag(DanmakuBlockedPresets.Warn) && entry.Level == LogLevel.Warning)
            || (_presets.HasFlag(DanmakuBlockedPresets.Error) && entry.Level == LogLevel.Error) || _exact.Contains(entry.Message)) return true;
        for (int i = 0; i < _patterns.Length; i++)
        {
            if (Volatile.Read(ref _timedOut[i]) != 0) continue;
            try { if (_patterns[i].IsMatch(entry.Message)) return true; }
            catch (RegexMatchTimeoutException) { Interlocked.Exchange(ref _timedOut[i], 1); }
        }
        return false;
    }
    public static string? Validate(DanmakuMatchKind kind, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return "请输入匹配内容";
        if (kind == DanmakuMatchKind.Regex) try { _ = CreateRegex(pattern); } catch (ArgumentException) { return "正则表达式无效"; }
        return null;
    }
    private static Regex CreateRegex(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(25));
}
