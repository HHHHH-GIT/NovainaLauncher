namespace Launcher.AI;

public static class ModDevelopmentGuides
{
    public static string Read(string topic)
    {
        var assembly = typeof(ModDevelopmentGuides).Assembly;
        using var stream = assembly.GetManifestResourceStream("Launcher.AI.Workflows." + topic + ".md") ?? throw new ArgumentException("未知指南");
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
}
