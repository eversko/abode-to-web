using System.Text;
using System.Text.RegularExpressions;

namespace AbodeToWeb.Core.Security;

public sealed record SecretValue(string Key, string Value);

public interface ILogRedactor
{
    string Redact(string text);
}

public sealed partial class SecretRedactor : ILogRedactor
{
    public const string Replacement = "[REDACTED]";
    private readonly IReadOnlyList<string> _secretVariants;
    private readonly IReadOnlyList<Regex> _keyPatterns;

    public SecretRedactor(IEnumerable<SecretValue>? secrets = null)
    {
        var descriptors = secrets?.Where(secret => !string.IsNullOrWhiteSpace(secret.Key)).ToArray() ?? [];
        _secretVariants = descriptors
            .SelectMany(CreateVariants)
            .Where(value => value.Length > 0 && !value.Equals(Replacement, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value.Length)
            .ToArray();

        var keys = descriptors.Select(secret => secret.Key)
            .Concat(DefaultSensitiveKeys)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        _keyPatterns = keys.SelectMany(CreateKeyPatterns).ToArray();
    }

    public string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return text;
        }

        var redacted = text;
        foreach (var value in _secretVariants)
        {
            redacted = redacted.Replace(value, Replacement, StringComparison.Ordinal);
        }

        try
        {
            redacted = AuthorizationPattern().Replace(redacted, match => match.Groups["prefix"].Value + Replacement);
            foreach (var pattern in _keyPatterns)
            {
                redacted = pattern.Replace(redacted, match => match.Groups["prefix"].Value + Replacement);
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return "[LOG ENTRY SUPPRESSED: REDACTION TIMEOUT]";
        }

        return redacted;
    }

    private static readonly string[] DefaultSensitiveKeys =
    [
        "password", "passwd", "token", "access_token", "refresh_token", "api_key", "api-key",
        "secret", "client_secret", "private_key", "connection_string"
    ];

    private static IEnumerable<string> CreateVariants(SecretValue secret)
    {
        if (string.IsNullOrEmpty(secret.Value))
        {
            yield break;
        }

        yield return secret.Value;
        var escaped = Uri.EscapeDataString(secret.Value);
        if (!escaped.Equals(secret.Value, StringComparison.Ordinal))
        {
            yield return escaped;
        }

        if (secret.Value.Length >= 8)
        {
            yield return Convert.ToBase64String(Encoding.UTF8.GetBytes(secret.Value));
        }
    }

    private static IEnumerable<Regex> CreateKeyPatterns(string key)
    {
        var escapedKey = Regex.Escape(key);
        yield return new Regex(
            $@"(?im)(?<prefix>(?:^|[\s,;{{?&]|&amp;)(?:[\""']?{escapedKey}[\""']?)\s*(?:=|:)\s*)(?:[\""'][^\r\n\""']*[\""']|[^\s,;}}&?#]+)",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100));
        yield return new Regex(
            $@"(?im)(?<prefix>(?:^|%(?:3f|26))(?:[\""']?{escapedKey}[\""']?)\s*%3d\s*)(?:(?!(?:%26(?=[a-z0-9_.~-]+%3d)|%23))[^\s,;}}&?#])+",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100));
    }

    [GeneratedRegex(@"(?im)(?<prefix>authorization\s*:\s*(?:bearer|basic)\s+)[^\s,;]+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 100)]
    private static partial Regex AuthorizationPattern();
}
