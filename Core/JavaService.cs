using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ARC.Launcher.Core;

public sealed record JavaInstallation(string Executable, int MajorVersion, string Architecture, string Version);
public sealed record JavaDiscovery(IReadOnlyList<JavaInstallation> Installations, IReadOnlyList<string> Issues);

public sealed class JavaService(MinecraftRoot root)
{
    public async Task<JavaDiscovery> DiscoverAsync(string? explicitJava = null, CancellationToken cancellationToken = default)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var issues = new List<string>();
        if (explicitJava is not null) candidates.Add(Path.GetFullPath(explicitJava));
        else
        {
            void AddHome(string? home) { if (!string.IsNullOrWhiteSpace(home)) candidates.Add(Path.Combine(home, "bin", "java.exe")); }
            AddHome(Environment.GetEnvironmentVariable("JAVA_HOME"));
            AddHome(Environment.GetEnvironmentVariable("JDK_HOME"));
            foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                candidates.Add(Path.Combine(entry.Trim('"'), "java.exe"));
            foreach (var baseDir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
                foreach (var vendor in new[] { "Java", "Eclipse Adoptium", "Microsoft", "Zulu", "Amazon Corretto", "BellSoft", "Semeru" })
                {
                    var directory = Path.Combine(baseDir, vendor);
                    if (!Directory.Exists(directory)) continue;
                    foreach (var home in Directory.EnumerateDirectories(directory)) AddHome(home);
                }
            var jdks = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".jdks");
            if (Directory.Exists(jdks)) foreach (var home in Directory.EnumerateDirectories(jdks)) AddHome(home);
            var runtime = root.Resolve("runtime");
            if (Directory.Exists(runtime))
                FindTestRuntimes(runtime, candidates, 0);
            if (OperatingSystem.IsWindows()) ReadRegistry(candidates);
        }
        var installations = new List<JavaInstallation>();
        foreach (var candidate in candidates.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(candidate))
            { if (explicitJava is not null) issues.Add("指定的 Java 可执行文件不存在。"); continue; }
            try { installations.Add(await InspectAsync(candidate, cancellationToken)); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
            { issues.Add($"Java 检测失败：{candidate} ({ex.GetType().Name})"); }
        }
        return new(installations, issues);
    }

    private void FindTestRuntimes(string directory, HashSet<string> candidates, int depth)
    {
        root.ValidateContained(directory);
        var executable = Path.Combine(directory, "java.exe");
        root.ValidateContained(executable);
        if (File.Exists(executable)) candidates.Add(executable);
        if (depth >= 5) return;
        foreach (var child in Directory.EnumerateDirectories(directory)) FindTestRuntimes(child, candidates, depth + 1);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void ReadRegistry(HashSet<string> candidates)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                foreach (var key in new[] { @"SOFTWARE\JavaSoft\JDK", @"SOFTWARE\JavaSoft\Java Development Kit", @"SOFTWARE\JavaSoft\Java Runtime Environment", @"SOFTWARE\JavaSoft\JRE" })
                {
                    using var java = machine.OpenSubKey(key);
                    if (java is null) continue;
                    foreach (var version in java.GetSubKeyNames())
                    {
                        using var release = java.OpenSubKey(version);
                        if (release?.GetValue("JavaHome") is string home) candidates.Add(Path.Combine(home, "bin", "java.exe"));
                    }
                }
    }

    public static async Task<JavaInstallation> InspectAsync(string executable, CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add("-XshowSettings:properties");
        start.ArgumentList.Add("-version");
        SanitizeEnvironment(start);
        using var process = Process.Start(start) ?? throw new IOException("无法启动 Java 检测。");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); cancellationToken.ThrowIfCancellationRequested(); throw new TimeoutException("Java 检测超时。"); }
        var output = await stdout + "\n" + await stderr;
        if (process.ExitCode != 0) throw new IOException("Java 检测退出码非零。");
        return ParseSettings(Path.GetFullPath(executable), output);
    }

    public static JavaInstallation ParseSettings(string executable, string output)
    {
        string Property(string name) => Regex.Match(output, @"(?m)^\s*" + Regex.Escape(name) + @"\s*=\s*([^\r\n]+)").Groups[1].Value.Trim();
        var version = Property("java.version");
        var parts = version.Split('.', '-', '+', '_');
        if (!int.TryParse(parts.ElementAtOrDefault(parts.FirstOrDefault() == "1" ? 1 : 0), out var major))
            throw new IOException("无法解析 Java 主版本。");
        var arch = NormalizeArchitecture(Property("os.arch"));
        if (arch is not ("amd64" or "x86" or "aarch64")) throw new IOException("无法解析 Java 架构。");
        return new(executable, major, arch, version);
    }

    public static string NormalizeArchitecture(string value) => value.ToLowerInvariant() switch
    { "x86_64" or "x64" or "amd64" => "amd64", "i386" or "i686" or "x86" => "x86", "arm64" or "aarch64" => "aarch64", _ => value };
    internal static void SanitizeEnvironment(ProcessStartInfo start)
    {
        foreach (var key in new[] { "JAVA_TOOL_OPTIONS", "_JAVA_OPTIONS", "JDK_JAVA_OPTIONS", "CLASSPATH" }) start.Environment.Remove(key);
    }
}
