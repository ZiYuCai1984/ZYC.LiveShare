using System.Text;
using System.Text.Json;
using ZYC.LiveShare.Auth;
using ZYC.LiveShare.Service;
using ZYC.LiveShare.Transport;

namespace ZYC.LiveShare.Tests;

public class ParsingTests
{
    private const string WorkspaceId = "0123456789abcdef0123456789abcdef0123";

    [Theory]
    [InlineData("https://prod.liveshare.vsengsaas.visualstudio.com/join?" + WorkspaceId)]
    [InlineData("https://prod.liveshare.vsengsaas.visualstudio.com/join/?" + WorkspaceId)]
    [InlineData("https://vscode.dev/liveshare/" + WorkspaceId)]
    [InlineData(
        "vscode://ms-vsliveshare.vsliveshare/join?vslsLink=https%3A%2F%2Fprod.liveshare.vsengsaas.visualstudio.com%2Fjoin%3F" +
        WorkspaceId)]
    [InlineData("vsls:?action=join&workspaceId=" + WorkspaceId)]
    [InlineData(WorkspaceId)]
    public void JoinLinkParsesWorkspaceId(string link)
    {
        Assert.Equal(WorkspaceId, JoinLink.ParseWorkspaceId(link));
    }

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("not-a-link")]
    public void JoinLinkRejectsOtherInput(string link)
    {
        Assert.False(JoinLink.TryParseWorkspaceId(link, out _));
    }

    [Fact]
    public void CascadeTokenReadsClaims()
    {
        var expires = DateTimeOffset.UtcNow.AddDays(7);
        var jwt = CreateJwt(new
        {
            aud = "x", name = "Guest User", anonymous = "anonymous", userId = "u1", exp = expires.ToUnixTimeSeconds()
        });

        var token = CascadeToken.Parse(jwt);

        Assert.True(token.IsAnonymous);
        Assert.Equal("u1", token.UserId);
        Assert.Equal("Guest User", token.Name);
        Assert.Equal(expires.ToUnixTimeSeconds(), token.ExpiresAt.ToUnixTimeSeconds());
        Assert.False(token.IsExpiring);
    }

    [Fact]
    public void CascadeTokenNearExpiryIsExpiring()
    {
        var token = CascadeToken.Parse(CreateJwt(new { exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() }));
        Assert.True(token.IsExpiring);
        Assert.False(token.IsAnonymous);
    }

    [Theory]
    [InlineData("2", ConnectionMode.Relay)]
    [InlineData("\"relay\"", ConnectionMode.Relay)]
    [InlineData("\"Direct\"", ConnectionMode.Direct)]
    [InlineData("\"devTunnel\"", ConnectionMode.DevTunnel)]
    public void SharedWorkspaceAcceptsNumericAndStringConnectionModes(string mode, ConnectionMode expected)
    {
        var json = $$"""
                     {"id":"w","connectionMode":{{mode}},"sessionToken":"s","hostPublicKeys":["AAAA"],
                      "connectLinks":["net.tcp://10.0.0.2:5990/"],"relayLink":"sb://r.servicebus.windows.net/x","relaySas":"sas"}
                     """;

        var workspace =
            JsonSerializer.Deserialize<SharedWorkspace>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal(expected, workspace.ConnectionMode);
        Assert.Equal("s", workspace.SessionToken);
        Assert.Single(workspace.ConnectLinks);
    }

    [Theory]
    [InlineData("net.tcp://10.0.0.2:5990/", true, "10.0.0.2", 5990)]
    [InlineData("net.tcp://host.local:1234/", true, "host.local", 1234)]
    [InlineData("tcp://10.0.0.2:5990/", false, "", 0)]
    [InlineData("net.tcp://10.0.0.2/", false, "", 0)]
    [InlineData("net.tcp://10.0.0.2:5990/path", false, "", 0)]
    public void DirectAddressParsing(string address, bool valid, string host, int port)
    {
        Assert.Equal(valid, HostConnector.TryParseDirectAddress(address, out var parsedHost, out var parsedPort));
        Assert.Equal(host, parsedHost);
        Assert.Equal(port, parsedPort);
    }

    [Theory]
    [InlineData("net.tcp://127.0.0.1:5990/", true)]
    [InlineData("net.tcp://[::1]:5990/", true)]
    [InlineData("net.tcp://192.0.2.10:5990/", false)]
    [InlineData("net.tcp://[2001:db8::1]:5990/", false)]
    public void DetectsHostOnThisMachine(string link, bool expected)
    {
        var workspace = new SharedWorkspace { Id = "w", ConnectLinks = [link] };
        Assert.Equal(expected, HostConnector.IsHostOnThisMachine(workspace));
    }

    [Theory]
    [InlineData(ConnectionMode.Auto, ConnectionMode.Auto, ConnectionMode.Auto)]
    [InlineData(ConnectionMode.Auto, ConnectionMode.Relay, ConnectionMode.Relay)]
    [InlineData(ConnectionMode.Relay, ConnectionMode.Auto, ConnectionMode.Relay)]
    [InlineData(ConnectionMode.Direct, ConnectionMode.Direct, ConnectionMode.Direct)]
    public void ConnectionModeResolution(ConnectionMode requested, ConnectionMode host, ConnectionMode expected)
    {
        Assert.Equal(expected, HostConnector.ResolveMode(requested, host));
    }

    [Theory]
    [InlineData(ConnectionMode.Direct, ConnectionMode.Relay)]
    [InlineData(ConnectionMode.Relay, ConnectionMode.Direct)]
    public void ConnectionModeConflictsThrow(ConnectionMode requested, ConnectionMode host)
    {
        Assert.Throws<HostConnectionException>(() => HostConnector.ResolveMode(requested, host));
    }

    private static string CreateJwt(object payload)
    {
        static string Encode(string json)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-')
                .Replace('/', '_');
        }

        return $"{Encode("{\"alg\":\"RS256\",\"typ\":\"JWT\"}")}.{Encode(JsonSerializer.Serialize(payload))}.signature";
    }
}