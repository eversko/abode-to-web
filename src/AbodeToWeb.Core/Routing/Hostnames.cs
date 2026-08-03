using System.Globalization;
using System.Net;

namespace AbodeToWeb.Core.Routing;

public readonly record struct Hostname
{
    private Hostname(string value) => Value = value;

    public string Value { get; }

    public static Hostname Parse(string value)
    {
        if (!TryParse(value, out var hostname, out var error))
        {
            throw new FormatException(error);
        }

        return hostname;
    }

    public static bool TryParse(string? value, out Hostname hostname) => TryParse(value, out hostname, out _);

    public static bool TryParse(string? value, out Hostname hostname, out string? error)
    {
        hostname = default;
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Hostname cannot be empty.";
            return false;
        }

        var candidate = value.TrimEnd('.');
        if (candidate.Length == 0 || candidate.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            error = "Hostname contains whitespace or a control character.";
            return false;
        }

        if (candidate.Contains('*', StringComparison.Ordinal) || candidate.Contains('_', StringComparison.Ordinal))
        {
            error = "Wildcard and underscore labels are not supported.";
            return false;
        }

        string ascii;
        try
        {
            ascii = new IdnMapping { UseStd3AsciiRules = true }.GetAscii(candidate).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            error = "Hostname is not a valid internationalized domain name.";
            return false;
        }

        if (ascii.Length > 253)
        {
            error = "Hostname exceeds 253 characters.";
            return false;
        }

        var labels = ascii.Split('.');
        if (labels.Length < 2)
        {
            error = "Hostname must be a domain name with at least two labels.";
            return false;
        }

        foreach (var label in labels)
        {
            if (label.Length is < 1 or > 63 ||
                !char.IsAsciiLetterOrDigit(label[0]) ||
                !char.IsAsciiLetterOrDigit(label[^1]) ||
                label.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
            {
                error = $"Hostname label '{label}' is invalid.";
                return false;
            }
        }

        if (IPAddress.TryParse(ascii, out _) ||
            ascii.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            ascii.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            ascii.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            error = "Hostname must be a routable domain name, not an IP address or local-only name.";
            return false;
        }

        hostname = new Hostname(ascii);
        return true;
    }

    public override string ToString() => Value ?? string.Empty;
}

public static class HostnameMatcher
{
    public static bool Matches(Hostname configured, string? hostHeader) =>
        TryParseHostHeader(hostHeader, out var requested) && configured == requested;

    public static bool TryParseHostHeader(string? hostHeader, out Hostname hostname)
    {
        hostname = default;
        if (string.IsNullOrWhiteSpace(hostHeader) ||
            hostHeader.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)) ||
            hostHeader.IndexOfAny(['/', '\\', '@', ',', '#', '?']) >= 0)
        {
            return false;
        }

        var value = hostHeader;
        var colon = value.LastIndexOf(':');
        if (colon >= 0)
        {
            if (value.IndexOf(':') != colon ||
                !int.TryParse(value.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
                port is < 1 or > 65_535)
            {
                return false;
            }

            value = value[..colon];
        }

        return Hostname.TryParse(value, out hostname);
    }
}

public sealed class HostnameRouteTable<TValue> where TValue : notnull
{
    private readonly Dictionary<Hostname, TValue> _routes;

    public HostnameRouteTable(IEnumerable<KeyValuePair<Hostname, TValue>> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var dictionary = new Dictionary<Hostname, TValue>();
        foreach (var (hostname, value) in routes)
        {
            if (!dictionary.TryAdd(hostname, value))
            {
                throw new ArgumentException($"Duplicate route for hostname '{hostname}'.", nameof(routes));
            }
        }

        _routes = dictionary;
    }

    public bool TryMatch(string? hostHeader, out TValue? value)
    {
        if (HostnameMatcher.TryParseHostHeader(hostHeader, out var hostname) && _routes.TryGetValue(hostname, out var route))
        {
            value = route;
            return true;
        }

        value = default;
        return false;
    }
}
