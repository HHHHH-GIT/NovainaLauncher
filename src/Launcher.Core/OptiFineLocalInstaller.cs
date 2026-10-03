using System.IO.Compression;
using System.Text.Json.Nodes;
using Optifine.Installer;

namespace Launcher.Core;

/// <summary>Local-JAR adapter for the MIT Optifine.Installer patcher; no uncontrolled download entry.</summary>
public static class OptiFineLocalInstaller
{
    public static async Task<string> InstallAsync(string root, OptiFineCatalogVersion version, string localJar, CancellationToken token)
    {
        var mc = version.MinecraftVersion; var edition = version.Edition;
        var info = new OptifineVersion(mc, edition, version.ForgeVersion ?? "", false, DateTime.UtcNow);
        var libraryDirectory = Path.Combine(root, "libraries", "optifine", "OptiFine", mc + "_" + edition);
        Directory.CreateDirectory(libraryDirectory);
        await Task.Run(() => Patcher.Process(new FileInfo(Path.Combine(root, "versions", mc, mc + ".jar")), new FileInfo(localJar), new FileInfo(Path.Combine(libraryDirectory, $"OptiFine-{mc}_{edition}.jar")), Utils.IsNewVersion(info), token), token);
        token.ThrowIfCancellationRequested();
        var id = mc + "-OptiFine_" + edition;
        var libraries = new JsonArray(new JsonObject { ["name"] = $"optifine:OptiFine:{mc}_{edition}" });
        using var jar = ZipFile.OpenRead(localJar);
        var args = new JsonObject { ["game"] = new JsonArray("--tweakClass", "optifine.OptiFineTweaker") };
        var profile = new JsonObject { ["id"] = id, ["inheritsFrom"] = mc, ["type"] = "release", ["mainClass"] = "net.minecraft.launchwrapper.Launch", ["libraries"] = libraries, ["arguments"] = args };
        if (Utils.IsLegacyVersion(info))
        {
            libraries.Add(new JsonObject { ["name"] = "net.minecraft:launchwrapper:" + Utils.GetLaunchwrapperVersionLegacy(info) });
            var vanilla = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "versions", mc, mc + ".json"), token))!;
            profile.Remove("arguments"); profile["minecraftArguments"] = vanilla["minecraftArguments"]?.GetValue<string>() + " --tweakClass optifine.OptiFineTweaker";
        }
        else
        {
            var entry = jar.GetEntry("launchwrapper-of.txt") ?? throw new InvalidDataException("OptiFine 安装格式不受支持");
            using var reader = new StreamReader(entry.Open());
            string wrapperVersion = (await reader.ReadToEndAsync(token)).Trim();
            string wrapperFile = "launchwrapper-of-" + wrapperVersion + ".jar";
            var wrapper = jar.GetEntry(wrapperFile) ?? throw new InvalidDataException("OptiFine 缺少启动包装器");
            var destination = Path.Combine(root, "libraries", "optifine", "launchwrapper-of", wrapperVersion, wrapperFile);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var output = File.Create(destination); await using var input = wrapper.Open(); await input.CopyToAsync(output, token);
            libraries.Add(new JsonObject { ["name"] = "optifine:launchwrapper-of:" + wrapperVersion });
        }
        var dir = Path.Combine(root, "versions", id); Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, id + ".json"), profile.ToJsonString(), token);
        return id;
    }
}
