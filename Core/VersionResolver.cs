using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ARC.Launcher.Core;

public sealed record ResolvedVersion(string Name, JsonObject Metadata, string ClientJar,
    IReadOnlyList<string> InheritanceChain);

public sealed class VersionResolver(MinecraftRoot root)
{
    public ResolvedVersion Resolve(string name) => Resolve(name, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private ResolvedVersion Resolve(string name, HashSet<string> visiting)
    {
        var path = root.Resolve("versions", name, name + ".json");
        if (!visiting.Add(name)) throw new InvalidDataException("版本继承存在循环。");
        if (visiting.Count > 64) throw new InvalidDataException("版本继承超过 64 层。");
        var child = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidDataException("版本 JSON 必须为对象。");
        var parentName = Text(child, "inheritsFrom");
        ResolvedVersion? parent = parentName is null ? null : Resolve(parentName, visiting);
        var merged = parent is null ? (JsonObject)child.DeepClone() : Merge(parent.Metadata, child);
        var ownJar = root.Resolve("versions", name, name + ".jar");
        var jarId = Text(child, "jar");
        var jar = jarId is not null ? root.Resolve("versions", jarId, jarId + ".jar")
            : File.Exists(ownJar) || child["downloads"]?["client"] is not null || parent is null ? ownJar : parent.ClientJar;
        visiting.Remove(name);
        return new(name, merged, jar, [.. parent?.InheritanceChain ?? [], name]);
    }

    private static JsonObject Merge(JsonObject parent, JsonObject child)
    {
        var result = (JsonObject)parent.DeepClone();
        if (parent["minecraftArguments"] is JsonValue legacy && parent["arguments"]?["game"] is null && child["arguments"]?["game"] is JsonArray)
        {
            var arguments = result["arguments"] as JsonObject ?? new JsonObject();
            arguments["game"] = new JsonArray(VersionRules.SplitLegacy(legacy.GetValue<string>()).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
            result["arguments"] = arguments;
        }
        // A complete legacy game string overrides inherited game arguments, but preserves JVM options.
        if (child["minecraftArguments"] is JsonValue && child["arguments"]?["game"] is null && result["arguments"] is JsonObject inheritedArguments)
            inheritedArguments.Remove("game");
        foreach (var (key, value) in child)
        {
            if (key == "libraries" && value is JsonArray childLibraries)
            {
                var libraries = (JsonArray?)result[key]?.DeepClone() ?? [];
                foreach (var library in childLibraries)
                {
                    var identity = LibraryIdentity(Text(library, "name") ?? throw new InvalidDataException("library 缺少 name"));
                    for (var i = libraries.Count - 1; i >= 0; i--)
                        if (LibraryIdentity(Text(libraries[i], "name") ?? "") == identity) libraries.RemoveAt(i);
                    libraries.Add(library?.DeepClone());
                }
                result[key] = libraries;
            }
            else if (key == "arguments" && value is JsonObject arguments)
            {
                var combined = result[key] as JsonObject ?? new JsonObject();
                foreach (var (kind, array) in arguments)
                    combined[kind] = new JsonArray((combined[kind] as JsonArray ?? []).Concat(array as JsonArray ?? [])
                        .Select(n => n?.DeepClone()).ToArray());
                result[key] = combined;
            }
            else if (value is JsonObject obj && result[key] is JsonObject inherited)
                result[key] = Merge(inherited, obj);
            else result[key] = value?.DeepClone();
        }
        return result;
    }

    // Version may change in a child; classifiers/extensions identify distinct artifacts.
    private static string LibraryIdentity(string name)
    {
        var extensionParts = name.Split('@');
        var parts = extensionParts[0].Split(':');
        return parts.Length >= 3 ? $"{parts[0]}:{parts[1]}:{(parts.Length > 3 ? parts[3] : "")}:{extensionParts.ElementAtOrDefault(1) ?? "jar"}" : name;
    }
    internal static string? Text(JsonNode? node, string key) => node?[key]?.GetValue<string>();
}

public sealed record PlatformContext(string Os, string Architecture, string OsVersion,
    IReadOnlyDictionary<string, bool> Features)
{
    public static PlatformContext Windows(string architecture) => new("windows", architecture,
        Environment.OSVersion.Version.ToString(), new Dictionary<string, bool>());
}

public static class VersionRules
{
    public static bool Allows(JsonNode? node, PlatformContext context)
    {
        if (node?["rules"] is not JsonArray rules) return true;
        var allowed = false;
        foreach (var rule in rules)
        {
            if (rule is null) throw new InvalidDataException("空规则");
            var os = rule["os"];
            if (os is not null &&
                ((VersionResolver.Text(os, "name") is { } name && name != context.Os) ||
                (VersionResolver.Text(os, "arch") is { } arch && !Matches(context.Architecture, arch)) ||
                (VersionResolver.Text(os, "version") is { } version && !Matches(context.OsVersion, version)))) continue;
            if (rule["features"] is JsonObject features && features.Any(pair =>
                (context.Features.TryGetValue(pair.Key, out var enabled) && enabled) != pair.Value!.GetValue<bool>())) continue;
            allowed = VersionResolver.Text(rule, "action") switch
            { "allow" => true, "disallow" => false, _ => throw new InvalidDataException("未知版本规则 action") };
        }
        return allowed;
    }

    private static bool Matches(string text, string pattern) => Regex.IsMatch(text, "^(?:" + pattern + ")$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static IReadOnlyList<string> Arguments(JsonArray? arguments, PlatformContext context)
    {
        var output = new List<string>();
        foreach (var node in arguments ?? [])
        {
            if (node is JsonValue value) output.Add(value.GetValue<string>());
            else if (node is JsonObject && Allows(node, context))
            {
                if (node["value"] is JsonArray values) output.AddRange(values.Select(v => v!.GetValue<string>()));
                else output.Add(node["value"]!.GetValue<string>());
            }
        }
        return output;
    }

    /// <summary>Tokenize legacy minecraftArguments before placeholder expansion, preserving path spaces.</summary>
    public static IReadOnlyList<string> SplitLegacy(string arguments)
    {
        var output = new List<string>();
        var token = new System.Text.StringBuilder();
        char quote = '\0';
        var started = false;
        for (var i = 0; i < arguments.Length; i++)
        {
            var c = arguments[i];
            if (c == '\\' && i + 1 < arguments.Length && arguments[i + 1] == '"') { token.Append('"'); i++; started = true; }
            else if (c is '\'' or '"' && (quote == '\0' || quote == c)) { quote = quote == '\0' ? c : '\0'; started = true; }
            else if (char.IsWhiteSpace(c) && quote == '\0') { if (started) { output.Add(token.ToString()); token.Clear(); started = false; } }
            else { token.Append(c); started = true; }
        }
        if (quote != '\0') throw new InvalidDataException("旧式启动参数引号未闭合。");
        if (started) output.Add(token.ToString());
        return output;
    }
}
