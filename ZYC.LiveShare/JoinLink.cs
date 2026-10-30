using System.Text.RegularExpressions;

namespace ZYC.LiveShare;

/// <summary>
///     Extracts the workspace id from a Live Share join link.
/// </summary>
public static partial class JoinLink
{
    // https://prod.liveshare.vsengsaas.visualstudio.com/join?<id>
    [GeneratedRegex(@"liveshare[^/]*/join/?\?([0-9a-z]{36})", RegexOptions.IgnoreCase)]
    private static partial Regex ServiceLinkRegex();

    // https://vscode.dev/liveshare/<id>
    [GeneratedRegex(@"/liveshare/([0-9a-z]{36})(?:[/?#]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex WebLinkRegex();

    // vsls:?action=join&workspaceId=<id>
    [GeneratedRegex(@"workspaceId=([0-9a-z-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex VslsLinkRegex();

    [GeneratedRegex(@"^[0-9a-z]{36}$", RegexOptions.IgnoreCase)]
    private static partial Regex BareIdRegex();

    public static bool TryParseWorkspaceId(string linkOrId, out string workspaceId)
    {
        var input = Uri.UnescapeDataString(linkOrId.Trim());
        foreach (var regex in new[] { ServiceLinkRegex(), WebLinkRegex(), VslsLinkRegex() })
        {
            var match = regex.Match(input);
            if (match.Success)
            {
                workspaceId = match.Groups[1].Value;
                return true;
            }
        }

        if (BareIdRegex().IsMatch(input))
        {
            workspaceId = input;
            return true;
        }

        workspaceId = string.Empty;
        return false;
    }

    public static string ParseWorkspaceId(string linkOrId)
    {
        return TryParseWorkspaceId(linkOrId, out var id)
            ? id
            : throw new FormatException($"Not a Live Share join link or workspace id: '{linkOrId}'.");
    }
}