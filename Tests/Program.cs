using ARC.Launcher.Core;

if (args.FirstOrDefault() == "--process-fixture")
{
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { WorkingDirectory = Environment.CurrentDirectory, Argument = args[2], InjectedJavaOptions = Environment.GetEnvironmentVariable("JAVA_TOOL_OPTIONS") }));
    Console.WriteLine("redaction-secret-marker");
    Console.Error.WriteLine("access_token=redaction-secret-marker");
    for (var attempt = 0; attempt < 200 && !File.Exists(args[1]); attempt++) await Task.Delay(50);
    Environment.ExitCode = File.Exists(args[1]) ? 7 : 9;
    return;
}

// Synthetic files only, under a uniquely named project test directory; never use real game files.
var basePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../..", ".test-work", Guid.NewGuid().ToString("N")));
var rootPath = Path.Combine(basePath, "中文 根目录 [测试]", ".minecraft");
Directory.CreateDirectory(rootPath);
var service = new LocalInstanceService(new MinecraftRoot(rootPath));
int passed = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception("FAIL: " + label);
    Console.WriteLine("PASS: " + label);
    passed++;
}
void Reject(Action action, string label)
{
    try { action(); } catch (Exception ex) when (ex is IOException or ArgumentException or InvalidDataException) { Check(true, label); return; }
    throw new Exception("FAIL: " + label);
}
foreach (var name in new[] { "最小的机械动力", "Fabric with spaces", "Better MC [FORGE] BMC4" })
{
    var path = Path.Combine(rootPath, "versions", name);
    Directory.CreateDirectory(Path.Combine(path, "mods"));
    File.WriteAllText(Path.Combine(path, name + ".json"), "{\"id\":\"test\",\"clientVersion\":\"1.20.1\",\"mainClass\":\"example.Main\",\"libraries\":[{\"name\":\"net.fabricmc:fabric-loader:0.1\"}]}");
    File.WriteAllBytes(Path.Combine(path, name + ".jar"), []);
    var instance = service.GetInstance(name);
    Check(instance.Loader == "Fabric" && instance.MinecraftVersion == "1.20.1" && instance.Issues.Count == 0, "metadata: " + name);
    Check(service.GetDirectory(name, GameDirectory.Mods).Path == Path.Combine(path, "mods"), "literal path: " + name);
    Check(!service.GetDirectory(name, GameDirectory.Saves).Exists && !Directory.Exists(Path.Combine(path, "saves")), "missing directory remains missing: " + name);
}
Check(service.Scan().Instances.Count == 3, "scan all three path variants");
var forgePath = Path.Combine(rootPath, "versions", "Fabric with spaces", "Fabric with spaces.json");
File.WriteAllText(forgePath, "{\"id\":\"forge\",\"mainClass\":\"cpw.mods.bootstraplauncher.BootstrapLauncher\",\"libraries\":[{\"name\":\"net.minecraftforge:fmlloader:1.20.1-47.4.23\"}]}");
Check(service.GetInstance("Fabric with spaces").Loader == "Forge", "Forge merged metadata fmlloader detection");
Reject(() => service.GetInstance(".."), "reject traversal");
Reject(() => service.GetInstance("D:\\minecraft\\.minecraft"), "reject absolute path");
Reject(() => service.GetInstance("nested/child"), "reject separator");
var broken = Path.Combine(rootPath, "versions", "broken");
Directory.CreateDirectory(broken);
File.WriteAllText(Path.Combine(broken, "broken.json"), "{invalid");
Check(service.Scan().Instances.Single(i => i.Name == "broken").Issues.Count == 2, "broken metadata and missing jar are diagnostics");
var shared = Path.Combine(rootPath, "versions", "shared");
Directory.CreateDirectory(shared);
Check(service.GetDirectory("shared", GameDirectory.Game).Path == rootPath, "shared layout fallback");
Check(service.Scan().Issues.Count == 2, "missing assets and libraries reported");
// A junction does not require developer-mode symlink permissions on Windows.
if (OperatingSystem.IsWindows())
{
    var junction = Path.Combine(rootPath, "versions", "junction");
    var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var arg in new[] { "/c", "mklink", "/J", junction, broken }) start.ArgumentList.Add(arg);
    using var process = System.Diagnostics.Process.Start(start)!;
    process.WaitForExit();
    Check(process.ExitCode == 0, "create synthetic junction fixture");
    Reject(() => service.GetInstance("junction"), "reject junction");
    Check(service.Scan().Issues.Any(i => i.StartsWith("junction：")), "unsafe instance is reported");
}
Console.WriteLine($"{passed} assertions passed. Synthetic fixtures retained at {basePath}");
await Phase2Tests.RunAsync(basePath, Check, Reject);
await OfflineTests.RunAsync(basePath, Check, Reject);
Console.WriteLine($"Total: {passed} assertions passed.");
