using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ZYC.LiveShare.Auth;

namespace ZYC.LiveShare.Service;

/// <summary>
///     Client for the Live Share REST API (<c>/api/v1.2/</c>).
/// </summary>
public sealed class LiveShareServiceClient(HttpClient httpClient, LiveShareEndpoints endpoints)
{
    internal static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);

    public async Task<UserProfile> GetProfileAsync(CascadeToken token, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "profile", token);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await LiveShareServiceException.ThrowIfFailedAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<UserProfile>(JsonOptions, cancellationToken))!;
    }

    /// <summary>
    ///     Adds the caller to the workspace as a guest and returns the host's connection info.
    /// </summary>
    public async Task<SharedWorkspace> AddGuestAsync(string workspaceId, CascadeToken token,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Put, $"workspace/{Uri.EscapeDataString(workspaceId)}/user", token);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new LiveShareServiceException(
                "The host is offline or the collaboration session is no longer available.", HttpStatusCode.NotFound);
        }

        await LiveShareServiceException.ThrowIfFailedAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<SharedWorkspace>(JsonOptions, cancellationToken))!;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, CascadeToken token)
    {
        var request = new HttpRequestMessage(method, new Uri(endpoints.ApiBase, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        return request;
    }
}

public sealed record UserProfile(string Id, string? Name, string? Email, string? Provider, bool IsAnonymous);