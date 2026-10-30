using System.Text.Json;
using ZYC.LiveShare.Auth;
using ZYC.LiveShare.Service;

namespace ZYC.LiveShare.Guest;

/// <summary>
///     A TCP server the host shares. Guests reach it through the stream named
///     <see cref="StreamName" /> (<c>server_sharing</c>) with <see cref="StreamCondition" /> (the port).
/// </summary>
public sealed record SharedServer
{
    public int SourcePort { get; init; }
    public int DestinationPort { get; init; }
    public string? SessionName { get; init; }
    public required string StreamName { get; init; }
    public required string StreamCondition { get; init; }
    public string? BrowseUrl { get; init; }
    public JsonElement? Privacy { get; init; }
}

public sealed record ServerSharingEventArgs(SharedServer Server);

/// <summary>Result of <c>workspace.joinWorkspace</c>.</summary>
public sealed record WorkspaceSessionInfo
{
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? OwnerId { get; init; }
    public int SessionNumber { get; init; }
    public ConnectionMode ConnectionMode { get; init; }
    public Dictionary<string, JsonElement>? Sessions { get; init; }
    public string[]? ServiceNames { get; init; }
}

public sealed class GuestJoinOptions
{
    /// <summary>A join link (<c>https://prod.liveshare.vsengsaas.visualstudio.com/join?…</c>) or bare workspace id.</summary>
    public required string JoinLink { get; init; }

    /// <summary>The Live Share token to join with; null joins anonymously.</summary>
    public CascadeToken? Token { get; init; }

    /// <summary>Display name shown to the host for anonymous guests.</summary>
    public string? AnonymousUserName { get; init; }

    public ConnectionMode ConnectionMode { get; init; } = ConnectionMode.Auto;

    public LiveShareEndpoints Endpoints { get; init; } = LiveShareEndpoints.Production;

    /// <summary>How long to wait for the host to accept the guest.</summary>
    public TimeSpan JoinTimeout { get; init; } = TimeSpan.FromMinutes(5);
}