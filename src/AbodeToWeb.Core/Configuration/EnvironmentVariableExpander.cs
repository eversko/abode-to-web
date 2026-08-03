using System.Text.RegularExpressions;

namespace AbodeToWeb.Core.Configuration;

public sealed record ExpansionResult(string? Value, IReadOnlyList<string> MissingVariables)
{
    public bool IsSuccess => MissingVariables.Count == 0;
}

public static partial class EnvironmentVariableExpander
{
    public static ExpansionResult Expand(string template, IReadOnlyDictionary<string, string> variables)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(variables);

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var normalized = new Dictionary<string, string>(variables, comparer);
        var missing = new HashSet<string>(comparer);

        string Replace(Match match)
        {
            var name = match.Groups["braced"].Success
                ? match.Groups["braced"].Value
                : match.Groups["percent"].Value;
            if (normalized.TryGetValue(name, out var value))
            {
                return value;
            }

            missing.Add(name);
            return match.Value;
        }

        var expanded = VariablePattern().Replace(template, Replace);
        return new ExpansionResult(missing.Count == 0 ? expanded : null, missing.Order(comparer).ToArray());
    }

    [GeneratedRegex(@"\$\{(?<braced>[A-Za-z_][A-Za-z0-9_]*)\}|%(?<percent>[A-Za-z_][A-Za-z0-9_]*)%", RegexOptions.CultureInvariant)]
    private static partial Regex VariablePattern();
}
