using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZYC.LiveShare.Service;

/// <summary>
///     How a guest reaches the host. Values match the Live Share wire format.
/// </summary>
[JsonConverter(typeof(ConnectionModeConverter))]
public enum ConnectionMode
{
    Auto,
    Direct,
    Relay,
    Local,
    SignalR,
    DevTunnel
}

/// <summary>
///     The workspace returned to a guest by <c>PUT workspace/{id}/user</c>: everything needed to reach the host.
/// </summary>
public sealed class SharedWorkspace
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public string? OwnerId { get; init; }
    public ConnectionMode ConnectionMode { get; init; }

    /// <summary>Per-guest session token; used as the SSH password and in <c>workspace.joinWorkspace</c>.</summary>
    public string? SessionToken { get; init; }

    /// <summary>Base64 SSH public keys the host must authenticate with.</summary>
    public string[] HostPublicKeys { get; init; } = [];

    /// <summary>Direct addresses, e.g. <c>net.tcp://192.168.1.2:5990/</c>.</summary>
    public string[] ConnectLinks { get; init; } = [];

    /// <summary>Azure Relay Hybrid Connection address.</summary>
    public string? RelayLink { get; init; }

    /// <summary>Shared access signature for <see cref="RelayLink" />.</summary>
    public string? RelaySas { get; init; }

    public bool AreAnonymousGuestsAllowed { get; init; }
    public bool? IsHostConnected { get; init; }

    /// <summary>Dev tunnel info, used only by browser clients. Not supported here.</summary>
    public JsonElement? TunnelProperties { get; init; }
}

/// <summary>
///     The service and hosts send connection modes as either numbers or camelCase strings.
/// </summary>
internal sealed class ConnectionModeConverter : JsonConverter<ConnectionMode>
{
    public override ConnectionMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Number => (ConnectionMode)reader.GetInt32(),
            JsonTokenType.String when Enum.TryParse<ConnectionMode>(reader.GetString(), true, out var mode) => mode,
            _ => ConnectionMode.Auto
        };
    }

    public override void Write(Utf8JsonWriter writer, ConnectionMode value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
    }
}