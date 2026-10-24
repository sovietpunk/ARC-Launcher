namespace ARC.Launcher.Core;

public enum GameDirectory { Game, Mods, Shaders, ResourcePacks, Saves }
public enum InstanceLayout { Isolated, Shared }
public sealed record DirectoryLocation(GameDirectory Kind, string Path, bool Exists);
public sealed record GameInstance(string Name, string? VersionId, string? MinecraftVersion,
    string Loader, string? MainClass, string? InheritsFrom, string MetadataPath,
    bool ClientJarExists, InstanceLayout Layout, IReadOnlyList<string> Issues);
public sealed record ScanReport(string RootPath, IReadOnlyList<GameInstance> Instances,
    IReadOnlyList<string> Issues);

/// <summary>Compatibility entry point retained for first-phase callers.</summary>
public interface IMinecraftLaunchService
{
    Task<LaunchResult> LaunchAsync(LaunchRequest request, CancellationToken cancellationToken = default);
}
public sealed record LaunchRequest(string InstanceName, string? JavaExecutable = null, OfflineIdentity? Offline = null);
public sealed record LaunchResult(bool Started, string? Error, int? ProcessId = null);
