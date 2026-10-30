namespace ZYC.LiveShare;

/// <summary>
///     Service endpoints of a Live Share environment.
/// </summary>
public sealed record LiveShareEndpoints(Uri Service, Uri TokenExchange, string TokenAudience)
{
    /// <summary>
    ///     REST API version: product major version 1 + protocol major version 2.
    ///     The service reports min 0.1 / max 1.2.
    /// </summary>
    public const string ApiVersion = "1.2";

    public static LiveShareEndpoints Production { get; } = new(
        new Uri("https://prod.liveshare.vsengsaas.visualstudio.com/"),
        new Uri("https://sts.online.visualstudio.com/api/v1/tokens/exchange"),
        "https://insiders.liveshare.vsengsaas.visualstudio.com/");

    public Uri ApiBase => new(Service, $"api/v{ApiVersion}/");
}