using System.Text.Json;

namespace ARC.Launcher.Core;

public sealed class LocalInstanceService(MinecraftRoot root)
{
    private static readonly string[] DataDirectories = ["mods", "shaderpacks", "resourcepacks", "saves", "config"];

    public ScanReport Scan()
    {
        var issues = new List<string>();
        var instances = new List<GameInstance>();
        foreach (var directory in new[] { "versions", "assets", "libraries" })
            if (!Directory.Exists(root.Resolve(directory))) issues.Add($"缺少目录：{directory}");
        var versions = root.Resolve("versions");
        if (!Directory.Exists(versions)) return new(root.RootPath, instances, issues);
        foreach (var directory in Directory.EnumerateDirectories(versions).Order(StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(directory);
            try { instances.Add(ReadInstance(name)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { issues.Add($"{name}：{ex.Message}"); }
        }
        return new(root.RootPath, instances, issues);
    }

    public GameInstance GetInstance(string name)
    {
        if (!Directory.Exists(root.Resolve("versions", name)))
            throw new DirectoryNotFoundException($"实例不存在：{name}");
        return ReadInstance(name);
    }

    public DirectoryLocation GetDirectory(string name, GameDirectory kind)
    {
        var instance = GetInstance(name);
        string[] parts = instance.Layout == InstanceLayout.Isolated ? ["versions", name] : [];
        var leaf = kind switch
        {
            GameDirectory.Game => null,
            GameDirectory.Mods => "mods",
            GameDirectory.Shaders => "shaderpacks",
            GameDirectory.ResourcePacks => "resourcepacks",
            GameDirectory.Saves => "saves",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var path = root.Resolve(leaf is null ? parts : [.. parts, leaf]);
        return new(kind, path, Directory.Exists(path));
    }

    private GameInstance ReadInstance(string name)
    {
        var metadata = root.Resolve("versions", name, name + ".json");
        var jar = root.Resolve("versions", name, name + ".jar");
        var issues = new List<string>();
        string? id = null, mainClass = null, inherits = null, minecraft = null;
        var loader = "Unknown";
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(metadata));
            var json = document.RootElement;
            if (json.ValueKind != JsonValueKind.Object) throw new JsonException("根节点必须是对象");
            id = String(json, "id");
            mainClass = String(json, "mainClass");
            inherits = String(json, "inheritsFrom");
            minecraft = String(json, "clientVersion") ?? inherits;
            var libraries = json.TryGetProperty("libraries", out var libs) && libs.ValueKind == JsonValueKind.Array
                ? libs.EnumerateArray().Select(l => String(l, "name") ?? "").ToArray() : [];
            loader = libraries.Any(l => l.StartsWith("net.fabricmc:fabric-loader:", StringComparison.Ordinal)) ? "Fabric"
                : libraries.Any(l => l.StartsWith("net.minecraftforge:forge:", StringComparison.Ordinal) || l.StartsWith("net.minecraftforge:fmlloader:", StringComparison.Ordinal)) ? "Forge"
                : libraries.Any(l => l.StartsWith("net.neoforged:neoforge:", StringComparison.Ordinal)) ? "NeoForge"
                : libraries.Any(l => l.StartsWith("org.quiltmc:quilt-loader:", StringComparison.Ordinal)) ? "Quilt" : "Vanilla";
            if (json.TryGetProperty("patches", out var patches) && patches.ValueKind == JsonValueKind.Array)
                foreach (var patch in patches.EnumerateArray())
                    if (String(patch, "id") == "game") minecraft = String(patch, "version") ?? minecraft;
            if (minecraft is null && json.TryGetProperty("arguments", out var args) &&
                args.TryGetProperty("game", out var game) && game.ValueKind == JsonValueKind.Array)
            {
                var strings = game.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()).ToArray();
                var index = Array.IndexOf(strings, "--fml.mcVersion");
                if (index >= 0 && index + 1 < strings.Length) minecraft = strings[index + 1];
            }
            if (id is null) issues.Add("版本 JSON 缺少 id");
            if (mainClass is null && inherits is null) issues.Add("版本 JSON 缺少 mainClass");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        { issues.Add($"无法读取版本 JSON：{ex.Message}"); }
        if (!File.Exists(jar)) issues.Add(inherits is null ? "缺少本地客户端 JAR" : "本地无 JAR，后续需解析继承版本");
        var isolated = DataDirectories.Any(d => Directory.Exists(root.Resolve("versions", name, d)));
        return new(name, id, minecraft, loader, mainClass, inherits, metadata, File.Exists(jar),
            isolated ? InstanceLayout.Isolated : InstanceLayout.Shared, issues);
    }

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}
