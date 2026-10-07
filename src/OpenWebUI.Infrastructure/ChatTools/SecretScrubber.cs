using System.Text.RegularExpressions;

namespace OpenWebUI.Infrastructure.ChatTools;

/// <summary>
/// Remove padrões conhecidos de credenciais de outputs antes de irem para o
/// modelo, para o transcript ou para o disco (SPEC-20261007-chat-agent-tools
/// RF-005): o valor real nunca retorna ao cliente nem persiste em arquivo.
/// </summary>
public static partial class SecretScrubber
{
    /// <summary>Texto que substitui cada segredo encontrado.</summary>
    public const string Redacted = "[REDACTED_SECRET]";

    /// <summary>Mascara segredos conhecidos no texto; null-safe.</summary>
    public static string? Scrub(string? output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return output;
        }

        var result = output;
        result = GitHubTokenRegex().Replace(result, Redacted);
        result = GitHubPatRegex().Replace(result, Redacted);
        result = ApiKeyRegex().Replace(result, Redacted);
        result = AwsKeyRegex().Replace(result, Redacted);
        result = BearerTokenRegex().Replace(result, $"Bearer {Redacted}");
        result = PrivateKeyRegex().Replace(result, Redacted);
        return result;
    }

    // ghp_/gho_/ghu_/ghs_/ghr_ tokens pessoais + OAuth.
    [GeneratedRegex(@"gh[pousr]_[A-Za-z0-9_]{36,}", RegexOptions.Compiled)]
    private static partial Regex GitHubTokenRegex();

    // github_pat_ fine-grained PATs.
    [GeneratedRegex(@"github_pat_[A-Za-z0-9_]{22,}", RegexOptions.Compiled)]
    private static partial Regex GitHubPatRegex();

    // sk- OpenAI/Anthropic/GenAI-style API keys.
    [GeneratedRegex(@"sk-[A-Za-z0-9_-]{20,}", RegexOptions.Compiled)]
    private static partial Regex ApiKeyRegex();

    // AWS access key ids.
    [GeneratedRegex(@"\b(?:AKIA|ASIA|AGPA|AIDA|AROA)[A-Z0-9]{16}\b", RegexOptions.Compiled)]
    private static partial Regex AwsKeyRegex();

    // Authorization: Bearer <token>.
    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9._~+/=-]{20,}", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex BearerTokenRegex();

    // PEM private key headers.
    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----", RegexOptions.Compiled)]
    private static partial Regex PrivateKeyRegex();
}
