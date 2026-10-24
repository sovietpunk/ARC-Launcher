using ARC.Launcher.Core;

internal static class OfflineTests
{
    public static async Task RunAsync(string basePath, Action<bool, string> check, Action<Action, string> reject)
    {
        var identity = new OfflineIdentity("Notch");
        check(identity.Uuid == "b50ad385829d3141a2167e7d7539ba7f", "offline UUID matches Java nameUUIDFromBytes reference vector");
        check(new OfflineIdentity("Notch").Uuid == identity.Uuid && new OfflineIdentity("notch").Uuid != identity.Uuid, "offline UUID deterministic and case-sensitive");
        check(identity.Uuid[12] == '3' && "89ab".Contains(identity.Uuid[16]), "offline UUID has RFC 4122 version and variant bits");
        check(identity.Info.Kind == LaunchIdentityKind.Offline && !identity.Info.IsOnlineAuthenticated, "offline identity does not claim online authentication");
        foreach (var name in new[] { "", "name with spaces", "a-name", "abcdefghijklmnopq", "中文名称" })
            reject(() => new OfflineIdentity(name), "invalid offline username rejected: " + name);
        var bound = IdentityArgumentBinder.Bind(["--username", "${auth_player_name}", "--uuid", "${auth_uuid}",
            "--accessToken", "${auth_access_token}", "--clientId", "${clientid}", "--xuid", "${auth_xuid}",
            "--userType", "${user_type}", "--session", "${auth_session}"], identity);
        check(bound.SequenceEqual(new[] { "--username", "Notch", "--uuid", identity.Uuid, "--accessToken", "0", "--clientId", "", "--xuid", "", "--userType", "legacy", "--session", "-" }), "identity binder creates explicit offline values and preserves empty ArgumentList values");
        reject(() => IdentityArgumentBinder.Bind(["${unexpected}"], identity), "identity binder rejects unknown placeholders");
        var path = Path.Combine(basePath, "Offline 测试 [Root] with spaces", ".minecraft");
        Directory.CreateDirectory(path);
        var root = new MinecraftRoot(path);
        Directory.CreateDirectory(root.Resolve("versions", "Offline [1.20.1]", "mods"));
        File.WriteAllText(root.Resolve("versions", "Offline [1.20.1]", "Offline [1.20.1].json"), """
        {"id":"offline-test","clientVersion":"1.20.1","mainClass":"example.Main","javaVersion":{"majorVersion":17},
         "assetIndex":{"id":"test"},"libraries":[],"arguments":{"jvm":["-cp","${classpath}"],"game":["--gameDir","${game_directory}","--username","${auth_player_name}","--accessToken","${auth_access_token}"]}}
        """);
        File.WriteAllText(root.Resolve("versions", "Offline [1.20.1]", "Offline [1.20.1].jar"), "synthetic jar");
        Directory.CreateDirectory(root.Resolve("assets", "indexes"));
        File.WriteAllText(root.Resolve("assets", "indexes", "test.json"), "{\"objects\":{}}");
        var instances = new LocalInstanceService(root);
        var planner = new LaunchPlanner(root, instances);
        var java = new JavaInstallation("synthetic-java.exe", 17, "amd64", "17.0.15");
        var login = new LoginState(false, "Microsoft 未接入");
        var offline = planner.Preview("Offline [1.20.1]", java, login, identity);
        check(offline.Check.CanLaunch && offline.Check.DependenciesReady && !offline.Check.Authentication.IsAuthenticated, "offline readiness independent of Microsoft login state");
        check(!planner.Preview("Offline [1.20.1]", java, login).Check.CanLaunch, "default Microsoft launch still requires authentication");
        var noJava = planner.Preview("Offline [1.20.1]", null, login, identity);
        check(!noJava.Check.CanLaunch && noJava.Check.Issues.Any(i => i.Code == "JavaMissing"), "offline mode never skips Java validation");
        // Add a missing library, asset and logging file without removing existing fixtures.
        File.WriteAllText(root.Resolve("versions", "Offline [1.20.1]", "Offline [1.20.1].json"), """
        {"id":"offline-test","clientVersion":"1.20.1","mainClass":"example.Main","javaVersion":{"majorVersion":17},
         "assetIndex":{"id":"missing"},"libraries":[{"name":"missing:library:1"},{"name":"missing:native:1","downloads":{"classifiers":{"natives-windows":{"path":"missing/native.jar"}}},"natives":{"windows":"natives-windows"}}],
         "logging":{"client":{"file":{"id":"absent.xml"},"argument":"-Dlog4j.configurationFile=${path}"}},
         "arguments":{"jvm":["-cp","${classpath}"],"game":["--username","${auth_player_name}"]}}
        """);
        var missing = planner.Preview("Offline [1.20.1]", java, login, identity);
        check(!missing.Check.CanLaunch && missing.Check.Issues.Count(i => i.Code == "MissingDependency") == 4, "offline mode retains library, asset, natives and logging dependency checks");
        var metadataPath = root.Resolve("versions", "Offline [1.20.1]", "Offline [1.20.1].json");
        var metadata = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(metadataPath))!;
        metadata["arguments"]!["game"]!.AsArray().Add("--quickPlayMultiplayer");
        metadata["arguments"]!["game"]!.AsArray().Add("example.invalid");
        File.WriteAllText(metadataPath, metadata.ToJsonString());
        check(planner.Preview("Offline [1.20.1]", java, login, identity).Check.Issues.Any(i => i.Code == "OfflineMultiplayerNotSupported"), "offline mode refuses automatic multiplayer connection arguments");
        var launcher = new LaunchService(root, instances, new JavaService(root), new UnexpectedAuthenticationService());
        var checkResult = await launcher.CheckAsync(new("Offline [1.20.1]", root.Resolve("absent-java.exe"), identity));
        check(!checkResult.CanLaunch && checkResult.Issues.All(i => i.Code != "AuthenticationRequired"), "offline preparation does not invoke Microsoft provider");
        try { await launcher.RunAsync(new("Offline [1.20.1]", root.Resolve("absent-java.exe"), identity)); throw new Exception("Expected dependency block"); }
        catch (LaunchBlockedException) { check(!Directory.Exists(root.Resolve(".arc-launcher")), "blocked offline run has no process/cache side effects"); }
    }

    private sealed class UnexpectedAuthenticationService : IAuthenticationService
    {
        public Task<LoginState> GetStateAsync(CancellationToken cancellationToken = default) => throw new Exception("Offline must not call Microsoft authentication");
        public Task<MinecraftSession?> GetSessionAsync(CancellationToken cancellationToken = default) => throw new Exception("Offline must not request Microsoft credentials");
    }
}
