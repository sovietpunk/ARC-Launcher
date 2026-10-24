using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ARC.Launcher.Core;

public enum LaunchIdentityKind { Microsoft, Offline }
public sealed record LaunchIdentityInfo(LaunchIdentityKind Kind, string? PlayerName, string? Uuid,
    bool IsOnlineAuthenticated, string Notice);

/// <summary>Identity data is independent of version/JVM argument construction.</summary>
public abstract class LaunchIdentity
{
    public abstract LaunchIdentityInfo Info { get; }
    internal abstract IReadOnlyDictionary<string, string> Parameters { get; }
    internal virtual IEnumerable<string> Secrets => [];
    public override string ToString() => $"{GetType().Name} [identity values omitted]";
}

public sealed class OfflineIdentity : LaunchIdentity
{
    public string Username { get; }
    public string Uuid { get; }
    public override LaunchIdentityInfo Info => new(LaunchIdentityKind.Offline, Username, Uuid, false,
        "Offline 本地单人游戏/开发测试；不具备 Microsoft 或 Minecraft 正版在线认证能力。");

    public OfflineIdentity(string username)
    {
        if (string.IsNullOrEmpty(username) || !Regex.IsMatch(username, "^[A-Za-z0-9_]{1,16}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Offline 名称必须为 1–16 位英文字母、数字或下划线。");
        Username = username;
        // Java UUID.nameUUIDFromBytes uses MD5 and RFC 4122 version/variant bits.
        // Parse hexadecimal rather than Guid(byte[]) to avoid .NET mixed-endian field order.
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + username));
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x30);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        Uuid = Convert.ToHexString(bytes).ToLowerInvariant();
    }

    // These are explicit unauthenticated protocol placeholders, never online credentials.
    internal override IReadOnlyDictionary<string, string> Parameters => new Dictionary<string, string>
    {
        ["auth_player_name"] = Username, ["auth_uuid"] = Uuid, ["auth_access_token"] = "0",
        ["auth_session"] = "-", ["clientid"] = "", ["auth_xuid"] = "",
        ["user_type"] = "legacy", ["user_properties"] = "{}"
    };
}

public sealed class MicrosoftLaunchIdentity : LaunchIdentity
{
    private readonly MinecraftSession session;
    public MicrosoftLaunchIdentity(MinecraftSession session)
    {
        if (!session.IsUsable) throw new ArgumentException("正版会话无效或已过期。");
        this.session = session;
    }
    public override LaunchIdentityInfo Info => new(LaunchIdentityKind.Microsoft, null, null, true, "Microsoft/Minecraft 正版身份");
    internal override IEnumerable<string> Secrets => [session.AccessToken, session.Uuid, session.Xuid, session.ClientId];
    internal override IReadOnlyDictionary<string, string> Parameters => new Dictionary<string, string>
    {
        ["auth_player_name"] = session.PlayerName, ["auth_uuid"] = session.Uuid,
        ["auth_access_token"] = session.AccessToken, ["auth_session"] = $"token:{session.AccessToken}:{session.Uuid}",
        ["clientid"] = session.ClientId, ["auth_xuid"] = session.Xuid,
        ["user_type"] = "msa", ["user_properties"] = "{}"
    };
}

public static class IdentityArgumentBinder
{
    public static IReadOnlyList<string> Bind(IEnumerable<string> arguments, LaunchIdentity identity)
    {
        var values = identity.Parameters;
        return arguments.Select(argument => Regex.Replace(argument, @"\$\{([^}]+)\}", match =>
        {
            if (!values.TryGetValue(match.Groups[1].Value, out var value))
                throw new InvalidDataException("存在未解析的启动占位符。");
            if (identity.Info.Kind == LaunchIdentityKind.Microsoft && string.IsNullOrWhiteSpace(value))
                throw new InvalidDataException("正版认证会话缺少必要的身份字段。");
            return value;
        })).ToArray();
    }
}
