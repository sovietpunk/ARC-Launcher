using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using ARC.Launcher.Core;

internal static class Phase2Tests
{
    public static async Task RunAsync(string basePath, Action<bool, string> check, Action<Action, string> reject)
    {
        var path = Path.Combine(basePath, "第二阶段 [Root] with spaces", ".minecraft");
        Directory.CreateDirectory(path);
        var root = new MinecraftRoot(path);
        var instances = new LocalInstanceService(root);
        var resolver = new VersionResolver(root);
        void WriteVersion(string name, string json, bool jar = false)
        {
            var directory = root.Resolve("versions", name);
            Directory.CreateDirectory(directory);
            File.WriteAllText(root.Resolve("versions", name, name + ".json"), json);
            if (jar) File.WriteAllText(root.Resolve("versions", name, name + ".jar"), "synthetic client");
        }
        void WriteRelative(string relative, string content)
        {
            var target = root.ResolveRelative(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, content);
        }
        WriteVersion("base", """
        {"id":"base","clientVersion":"1.20.1","type":"release","mainClass":"example.Main","javaVersion":{"majorVersion":17},
         "assetIndex":{"id":"test"},"libraries":[{"name":"example:library:1"}],
         "arguments":{"jvm":["-Djava.library.path=${natives_directory}","-cp","${classpath}"],
         "game":["--gameDir","${game_directory}","--accessToken","${auth_access_token}","--uuid","${auth_uuid}"]}}
        """, true);
        WriteVersion("子版本 [Fabric]", """
        {"id":"child","inheritsFrom":"base","mainClass":"net.fabricmc.loader.impl.launch.knot.KnotClient",
         "libraries":[{"name":"example:library:2"}],"arguments":{"jvm":["-DFabricMcEmu= net.minecraft.client.main.Main "],"game":[]}}
        """);
        Directory.CreateDirectory(root.Resolve("versions", "子版本 [Fabric]", "mods"));
        WriteRelative("libraries/example/library/2/library-2.jar", "synthetic library");
        WriteRelative("assets/indexes/test.json", "{\"objects\":{}}");
        var resolved = resolver.Resolve("子版本 [Fabric]");
        check(resolved.InheritanceChain.SequenceEqual(new[] { "base", "子版本 [Fabric]" }) && resolved.ClientJar == root.Resolve("versions", "base", "base.jar"), "inheritance and parent client JAR");
        check(((JsonArray)resolved.Metadata["libraries"]!).Count == 1 && resolved.Metadata["libraries"]![0]!["name"]!.GetValue<string>() == "example:library:2", "child overrides Maven version");
        check(resolved.Metadata["arguments"]!["jvm"]!.AsArray().Count == 4, "JVM arguments concatenate across inheritance");
        WriteVersion("cycleA", "{\"inheritsFrom\":\"cycleB\"}");
        WriteVersion("cycleB", "{\"inheritsFrom\":\"cycleA\"}");
        reject(() => resolver.Resolve("cycleA"), "inheritance cycle rejected");
        WriteVersion("missingParent", "{\"inheritsFrom\":\"absent\"}");
        reject(() => resolver.Resolve("missingParent"), "missing inheritance parent rejected");
        WriteVersion("escapeParent", "{\"inheritsFrom\":\"..\"}");
        reject(() => resolver.Resolve("escapeParent"), "unsafe inherited version name rejected");
        reject(() => root.ResolveRelative("libraries/../../outside.jar"), "artifact traversal rejected");
        var windows = PlatformContext.Windows("amd64");
        bool Allows(string json) => VersionRules.Allows(JsonNode.Parse(json), windows);
        check(Allows("{}") && !Allows("{\"rules\":[]}"), "library rules default semantics");
        check(Allows("{\"rules\":[{\"action\":\"allow\",\"os\":{\"name\":\"windows\",\"arch\":\"amd64\"}}]}") &&
            !Allows("{\"rules\":[{\"action\":\"allow\",\"os\":{\"name\":\"linux\"}}]}"), "OS and architecture rules");
        check(!Allows("{\"rules\":[{\"action\":\"allow\"},{\"action\":\"disallow\",\"os\":{\"name\":\"windows\"}}]}"), "last matching rule wins");
        check(!Allows("{\"rules\":[{\"action\":\"allow\",\"features\":{\"is_demo_user\":true}}]}"), "demo arguments never enabled");
        var conditional = JsonNode.Parse("""
        ["always",{"rules":[{"action":"allow","os":{"name":"windows"}}],"value":["--value","with spaces"]},
        {"rules":[{"action":"allow","features":{"is_demo_user":true}}],"value":"--demo"}]
        """)!.AsArray();
        check(VersionRules.Arguments(conditional, windows).SequenceEqual(new[] { "always", "--value", "with spaces" }), "conditional argument arrays preserve single arguments");
        var java = JavaService.ParseSettings("synthetic-java.exe", "java.version = 17.0.9\nos.arch = amd64\n");
        check(java.MajorVersion == 17 && java.Architecture == "amd64", "Java 17 properties parsed");
        check(JavaService.ParseSettings("synthetic-java.exe", "java.version = 21.0.1\nos.arch = aarch64").MajorVersion == 21 &&
            JavaService.ParseSettings("synthetic-java.exe", "java.version = 1.8.0_202\nos.arch = x86").MajorVersion == 8, "Java modern and legacy versions parsed");
        reject(() => JavaService.ParseSettings("synthetic-java.exe", "bad output"), "unrecognized Java probe rejected");
        var login = new LoginState(false, "认证未接入");
        var planner = new LaunchPlanner(root, instances);
        var preview = planner.Preview("子版本 [Fabric]", java, login);
        check(preview.Check.DependenciesReady && !preview.Check.CanLaunch && preview.Check.Issues.Single().Code == "AuthenticationRequired", "dependencies valid but authentication blocks launch");
        check(preview.Arguments.Contains(preview.WorkingDirectory) && preview.Arguments.Contains("-DFabricMcEmu= net.minecraft.client.main.Main "), "Chinese gameDir and Fabric JVM argument preserved");
        var cpIndex = preview.Arguments.ToList().IndexOf("-cp");
        check(preview.Arguments[cpIndex + 1] == string.Join(';', preview.ClassPath) && preview.ClassPath.Count == 2, "classpath is one argument with inherited client");
        check(preview.Arguments.Contains("${auth_access_token}") && !Directory.Exists(root.Resolve(".arc-launcher")), "preview keeps symbolic credentials and performs no cache writes");
        var wrongJava = planner.Preview("子版本 [Fabric]", java with { MajorVersion = 21 }, login);
        check(wrongJava.Check.Issues.Any(i => i.Code == "JavaMismatch"), "incorrect explicit Java rejected");
        WriteVersion("forge", """
        {"id":"forge","inheritsFrom":"base","mainClass":"cpw.mods.bootstraplauncher.BootstrapLauncher",
         "arguments":{"jvm":["-p","${library_directory}/example/module/1/module-1.jar","--add-modules","ALL-MODULE-PATH","--add-opens","java.base/java.lang.invoke=cpw.mods.securejarhandler"],
         "game":["--launchTarget","forgeclient","--fml.mcVersion","1.20.1"]}}
        """);
        WriteRelative("libraries/example/library/1/library-1.jar", "library");
        WriteRelative("libraries/example/module/1/module-1.jar", "module");
        var forge = planner.Preview("forge", java, login);
        check(forge.ModulePath.Single() == root.ResolveRelative("libraries/example/module/1/module-1.jar") && forge.Arguments.Contains("forgeclient") && forge.Arguments.Contains("--add-opens"), "Forge module path and loader arguments preserved");
        WriteVersion("legacy", """
        {"id":"legacy","inheritsFrom":"base","minecraftArguments":"--gameDir \"${game_directory}\" --accessToken ${auth_access_token}","arguments":null}
        """);
        var legacy = planner.Preview("legacy", java, login);
        check(legacy.Arguments.Contains(legacy.WorkingDirectory) && legacy.Arguments.Contains("-cp"), "legacy arguments and minimal legacy JVM defaults");
        WriteVersion("legacyOverride", """
        {"inheritsFrom":"base","minecraftArguments":"--gameDir ${game_directory} --legacy-marker"}
        """);
        check(planner.Preview("legacyOverride", java, login).Arguments.Contains("--legacy-marker"), "legacy child overrides inherited modern game arguments");
        WriteVersion("mixedParent", """
        {"inheritsFrom":"base","minecraftArguments":"--legacy-parent","arguments":null}
        """);
        WriteVersion("mixedChild", """
        {"inheritsFrom":"mixedParent","arguments":{"game":["--modern-child"]}}
        """);
        var mixed = planner.Preview("mixedChild", java, login);
        check(mixed.Arguments.Contains("--legacy-parent") && mixed.Arguments.Contains("--modern-child"), "modern child includes legacy parent game arguments");
        WriteVersion("unknownPlaceholder", """
        {"inheritsFrom":"base","arguments":{"jvm":["-Dunknown=${unsupported_placeholder}"]}}
        """);
        check(planner.Preview("unknownPlaceholder", java, login).Check.Issues.Any(i => i.Code == "UnknownPlaceholder"), "unknown placeholder prevents launch");
        WriteVersion("missingModule", """
        {"inheritsFrom":"base","arguments":{"jvm":["--module-path=${library_directory}/absent.jar"]}}
        """);
        check(planner.Preview("missingModule", java, login).Check.Issues.Any(i => i.Code == "MissingDependency"), "missing module path prevents launch");
        reject(() => VersionRules.SplitLegacy("--foo \"unclosed"), "legacy unclosed quotes rejected");
        check(VersionRules.SplitLegacy("--foo \"hello world\" --empty \"\"").SequenceEqual(new[] { "--foo", "hello world", "--empty", "" }), "legacy quoted and empty arguments");
        WriteVersion("nativeArch", """
        {"inheritsFrom":"base","libraries":[{"name":"org.lwjgl:lwjgl:1:natives-windows"},{"name":"org.lwjgl:lwjgl:1:natives-windows-x86"},{"name":"org.lwjgl:lwjgl:1:natives-windows-arm64"}]}
        """);
        WriteRelative("libraries/org/lwjgl/lwjgl/1/lwjgl-1-natives-windows.jar", "synthetic archive (preview only)");
        var nativeArch = planner.Preview("nativeArch", java, login);
        check(nativeArch.ClassPath.Count(p => p.Contains("natives-windows")) == 1 && nativeArch.Check.DependenciesReady, "Windows native artifact architecture filtering");
        var redactor = new SecretRedactor(["redaction-secret-marker"]);
        check(!redactor.Redact("Bearer redaction-secret-marker access_token=redaction-secret-marker").Contains("redaction-secret-marker"), "known secrets redacted");
        check(!SecretRedactor.RedactPatterns("{\"access_token\":\"sensitive-marker\"}").Contains("sensitive-marker"), "JSON credential patterns redacted");
        check(LaunchPlanner.RedactArguments(["--accessToken", "sensitive-marker", "--username", "sensitive-name"])[1] == "[REDACTED]", "literal credential arguments redacted");
        var launcher = new LaunchService(root, instances, new JavaService(root), new UnavailableAuthenticationService());
        try { await launcher.RunAsync(new("子版本 [Fabric]", root.Resolve("missing-java.exe"))); throw new Exception("Expected launch blocked"); }
        catch (LaunchBlockedException ex) { check(ex.Check.Issues.Any(i => i.Code == "AuthenticationRequired") && !Directory.Exists(root.Resolve(".arc-launcher")), "blocked run creates no caches and no process"); }

        var archivePath = root.Resolve("synthetic-native.zip");
        using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("native.dll").Open())) writer.Write("synthetic dll");
            using (var writer = new StreamWriter(zip.CreateEntry("META-INF/ignored.dll").Open())) writer.Write("excluded");
        }
        var nativeDestination = root.Resolve("native-test");
        NativeExtractor.Extract(root, [new(archivePath, ["META-INF/"])], nativeDestination);
        check(File.Exists(Path.Combine(nativeDestination, "native.dll")) && !Directory.Exists(Path.Combine(nativeDestination, "META-INF")), "native extraction and exclusions");
        reject(() => NativeExtractor.Extract(root, [new(archivePath, [])], nativeDestination), "native extraction never overwrites existing destination");
        WriteRelative("libraries/native/test.jar", "placeholder");
        File.Copy(archivePath, root.ResolveRelative("libraries/native/archive.jar"));
        WriteVersion("classicNatives", """
        {"inheritsFrom":"base","libraries":[{"name":"example:classic:1","downloads":{"classifiers":{"natives-windows-64":{"path":"native/archive.jar"}}},"natives":{"windows":"natives-windows-${arch}"},"extract":{"exclude":["META-INF/"]}}]}
        """);
        check(planner.Preview("classicNatives", java, login).Check.DependenciesReady, "classic natives classifier and arch substitution");
        var badZip = root.Resolve("bad-native.zip");
        using (var zip = ZipFile.Open(badZip, ZipArchiveMode.Create)) { using var writer = new StreamWriter(zip.CreateEntry("../escape.dll").Open()); writer.Write("bad"); }
        reject(() => NativeExtractor.Extract(root, [new(badZip, [])], root.Resolve("bad-native-output")), "ZIP traversal rejected");
        check(!File.Exists(root.Resolve("escape.dll")), "ZIP traversal creates no escaped file");

        var processes = new GameProcessService(root);
        check(processes.Status().Processes.Count == 0 && !Directory.Exists(root.Resolve(".arc-launcher")), "status query performs no writes");
        var release = root.Resolve("fixture-release.txt");
        var logs = new List<LaunchLog>();
        var logLock = new object();
        string[] fixtureArgs = ["--process-fixture", release, "中文 参数 [空格]"];
        var childArgs = string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)
            ? new[] { typeof(Phase2Tests).Assembly.Location }.Concat(fixtureArgs).ToArray() : fixtureArgs;
        var running = processes.Start(Environment.ProcessPath!, childArgs, preview.WorkingDirectory, Guid.NewGuid().ToString("N"), redactor,
            entry => { lock (logLock) logs.Add(entry); });
        try
        {
            check(processes.Status().Processes.Single().State == "Running", "cross-service process status while running");
            reject(() => new GameProcessService(root).Start(Environment.ProcessPath!, childArgs, preview.WorkingDirectory + Path.DirectorySeparatorChar, Guid.NewGuid().ToString("N"), redactor), "duplicate gameDir process rejected (including trailing separator)");
        }
        finally { File.WriteAllText(release, "release"); }
        var exited = await running.Completion.WaitAsync(TimeSpan.FromSeconds(15));
        check(exited.State == "Exited" && exited.ExitCode == 7 && processes.Status().Processes.Single().ExitCode == 7, "asynchronous process exit and persisted exit code");
        var output = logs.Single(l => l.Stream == "stdout" && l.Message.StartsWith('{')).Message;
        using var result = JsonDocument.Parse(output);
        check(result.RootElement.GetProperty("Argument").GetString() == "中文 参数 [空格]" && result.RootElement.GetProperty("WorkingDirectory").GetString() == preview.WorkingDirectory, "ArgumentList preserves path characters and effective working directory");
        check(logs.Any(l => l.Stream == "stderr") && logs.All(l => !l.Message.Contains("redaction-secret-marker")), "stdout and stderr are asynchronously drained and redacted");
        check(exited.MainMenuState == "Unverified", "process exit never implies main-menu success");
        var persistedLogs = File.ReadAllText(exited.LogPath!);
        check(exited.LogCaptureState == "Completed" && !persistedLogs.Contains("redaction-secret-marker") && persistedLogs.Contains("stderr"), "persistent process logs are redacted and include stderr");
        var restarted = processes.Start(Environment.ProcessPath!, childArgs, preview.WorkingDirectory, Guid.NewGuid().ToString("N"), redactor);
        check((await restarted.Completion).ExitCode == 7, "completed gameDir lease permits a later launch");
        var foreignRoot = new MinecraftRoot(Path.Combine(basePath, "foreign-cache-test"));
        Directory.CreateDirectory(foreignRoot.Resolve(".arc-launcher", "v1"));
        File.WriteAllText(foreignRoot.Resolve(".arc-launcher", "v1", "owner.txt"), "user-owned-marker");
        reject(() => new GameProcessService(foreignRoot).Start(Environment.ProcessPath!, childArgs, foreignRoot.RootPath, Guid.NewGuid().ToString("N"), redactor), "foreign runtime cache is never overwritten");
        check(File.ReadAllText(foreignRoot.Resolve(".arc-launcher", "v1", "owner.txt")) == "user-owned-marker", "foreign runtime marker remains unchanged");
    }
}
