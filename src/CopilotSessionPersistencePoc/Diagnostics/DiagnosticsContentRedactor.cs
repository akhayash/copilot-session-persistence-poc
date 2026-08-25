using System.Text.RegularExpressions;

namespace CopilotSessionPersistencePoc.Diagnostics;

internal static partial class DiagnosticsContentRedactor
{
    public static string Redact(string content, bool contentTruncated)
    {
        string result = GitHubTokenPattern().Replace(content, "$1[REDACTED]");
        result = BearerTokenPattern().Replace(result, "$1[REDACTED]");
        if (!contentTruncated)
        {
            return result;
        }

        result = TruncatedGitHubTokenPattern().Replace(result, "$1[REDACTED]");
        return TruncatedBearerTokenPattern().Replace(result, "$1[REDACTED]");
    }

    [GeneratedRegex(@"\b(ghp_|github_pat_)[A-Za-z0-9_]{8,}", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubTokenPattern();

    [GeneratedRegex(@"(Bearer\s+)[A-Za-z0-9._~-]{8,}", RegexOptions.IgnoreCase)]
    private static partial Regex BearerTokenPattern();

    [GeneratedRegex(@"(ghp_|github_pat_)[A-Za-z0-9_]*$", RegexOptions.IgnoreCase)]
    private static partial Regex TruncatedGitHubTokenPattern();

    [GeneratedRegex(@"(Bearer\s+)[A-Za-z0-9._~-]*$", RegexOptions.IgnoreCase)]
    private static partial Regex TruncatedBearerTokenPattern();
}
