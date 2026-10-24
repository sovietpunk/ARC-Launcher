using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ARC.Launcher.Core;

public sealed record LaunchLog(DateTimeOffset Time, string Stream, string Message);
public sealed record GameProcessStatus(string RunId, string GameDirectory, int ProcessId,
    DateTimeOffset StartedAt, string State, int? ExitCode, string MainMenuState = "Unverified",
    string? LogPath = null, string LogCaptureState = "Capturing", LaunchIdentityInfo? Identity = null);
public sealed record ProcessStatusReport(IReadOnlyList<GameProcessStatus> Processes, IReadOnlyList<string> Issues);
public sealed class ManagedGameProcess(GameProcessStatus initialStatus, Task<GameProcessStatus> completion)
{
    public GameProcessStatus InitialStatus { get; } = initialStatus;
    public Task<GameProcessStatus> Completion { get; } = completion;
}

/// <summary>Process supervision independent of authentication and argument construction.</summary>
public sealed class GameProcessService(MinecraftRoot root)
{
    private const string Owner = "ARC.Launcher/runtime/v1";
    private sealed record Lease(string Owner, GameProcessStatus? Process);
    private string CacheRoot => root.Resolve(".arc-launcher", "v1");

    internal string CreateRunDirectory(string runId)
    {
        EnsureOwnedCache();
        var path = root.Resolve(".arc-launcher", "v1", "runs", runId);
        if (Directory.Exists(path) || File.Exists(path)) throw new IOException("运行缓存已存在，拒绝覆盖。");
        Directory.CreateDirectory(path);
        return path;
    }

    private void EnsureOwnedCache()
    {
        var marker = root.Resolve(".arc-launcher", "v1", "owner.txt");
        if (Directory.Exists(CacheRoot))
        {
            if (!File.Exists(marker) || File.ReadAllText(marker) != Owner) throw new IOException("缓存目录不是 ARC 所有，拒绝写入。");
            return;
        }
        Directory.CreateDirectory(CacheRoot);
        root.ValidateContained(marker);
        using var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write);
        stream.Write(Encoding.UTF8.GetBytes(Owner));
    }

    /// <summary>Caller owns the command. Process creation alone does not prove main-menu readiness.</summary>
    public ManagedGameProcess Start(string executable, IReadOnlyList<string> arguments, string gameDirectory,
        string runId, SecretRedactor redactor, Action<LaunchLog>? onLog = null, Action? prepare = null, LaunchIdentityInfo? identity = null)
    {
        gameDirectory = Path.TrimEndingDirectorySeparator(root.ValidateContained(gameDirectory));
        if (!Directory.Exists(gameDirectory)) throw new DirectoryNotFoundException("gameDir 不存在。");
        // Validate caller-controlled run IDs as a single safe path component before writes.
        root.Resolve(".arc-launcher", "v1", "runs", runId);
        EnsureOwnedCache();
        var locks = root.Resolve(".arc-launcher", "v1", "locks");
        Directory.CreateDirectory(locks);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(gameDirectory).ToUpperInvariant())));
        var lockPath = root.Resolve(".arc-launcher", "v1", "locks", key + ".json");
        FileStream leaseStream;
        try { leaseStream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read); }
        catch (IOException) { throw new IOException("同一 gameDir 已有 ARC 启动会话。"); }
        Process? process = null;
        StreamWriter? logWriter = null;
        try
        {
            if (leaseStream.Length > 0)
            {
                var lease = JsonSerializer.Deserialize<Lease>(leaseStream) ?? throw new IOException("无效运行锁，拒绝覆盖。");
                if (lease.Owner != Owner) throw new IOException("运行锁不是 ARC 所有，拒绝覆盖。");
                if (lease.Process is { } previous && IsAlive(previous)) throw new IOException("同一 gameDir 的游戏进程仍在运行。");
            }
            // Check duplication before native extraction or any per-run work.
            var runDir = CreateRunDirectory(runId);
            prepare?.Invoke();
            var logPath = root.ValidateContained(Path.Combine(runDir, "launch.ndjson"));
            logWriter = new StreamWriter(new FileStream(logPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = gameDirectory, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            JavaService.SanitizeEnvironment(start);
            process = Process.Start(start) ?? throw new IOException("无法创建游戏进程。");
            var status = new GameProcessStatus(runId, gameDirectory, process.Id, process.StartTime.ToUniversalTime(), "Running", null,
                LogPath: logPath, Identity: identity);
            leaseStream.Position = 0;
            leaseStream.SetLength(0);
            JsonSerializer.Serialize(leaseStream, new Lease(Owner, status));
            leaseStream.Flush(true);
            WriteStatus(runDir, status);
            return new(status, ObserveAsync(process, leaseStream, logWriter, runDir, status, redactor, onLog));
        }
        catch
        {
            if (process is not null) { try { if (!process.HasExited) process.Kill(true); } finally { process.Dispose(); } }
            leaseStream.Dispose();
            logWriter?.Dispose();
            throw;
        }
    }

    private async Task<GameProcessStatus> ObserveAsync(Process process, FileStream lease, StreamWriter logWriter, string runDirectory,
        GameProcessStatus initial, SecretRedactor redactor, Action<LaunchLog>? onLog)
    {
        using var logGate = new SemaphoreSlim(1, 1);
        var logFailed = false;
        async Task Pump(StreamReader reader, string name)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                // Redact before truncating or delivering callbacks. Never persist raw output.
                var safe = redactor.Redact(line);
                var entry = new LaunchLog(DateTimeOffset.UtcNow, name, safe.Length > 16384 ? safe[..16384] + "…" : safe);
                await logGate.WaitAsync();
                try { await logWriter.WriteLineAsync(JsonSerializer.Serialize(entry)); }
                catch (IOException) { logFailed = true; }
                finally { logGate.Release(); }
                try { onLog?.Invoke(entry); }
                catch { /* A subscriber cannot disable pipe draining or leave a process unobserved. */ }
            }
        }
        try
        {
            var stdout = Pump(process.StandardOutput, "stdout");
            var stderr = Pump(process.StandardError, "stderr");
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
            var final = initial with { State = "Exited", ExitCode = process.ExitCode, LogCaptureState = logFailed ? "Failed" : "Completed" };
            WriteStatus(runDirectory, final);
            return final;
        }
        finally
        {
            try { logWriter.Dispose(); }
            finally { process.Dispose(); lease.Dispose(); }
        }
    }

    private void WriteStatus(string runDirectory, GameProcessStatus status)
    {
        var path = root.ValidateContained(Path.Combine(runDirectory, "status.json"));
        var temporary = root.ValidateContained(Path.Combine(runDirectory, Guid.NewGuid().ToString("N") + ".tmp"));
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write)) JsonSerializer.Serialize(stream, status);
        File.Move(temporary, path, true);
    }

    public ProcessStatusReport Status()
    {
        var directory = root.Resolve(".arc-launcher", "v1", "runs");
        var statuses = new List<GameProcessStatus>();
        var issues = new List<string>();
        if (!Directory.Exists(directory)) return new(statuses, issues);
        foreach (var run in Directory.EnumerateDirectories(directory))
        {
            try
            {
                root.ValidateContained(run);
                var path = root.ValidateContained(Path.Combine(run, "status.json"));
                if (!File.Exists(path)) continue;
                var state = JsonSerializer.Deserialize<GameProcessStatus>(File.ReadAllText(path)) ?? throw new InvalidDataException();
                root.ValidateContained(state.GameDirectory);
                if (state.State == "Running" && !IsAlive(state)) state = state with { State = "ExitedUnknown", ExitCode = null };
                statuses.Add(state);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or ArgumentException)
            { issues.Add($"无法读取会话 {Path.GetFileName(run)} ({ex.GetType().Name})"); }
        }
        return new(statuses.OrderByDescending(s => s.StartedAt).ToArray(), issues);
    }

    private static bool IsAlive(GameProcessStatus status)
    {
        try
        {
            using var process = Process.GetProcessById(status.ProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime() == status.StartedAt.UtcDateTime;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
}
