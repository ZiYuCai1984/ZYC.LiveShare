using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

// ReSharper disable GrammarMistakeInComment
// ReSharper disable InvalidXmlDocComment

namespace ZYC.LiveShare.Auth;

/// <summary>
///     Acquires Live Share service tokens.
/// </summary>
public sealed class LiveShareAuthClient(HttpClient httpClient, LiveShareEndpoints endpoints)
{
    /// <summary>
    ///     Gets an anonymous guest token. Joining with it only works when the host allows anonymous guests.
    /// </summary>
    public async Task<CascadeToken> GetAnonymousTokenAsync(CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            new Uri(endpoints.Service, "auth/anonymous-token"), new { scope = string.Empty }, cancellationToken);
        await LiveShareServiceException.ThrowIfFailedAsync(response, cancellationToken);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var accessToken = body.RootElement.GetProperty("access_token").GetString()
                          ?? throw new LiveShareServiceException("The anonymous token response has no access_token.");
        return CascadeToken.Parse(accessToken);
    }

    /// <summary>
    ///     Exchanges an external identity provider token (e.g. a GitHub OAuth token with
    ///     <c>read:user user:email</c> scopes) for a Live Share token, as the VS Code extension does.
    /// </summary>
    /// <param name="provider"><c>github</c> or <c>microsoft</c>.</param>
    public async Task<CascadeToken> ExchangeTokenAsync(
        string provider,
        string externalToken,
        CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(new
        {
            provider,
            token = externalToken,
            audience = endpoints.TokenAudience,
            lifetime = "25"
        });
        using var content = new StringContent(json, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json-patch+json");

        using var response = await httpClient.PostAsync(endpoints.TokenExchange, content, cancellationToken);
        await LiveShareServiceException.ThrowIfFailedAsync(response, cancellationToken);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var token = body.RootElement.GetProperty("token").GetString()
                    ?? throw new LiveShareServiceException("The token exchange response has no token.");
        return CascadeToken.Parse(token);
    }
}