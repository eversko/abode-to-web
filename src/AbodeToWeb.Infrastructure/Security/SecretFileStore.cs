using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using AbodeToWeb.Core.Security;

namespace AbodeToWeb.Infrastructure.Security;

public sealed partial class SecretFileStore(AbodeToWebPaths paths)
{
    private const int MaximumSecretKeys = 500;
    private const int MaximumExampleBytes = 65_536;
    private const long MaximumSecretFileBytes = 128L * 1024 * 1024;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _writeLocks = new(StringComparer.OrdinalIgnoreCase);
    private long _changeVersion;

    public long ChangeVersion => Interlocked.Read(ref _changeVersion);

    public static bool IsValidApplicationId(string? appId) =>
        appId is not null && AppIdPattern().IsMatch(appId);

    public IReadOnlyList<string> ListKeys(string appId)
    {
        var values = Read(appId);
        return values.Keys.Order(StringComparer.Ordinal).ToArray();
    }

    public async Task ReplaceAsync(string appId, IReadOnlyDictionary<string, string?> replacements, CancellationToken cancellationToken = default)
    {
        ValidateAppId(appId);
        if (replacements.Count > MaximumSecretKeys) throw new ArgumentException($"At most {MaximumSecretKeys} secret keys may be changed at once.", nameof(replacements));
        var gate = _writeLocks.GetOrAdd(appId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var values = Read(appId);
            foreach (var (key, value) in replacements)
            {
                ValidateKey(key);
                if (value is null) values.Remove(key);
                else
                {
                    if (value.Length > 64 * 1024) throw new ArgumentException($"Secret '{key}' is too large.", nameof(replacements));
                    if (value.Contains('\0')) throw new ArgumentException($"Secret '{key}' contains a null character.", nameof(replacements));
                    values[key] = value;
                }
            }
            if (values.Count > MaximumSecretKeys)
                throw new ArgumentException($"An application may register at most {MaximumSecretKeys} secret keys.", nameof(replacements));

            Directory.CreateDirectory(paths.SecretsRoot);
            var destination = GetPath(appId);
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var content = string.Join(Environment.NewLine, values.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={Escape(pair.Value)}")) + (values.Count == 0 ? string.Empty : Environment.NewLine);
            try
            {
                WindowsSecretFilePermissions? permissions = null;
                if (OperatingSystem.IsWindows())
                {
                    permissions = WindowsSecretFilePermissions.Capture(paths.SecretsRoot);
                    if (File.Exists(destination)) permissions.ValidateExisting(destination);

                    // Secure an empty file before writing any secret bytes. Truncating
                    // that same file below preserves its protected security descriptor.
                    using (new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                    permissions.Apply(temporary);
                }

                await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), cancellationToken);
                if (OperatingSystem.IsWindows() && permissions is not null) permissions.ValidateExisting(temporary);
                File.Move(temporary, destination, true);
                if (OperatingSystem.IsWindows() && permissions is not null) permissions.ValidateExisting(destination);
                Interlocked.Increment(ref _changeVersion);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        finally { gate.Release(); }
    }

    public IReadOnlyDictionary<string, string> ReadForProcess(string appId) => Read(appId);

    public async Task DeleteAsync(string appId, CancellationToken cancellationToken = default)
    {
        ValidateAppId(appId);
        var gate = _writeLocks.GetOrAdd(appId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var path = GetPath(appId);
            if (!File.Exists(path)) return;
            if (OperatingSystem.IsWindows())
            {
                WindowsSecretFilePermissions.Capture(paths.SecretsRoot).ValidateExisting(path);
            }
            File.Delete(path);
            Interlocked.Increment(ref _changeVersion);
        }
        finally { gate.Release(); }
    }

    public byte[] CreateNamesOnlyExample(string appId)
    {
        ValidateAppId(appId);
        var path = GetPath(appId);
        if (!File.Exists(path)) return [];
        ValidateReadableSecretFile(path);

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawLine in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(rawLine) || rawLine.TrimStart().StartsWith('#')) continue;
            var separator = rawLine.IndexOf('=');
            if (separator <= 0) continue;
            var key = rawLine[..separator].Trim();
            ValidateKey(key);
            keys.Add(key);
            if (keys.Count > MaximumSecretKeys)
                throw new InvalidDataException($"A protected environment file contains more than {MaximumSecretKeys} keys.");
        }

        var content = string.Join('\n', keys.Order(StringComparer.Ordinal).Select(key => key + "="));
        if (content.Length > 0) content += '\n';
        var result = new UTF8Encoding(false).GetBytes(content);
        if (result.Length > MaximumExampleBytes)
            throw new InvalidDataException("The generated environment example exceeds AbodeToWeb's safe response limit.");
        return result;
    }

    private Dictionary<string, string> Read(string appId)
    {
        ValidateAppId(appId);
        var path = GetPath(appId);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return values;
        ValidateReadableSecretFile(path);
        foreach (var rawLine in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(rawLine) || rawLine.TrimStart().StartsWith('#')) continue;
            var separator = rawLine.IndexOf('=');
            if (separator <= 0) continue;
            var key = rawLine[..separator].Trim();
            ValidateKey(key);
            values[key] = Unescape(rawLine[(separator + 1)..]);
        }
        return values;
    }

    private string GetPath(string appId) => Path.Combine(paths.SecretsRoot, appId + ".env");

    private void ValidateReadableSecretFile(string path)
    {
        if (OperatingSystem.IsWindows())
            WindowsSecretFilePermissions.Capture(paths.SecretsRoot).ValidateExisting(path);
        if (new FileInfo(path).Length > MaximumSecretFileBytes)
            throw new InvalidDataException("A protected environment file exceeds AbodeToWeb's safe size limit.");
    }

    private static string Escape(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).Insert(0, "base64:");

    private static string Unescape(string value)
    {
        if (!value.StartsWith("base64:", StringComparison.Ordinal)) return value;
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(value[7..])); }
        catch (FormatException) { throw new InvalidDataException("A protected environment file contains invalid base64 data."); }
    }

    private static void ValidateAppId(string appId)
    {
        if (!IsValidApplicationId(appId)) throw new ArgumentException("Application ID must be a lowercase slug.", nameof(appId));
    }

    private static void ValidateKey(string key)
    {
        if (!EnvironmentKeyPattern().IsMatch(key)) throw new ArgumentException($"'{key}' is not a valid environment variable name.", nameof(key));
        if (!EnvironmentVariablePolicy.IsAllowedSecretKey(key))
            throw new ArgumentException($"'{key}' can alter process loading or AbodeToWeb control variables and cannot be managed as a browser-set secret.", nameof(key));
    }

    [GeneratedRegex("^[a-z](?:[a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex AppIdPattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,127}$")]
    private static partial Regex EnvironmentKeyPattern();
}
