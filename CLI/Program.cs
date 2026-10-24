using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ARC.Launcher.Core;

Console.OutputEncoding = Encoding.UTF8;
try
{
    if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
    {
        Console.WriteLine("ARC Launcher — Offline local test\nlist\ninfo <实例名>\npath <实例名> <game|mods|shaders|resourcepacks|saves>\nopen <实例名> <game|mods|shaders|resourcepacks|saves>\nscan\njava list [--java <java.exe>]\nlaunch check|preview|run <实例名> [--java <java.exe>] [--offline --username <名称>]\nlaunch status\n全部命令支持 --json；仅访问项目下 .minecraft。Offline 仅本地单人游戏/开发测试，不具备正版在线认证能力。");
        return 0;
    }
    var json = args.Contains("--json");
    var positional = new List<string>();
    string? explicitJava = null;
    string? username = null;
    var offline = false;
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] == "--json") continue;
        if (args[i] == "--offline")
        {
            if (offline) throw new ArgumentException("--offline 不能重复。");
            offline = true;
        }
        else if (args[i] == "--username")
        {
            if (username is not null || ++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("--username 需要一个名称。");
            username = args[i];
        }
        else if (args[i] == "--java")
        {
            if (explicitJava is not null || ++i >= args.Length) throw new ArgumentException("--java 需要且只能提供一个可执行文件路径。");
            explicitJava = args[i];
        }
        else positional.Add(args[i]);
    }
    var values = positional.ToArray();
    if (values.Length == 0) throw new ArgumentException("缺少命令。");
    if (explicitJava is not null && values[0] is not ("java" or "launch")) throw new ArgumentException("--java 仅用于 java 或 launch 命令。");
    if (offline || username is not null)
    {
        if (!offline || username is null) throw new ArgumentException("必须同时明确指定 --offline 和 --username。");
        if (values.Length != 3 || values[0] != "launch" || values[1] is not ("check" or "preview" or "run"))
            throw new ArgumentException("Offline 仅用于 launch check/preview/run。");
    }
    var workspace = new DirectoryInfo(AppContext.BaseDirectory);
    while (workspace is not null && !File.Exists(Path.Combine(workspace.FullName, "ARC.Launcher.slnx")))
        workspace = workspace.Parent;
    if (workspace is null) throw new InvalidOperationException("找不到项目根目录 ARC.Launcher.slnx。");
    // The sole production composition point: never accept an arbitrary external game root.
    var root = new MinecraftRoot(Path.Combine(workspace.FullName, ".minecraft"));
    var service = new LocalInstanceService(root);
    var options = new JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    void Print(object value) => Console.WriteLine(JsonSerializer.Serialize(value, options));
    var javaService = new JavaService(root);
    ILaunchService launcher = new LaunchService(root, service, javaService, new UnavailableAuthenticationService());
    switch (values[0])
    {
        case "java" when values.Length == 2 && values[1] == "list":
            var discovery = await javaService.DiscoverAsync(explicitJava);
            Print(discovery);
            return discovery.Installations.Count > 0 ? 0 : 2;
        case "launch" when values.Length == 2 && values[1] == "status":
            Print(launcher.Status());
            return 0;
        case "launch" when values.Length == 3:
            var request = new LaunchRequest(values[2], explicitJava, offline ? new OfflineIdentity(username!) : null);
            switch (values[1])
            {
                case "check":
                    var check = await launcher.CheckAsync(request);
                    Print(check);
                    return check.CanLaunch ? 0 : 2;
                case "preview":
                    var preview = await launcher.PreviewAsync(request);
                    Print(preview);
                    return preview.Check.CanLaunch ? 0 : 2;
                case "run":
                    if (request.Offline is not null && !json) Console.WriteLine(request.Offline.Info.Notice);
                    var outputLock = new object();
                    void Log(LaunchLog entry)
                    {
                        lock (outputLock)
                        {
                            if (json) Console.WriteLine(JsonSerializer.Serialize(new { Type = "log", Data = entry }));
                            else Console.WriteLine($"[{entry.Stream}] {entry.Message}");
                        }
                    }
                    var running = await launcher.RunAsync(request, Log);
                    lock (outputLock)
                    {
                        if (json) Console.WriteLine(JsonSerializer.Serialize(new { Type = "started", Data = running.InitialStatus }));
                        else Console.WriteLine($"进程创建成功：{running.InitialStatus.ProcessId}；游戏主菜单尚未确认。\n脱敏日志：{running.InitialStatus.LogPath}");
                    }
                    var final = await running.Completion;
                    if (json) Console.WriteLine(JsonSerializer.Serialize(new { Type = "exited", Data = final }));
                    else Print(final);
                    return final.ExitCode == 0 ? 0 : 3;
                default: throw new ArgumentException("未知 launch 子命令。");
            }
        case "list" when values.Length == 1:
        case "scan" when values.Length == 1:
            var report = service.Scan();
            if (json || values[0] == "scan") Print(report);
            else
            {
                foreach (var instance in report.Instances)
                    Console.WriteLine($"{instance.Name}\t{instance.MinecraftVersion ?? "未知版本"}\t{instance.Loader}\t{instance.Layout}\t问题: {instance.Issues.Count}");
                foreach (var issue in report.Issues) Console.Error.WriteLine(issue);
                Console.WriteLine($"共 {report.Instances.Count} 个实例");
            }
            return report.Issues.Count == 0 && report.Instances.All(i => i.Issues.Count == 0) ? 0 : 2;
        case "info" when values.Length == 2:
            Print(new { Instance = service.GetInstance(values[1]), Directories = Enum.GetValues<GameDirectory>().Select(k => service.GetDirectory(values[1], k)).ToArray() });
            return 0;
        case "path" or "open" when values.Length == 3:
            var kind = values[2].ToLowerInvariant() switch
            {
                "game" => GameDirectory.Game, "mods" => GameDirectory.Mods,
                "shaders" or "shaderpacks" => GameDirectory.Shaders,
                "resourcepacks" => GameDirectory.ResourcePacks, "saves" => GameDirectory.Saves,
                _ => throw new ArgumentException("未知目录类型；请运行 help。")
            };
            var location = service.GetDirectory(values[1], kind);
            if (json) Print(location);
            else Console.WriteLine($"{location.Path}\n存在: {location.Exists}");
            if (values[0] == "open")
            {
                if (!location.Exists) throw new DirectoryNotFoundException("目录不存在，未创建也未打开。");
                root.EnsureSafe(location.Path);
                Process.Start(new ProcessStartInfo{
                    FileName = "explorer.exe",
                    ArgumentList = { location.Path },
                    UseShellExecute = true
                }) ;
            }
            return location.Exists ? 0 : 2;
        default: throw new ArgumentException("命令或参数数量无效；请运行 help。");
    }
}
catch (LaunchBlockedException ex)
{
    Console.WriteLine(JsonSerializer.Serialize(ex.Check, new JsonSerializerOptions { WriteIndented = true }));
    return 2;
}
catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException or PlatformNotSupportedException or System.Text.RegularExpressions.RegexMatchTimeoutException)
{
    // Parsing/process exceptions can carry metadata text; never echo raw launch exception messages.
    var message = args.FirstOrDefault() is "launch" or "java" ? $"操作失败 ({ex.GetType().Name})；请检查 JSON、路径、权限和 Java。" : ex.Message;
    if (args.Contains("--json")) Console.WriteLine(JsonSerializer.Serialize(new { Error = message }));
    else Console.Error.WriteLine($"错误：{message}");
    return 1;
}
