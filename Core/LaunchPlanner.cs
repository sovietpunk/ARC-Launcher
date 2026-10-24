using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ARC.Launcher.Core;

public sealed record LaunchIssue(string Code, string Message);
public sealed record NativeArchive(string Path, IReadOnlyList<string> Excludes);
public sealed record LaunchCheck(string InstanceName, bool DependenciesReady, bool CanLaunch,
    int RequiredJavaMajor, JavaInstallation? Java, LoginState Authentication,
    int CheckedFiles, IReadOnlyList<LaunchIssue> Issues, LaunchIdentityInfo? Identity = null);
public sealed record LaunchPreview(LaunchCheck Check, string RunId, string WorkingDirectory,
    string NativesDirectory, string MainClass, IReadOnlyList<string> Arguments,
    IReadOnlyList<string> ClassPath, IReadOnlyList<string> ModulePath, IReadOnlyList<string> InheritanceChain);

// Raw arguments never cross the preview/serialization boundary.
internal sealed record PreparedLaunch(LaunchPreview Preview, IReadOnlyList<NativeArchive> NativeArchives, IReadOnlyList<string> RawArguments);

public sealed class LaunchPlanner(MinecraftRoot root, LocalInstanceService instances)
{
    public LaunchPreview Preview(string name, JavaInstallation? java, LoginState login, OfflineIdentity? offline = null) => Build(name, java, login, offline).Preview;

    internal PreparedLaunch Build(string name, JavaInstallation? java, LoginState login, OfflineIdentity? offline = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("本阶段仅支持 Windows。");
        var resolved = new VersionResolver(root).Resolve(name);
        var metadata = resolved.Metadata;
        var issues = new List<LaunchIssue>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var classpath = new List<string>();
        var natives = new List<NativeArchive>();
        int checkedFiles = 0;
        void Require(string path, string kind)
        {
            root.ValidateContained(path);
            if (!seen.Add(path)) return;
            checkedFiles++;
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                issues.Add(new("MissingDependency", $"{kind} 缺失或为空：{path}"));
        }
        var major = RequiredJavaMajor(metadata, instances.GetInstance(name).MinecraftVersion);
        if (major is not (17 or 21)) issues.Add(new("UnsupportedJavaRequirement", $"此阶段仅支持 Java 17/21；JSON 要求 {major}。"));
        if (java is null) issues.Add(new("JavaMissing", $"未找到合适的 Java {major}，可使用 --java 指定；不会自动安装。"));
        else if (java.MajorVersion != major || java.Architecture != JavaService.NormalizeArchitecture(System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString()))
            issues.Add(new("JavaMismatch", $"要求 Java {major} 和系统架构匹配，所选为 {java.MajorVersion}/{java.Architecture}。"));
        var context = PlatformContext.Windows(java?.Architecture ?? JavaService.NormalizeArchitecture(System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString()));
        var gameDir = instances.GetDirectory(name, GameDirectory.Game).Path;
        if (!Directory.Exists(gameDir)) issues.Add(new("GameDirectoryMissing", "有效 gameDir 不存在。"));
        if (root.RootPath.Contains(';')) issues.Add(new("UnsupportedPath", "Java classpath 不支持包含分号的根目录。"));
        var runId = Guid.NewGuid().ToString("N");
        var nativesDir = root.Resolve(".arc-launcher", "v1", "runs", runId, "natives");
        foreach (var library in metadata["libraries"] as JsonArray ?? [])
        {
            if (library is null || !VersionRules.Allows(library, context)) continue;
            var coordinate = VersionResolver.Text(library, "name") ?? throw new InvalidDataException("library 缺少 Maven 坐标。");
            if (!NativeMatchesArchitecture(coordinate, context.Architecture)) continue;
            var artifact = library["downloads"]?["artifact"];
            // Old native-only libraries may omit a main artifact entirely.
            if (artifact is not null || library["downloads"] is null)
            {
                var relative = VersionResolver.Text(artifact, "path") ?? MavenPath(coordinate);
                var path = root.ResolveRelative("libraries/" + relative);
                Require(path, "library");
                if (!classpath.Contains(path, StringComparer.OrdinalIgnoreCase)) classpath.Add(path);
                if (coordinate.Split(':').ElementAtOrDefault(3)?.StartsWith("natives-windows", StringComparison.Ordinal) == true)
                    natives.Add(new(path, ["META-INF/"]));
            }
            var classifier = VersionResolver.Text(library["natives"], "windows");
            if (classifier is not null)
            {
                classifier = classifier.Replace("${arch}", context.Architecture == "x86" ? "32" : "64", StringComparison.Ordinal);
                var download = library["downloads"]?["classifiers"]?[classifier];
                var path = root.ResolveRelative("libraries/" + (VersionResolver.Text(download, "path") ?? MavenPath(coordinate, classifier)));
                Require(path, "native archive");
                var exclude = (library["extract"]?["exclude"] as JsonArray ?? []).Select(e => e!.GetValue<string>()).ToArray();
                natives.Add(new(path, exclude));
            }
        }
        Require(resolved.ClientJar, "client JAR");
        classpath.Add(resolved.ClientJar);
        var assetId = VersionResolver.Text(metadata["assetIndex"], "id") ?? VersionResolver.Text(metadata, "assets");
        var gameAssets = root.Resolve("assets");
        if (assetId is null) issues.Add(new("AssetIndexMissing", "JSON 未提供资产索引 ID。"));
        else
        {
            var indexPath = root.Resolve("assets", "indexes", assetId + ".json");
            Require(indexPath, "asset index");
            if (File.Exists(indexPath))
            {
                var index = JsonNode.Parse(File.ReadAllText(indexPath)) as JsonObject ?? throw new InvalidDataException("资产索引格式错误。");
                if (index["virtual"]?.GetValue<bool>() == true) gameAssets = root.Resolve("assets", "virtual", assetId);
                foreach (var (assetName, asset) in index["objects"] as JsonObject ?? throw new InvalidDataException("资产索引缺少 objects"))
                {
                    var hash = VersionResolver.Text(asset, "hash") ?? "";
                    if (!Regex.IsMatch(hash, "^[a-fA-F0-9]{40}$")) throw new InvalidDataException("无效资源对象哈希。");
                    Require(root.Resolve("assets", "objects", hash[..2], hash), "asset object");
                    if (index["virtual"]?.GetValue<bool>() == true) Require(root.ResolveRelative("assets/virtual/" + assetId + "/" + assetName), "virtual asset");
                    if (index["map_to_resources"]?.GetValue<bool>() == true) Require(root.ValidateContained(Path.Combine(gameDir, "resources", assetName)), "mapped resource");
                }
            }
        }
        var substitutions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["version_name"] = VersionResolver.Text(metadata, "id") ?? name, ["version_type"] = VersionResolver.Text(metadata, "type") ?? "release",
            ["game_directory"] = gameDir, ["assets_root"] = root.Resolve("assets"), ["game_assets"] = gameAssets,
            ["assets_index_name"] = assetId ?? "<missing asset index>", ["natives_directory"] = nativesDir,
            ["library_directory"] = root.Resolve("libraries"), ["classpath"] = string.Join(';', classpath),
            ["classpath_separator"] = ";", ["launcher_name"] = "ARC", ["launcher_version"] = "0.2.0"
        };
        foreach (var key in CredentialKeys) substitutions[key] = "${" + key + "}";
        var jvm = VersionRules.Arguments(metadata["arguments"]?["jvm"] as JsonArray, context).ToList();
        if (metadata["arguments"]?["jvm"] is null)
            jvm.AddRange(["-Djava.library.path=${natives_directory}", "-cp", "${classpath}"]);
        var game = metadata["arguments"]?["game"] is JsonArray modern
            ? VersionRules.Arguments(modern, context)
            : VersionRules.SplitLegacy(VersionResolver.Text(metadata, "minecraftArguments") ?? throw new InvalidDataException("版本缺少游戏参数。"));
        if (metadata["logging"]?["client"] is JsonObject logging)
        {
            var logId = VersionResolver.Text(logging["file"], "id") ?? throw new InvalidDataException("logging 缺少文件 ID。");
            var logPath = root.Resolve("assets", "log_configs", logId);
            Require(logPath, "logging config");
            if (File.Exists(logPath) && VersionResolver.Text(logging["file"], "sha1") is { } expectedHash)
            {
                using var logFile = File.OpenRead(logPath);
                if (!Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(logFile)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    issues.Add(new("LoggingConfigurationHashMismatch", "日志配置 SHA1 与版本 JSON 不符。"));
            }
            var argument = VersionResolver.Text(logging, "argument") ?? throw new InvalidDataException("logging 缺少 argument。");
            jvm.Add(argument.Replace("${path}", logPath, StringComparison.Ordinal));
        }
        string Expand(string argument) => Regex.Replace(argument, @"\$\{([^}]+)\}", match =>
        {
            if (substitutions.TryGetValue(match.Groups[1].Value, out var replacement)) return replacement;
            issues.Add(new("UnknownPlaceholder", $"无法解析占位符：{match.Value}"));
            return match.Value;
        });
        var expandedJvm = jvm.Select(Expand).ToArray();
        var expandedGame = game.Select(Expand).ToArray();
        if (offline is not null && expandedGame.Any(a => new[] { "--server", "--port", "--quickPlayMultiplayer", "--quickPlayRealms" }
            .Any(option => a.Equals(option, StringComparison.OrdinalIgnoreCase) || a.StartsWith(option + "=", StringComparison.OrdinalIgnoreCase))))
            issues.Add(new("OfflineMultiplayerNotSupported", "Offline 测试不允许版本参数自动连接多人服务器或 Realms。"));
        var mainClass = VersionResolver.Text(metadata, "mainClass") ?? throw new InvalidDataException("版本缺少 mainClass。");
        if (!Regex.IsMatch(mainClass, @"^[\w.$]+$")) throw new InvalidDataException("mainClass 不是合法 Java 类名。");
        var modulepath = new List<string>();
        for (var i = 0; i < expandedJvm.Length; i++)
        {
            var arg = expandedJvm[i];
            if (arg.StartsWith('@')) issues.Add(new("UnsupportedArgumentFile", "不执行版本 JSON 中的外部 JVM 参数文件。"));
            string? paths = null;
            var module = false;
            if (arg is "-p" or "--module-path" or "--upgrade-module-path" or "-cp" or "-classpath" or "--class-path")
            {
                module = arg is "-p" or "--module-path" or "--upgrade-module-path";
                if (++i >= expandedJvm.Length) throw new InvalidDataException("JVM 路径参数缺少值。");
                paths = expandedJvm[i];
            }
            else if (arg.StartsWith("--module-path=", StringComparison.Ordinal) || arg.StartsWith("--upgrade-module-path=", StringComparison.Ordinal) || arg.StartsWith("--class-path=", StringComparison.Ordinal))
            { paths = arg[(arg.IndexOf('=') + 1)..]; module = !arg.StartsWith("--class-path=", StringComparison.Ordinal); }
            if (paths is null) continue;
            foreach (var part in paths.Split(';'))
            {
                var path = root.ValidateContained(Path.IsPathRooted(part) ? part : Path.Combine(gameDir, part));
                Require(path, module ? "module path" : "classpath");
                if (module) modulepath.Add(path);
            }
        }
        if (offline is null && (!login.IsAuthenticated || login.ExpiresAt is null || login.ExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(30)))
            issues.Add(new("AuthenticationRequired", login.IsAuthenticated ? "认证登录状态缺少有效的到期时间。" : login.Status));
        var dependenciesReady = issues.All(i => i.Code == "AuthenticationRequired");
        var identity = offline?.Info ?? new LaunchIdentityInfo(LaunchIdentityKind.Microsoft, null, null, login.IsAuthenticated, login.Status);
        var check = new LaunchCheck(name, dependenciesReady, issues.Count == 0, major, java, login, checkedFiles, issues.Distinct().ToArray(), identity);
        var args = expandedJvm.Concat([mainClass]).Concat(expandedGame).ToArray();
        return new(new(check, runId, gameDir, nativesDir, mainClass, RedactArguments(args), classpath, modulepath, resolved.InheritanceChain), natives, args);
    }

    internal static readonly string[] CredentialKeys = ["auth_player_name", "auth_uuid", "auth_access_token", "auth_session", "clientid", "auth_xuid", "user_type", "user_properties"];

    internal static int RequiredJavaMajor(JsonObject metadata, string? fallbackMinecraft)
    {
        if (metadata["javaVersion"]?["majorVersion"] is JsonValue declared) return declared.GetValue<int>();
        var minecraft = VersionResolver.Text(metadata, "clientVersion") ?? fallbackMinecraft;
        if (!Version.TryParse(minecraft, out var version)) return 0;
        return version >= new Version(1, 20, 5) ? 21 : version >= new Version(1, 18) ? 17 : version >= new Version(1, 17) ? 16 : 8;
    }

    public static IReadOnlyList<string> RedactArguments(IEnumerable<string> arguments)
    {
        var result = arguments.ToArray();
        for (var i = 0; i < result.Length; i++)
        {
            // Keep symbolic authentication placeholders, never present literal credentials.
            if (i > 0 && Regex.IsMatch(result[i - 1], @"(?i)^--(?:accessToken|clientId|xuid|uuid|username|userProperties|session)$"))
                result[i] = Regex.IsMatch(result[i], @"^\$\{(?:auth_\w+|clientid|user_properties)\}$") ? result[i] : "[REDACTED]";
            else result[i] = SecretRedactor.RedactPatterns(result[i]);
        }
        return result;
    }

    public static string MavenPath(string coordinate, string? classifierOverride = null)
    {
        var extensionParts = coordinate.Split('@');
        var parts = extensionParts[0].Split(':');
        if (parts.Length is < 3 or > 4 || parts.Any(p => !Regex.IsMatch(p, @"^[\w.\-+]+$")) || parts[0].Split('.').Any(p => p is "" or "." or ".."))
            throw new InvalidDataException("非法 Maven 坐标。");
        var classifier = classifierOverride ?? parts.ElementAtOrDefault(3);
        var extension = extensionParts.ElementAtOrDefault(1) ?? "jar";
        if (!Regex.IsMatch(extension, @"^\w+$")) throw new InvalidDataException("非法 Maven 扩展名。");
        return $"{parts[0].Replace('.', '/')}/{parts[1]}/{parts[2]}/{parts[1]}-{parts[2]}{(classifier is null ? "" : "-" + classifier)}.{extension}";
    }

    private static bool NativeMatchesArchitecture(string coordinate, string architecture)
    {
        var classifier = coordinate.Split(':').ElementAtOrDefault(3)?.Split('@')[0];
        if (classifier?.StartsWith("natives-", StringComparison.Ordinal) != true) return true;
        return classifier == architecture switch
        { "x86" => "natives-windows-x86", "aarch64" => "natives-windows-arm64", _ => "natives-windows" };
    }
}
