using AbodeToWeb.Core.Security;
using AbodeToWeb.Infrastructure.Configuration;
using AbodeToWeb.Infrastructure.Security;

namespace AbodeToWeb.Server.Services;

public sealed class DynamicLogRedactor(ManifestProvider manifest, SecretFileStore secrets) : ILogRedactor
{
    private readonly Lock _lock = new();
    private SecretRedactor _current = new();
    private DateTimeOffset _expiresAt;
    private long _loadedSecretVersion = -1;
    private bool _suppress;

    public string Redact(string text)
    {
        SecretRedactor redactor;
        lock (_lock)
        {
            if (DateTimeOffset.UtcNow >= _expiresAt || _loadedSecretVersion != secrets.ChangeVersion)
            {
                var values = new List<SecretValue>();
                var suppress = false;
                foreach (var app in manifest.Current.Apps)
                {
                    try
                    {
                        foreach (var pair in secrets.ReadForProcess(app.Id)) values.Add(new SecretValue(pair.Key, pair.Value));
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                         or ArgumentException or System.Security.SecurityException)
                    {
                        // If any protected file cannot be trusted, emitting the
                        // original entry could disclose an unknown secret. Do not
                        // log this failure through the same redactor recursively.
                        suppress = true;
                        values.Clear();
                        break;
                    }
                }
                _current = new SecretRedactor(values);
                _suppress = suppress;
                _loadedSecretVersion = secrets.ChangeVersion;
                _expiresAt = DateTimeOffset.UtcNow.AddSeconds(15);
            }
            if (_suppress) return "[Log entry suppressed because secret redaction could not be verified.]";
            redactor = _current;
        }
        return redactor.Redact(text);
    }

    public void Invalidate()
    {
        lock (_lock)
        {
            _expiresAt = DateTimeOffset.MinValue;
            _loadedSecretVersion = -1;
        }
    }
}
