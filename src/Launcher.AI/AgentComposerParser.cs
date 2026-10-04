namespace Launcher.AI;

public sealed record AgentComposerToken(char Prefix, string Query, int Start, int Length);
public static class AgentComposerParser
{
    public static AgentComposerToken? AtCaret(string input, int caret)
    {
        caret = Math.Clamp(caret, 0, input.Length);
        var start = input.LastIndexOfAny(['/', '@'], Math.Max(0, caret - 1), caret);
        if (start < 0 || (start > 0 && !char.IsWhiteSpace(input[start - 1]))) return null;
        var prefix = input[start]; var query = input[(start + 1)..caret];
        if (query.Length > 100 || query.Contains('\n') || query.Contains('」') || query.Contains('「')) return null;
        if (prefix == '/' && (input[..start].Trim().Length > 0 || query.Any(char.IsWhiteSpace))) return null;
        return new(prefix, query, start, caret - start);
    }
    public static bool Matches(AgentReference reference, string query) => string.IsNullOrWhiteSpace(query) ||
        (reference.Name + " " + reference.Detail + " " + reference.KindLabel).Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);
}
