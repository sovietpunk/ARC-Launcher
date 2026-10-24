using System.Text.Json.Serialization;

namespace ARC.Launcher.Core;

public sealed record LoginState(bool IsAuthenticated, string Status, DateTimeOffset? ExpiresAt = null);

/// <summary>A provider must verify Microsoft/Xbox/Minecraft authentication, entitlement and profile.
/// Offline test identities are a separate type and never returned by this provider.</summary>
public interface IAuthenticationService
{
    Task<LoginState> GetStateAsync(CancellationToken cancellationToken = default);
    Task<MinecraftSession?> GetSessionAsync(CancellationToken cancellationToken = default);
}

public sealed class UnavailableAuthenticationService : IAuthenticationService
{
    public Task<LoginState> GetStateAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new LoginState(false, "Microsoft/Minecraft 正版认证尚未接入"));
    public Task<MinecraftSession?> GetSessionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<MinecraftSession?>(null);
}

/// <summary>Memory-only credentials returned by a trusted authentication provider.</summary>
public sealed class MinecraftSession(string playerName, string uuid, string accessToken,
    string clientId, string xuid, DateTimeOffset expiresAt)
{
    [JsonIgnore] public string PlayerName { get; } = playerName;
    [JsonIgnore] public string Uuid { get; } = uuid;
    [JsonIgnore] public string AccessToken { get; } = accessToken;
    [JsonIgnore] public string ClientId { get; } = clientId;
    [JsonIgnore] public string Xuid { get; } = xuid;
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
    public bool IsUsable => ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(30) &&
        !string.IsNullOrWhiteSpace(AccessToken) && !string.IsNullOrWhiteSpace(PlayerName) &&
        Guid.TryParse(Uuid, out _);
    public override string ToString() => "MinecraftSession [REDACTED]";
}
