using System.Net;

namespace ZYC.LiveShare;

/// <summary>
///     An error returned by the Live Share web service.
/// </summary>
public class LiveShareServiceException(
    string message,
    HttpStatusCode? statusCode = null,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;

    internal static async Task ThrowIfFailedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var detail = string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase : body;
        throw new LiveShareServiceException(
            $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri} failed with {(int)response.StatusCode} {response.StatusCode}: {detail}",
            response.StatusCode);
    }
}