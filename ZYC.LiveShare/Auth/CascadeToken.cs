using System.Text.Json;

namespace ZYC.LiveShare.Auth;

/// <summary>
///     A Live Share service token (internally called a "Cascade" token): a JWT issued by the
///     Live Share service, either directly (anonymous, user code) or via STS token exchange.
/// </summary>
public sealed record CascadeToken(
    string Value,
    DateTimeOffset ExpiresAt,
    bool IsAnonymous,
    string? UserId,
    string? Name)
{
    /// <summary>The official client renews tokens that expire within two hours.</summary>
    public static TimeSpan RenewalMargin { get; } = TimeSpan.FromHours(2);

    public bool IsExpiring => DateTimeOffset.UtcNow + RenewalMargin >= ExpiresAt;

    public static CascadeToken Parse(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3)
        {
            throw new FormatException("Token is not a JWT.");
        }

        using var payload = JsonDocument.Parse(Base64UrlDecode(parts[1]));
        var root = payload.RootElement;

        var expiresAt = root.TryGetProperty("exp", out var exp)
            ? DateTimeOffset.FromUnixTimeSeconds(exp.GetInt64())
            : DateTimeOffset.MaxValue;
        var isAnonymous = root.TryGetProperty("anonymous", out var anonymous)
                          && anonymous.ValueKind == JsonValueKind.String
                          && anonymous.GetString() == "anonymous";

        return new CascadeToken(jwt, expiresAt, isAnonymous, GetString(root, "userId"), GetString(root, "name"));
    }

    public override string ToString()
    {
        return $"{(IsAnonymous ? "anonymous" : "user")} token for {Name ?? UserId}, expires {ExpiresAt:u}";
    }

    private static string? GetString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 += (base64.Length % 4) switch { 2 => "==", 3 => "=", _ => string.Empty };
        return Convert.FromBase64String(base64);
    }
}