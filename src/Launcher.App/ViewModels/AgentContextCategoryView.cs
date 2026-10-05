using System.Globalization;
using Launcher.AI;

namespace Launcher.App.ViewModels;

public sealed record AgentContextCategoryView(string Name, long Tokens, double Percent, string Color)
{
    public string TokenLabel => AgentContextUsage.FormatTokens(Tokens);
    public string PercentLabel => FormatPercent(Percent);
    public static string FormatPercent(double percent) => percent > 0 && percent < .1 ? "<0.1%" : percent.ToString("0.#", CultureInfo.InvariantCulture) + "%";
    private static readonly string[] Palette = ["#007AFF", "#AF52DE", "#30B89A", "#E69039", "#DF6685", "#7574D8", "#339EB8"];
    public static IReadOnlyList<AgentContextCategoryView> Create(AgentContextUsage usage)
    {
        var total = Math.Max(0, usage.Capacity);
        return usage.Categories.Select((category, index) => new AgentContextCategoryView(category.Name, category.Tokens,
            total > 0 ? 100d * Math.Max(0, category.Tokens) / total : 0, Palette[index % Palette.Length])).ToArray();
    }
}
