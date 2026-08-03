using System.Text;
using AbodeToWeb.Core.Configuration;
using AbodeToWeb.Core.Validation;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AbodeToWeb.Infrastructure.Configuration;

public sealed record ManifestLoadResult(bool Success, AbodeToWebManifest? Manifest, IReadOnlyList<ValidationIssue> Errors, bool UsedLastKnownGood);

public sealed class ManifestProvider : IDisposable
{
    private readonly AbodeToWebPaths _paths;
    private readonly YamlManifestParser _parser;
    private readonly Func<AbodeToWebManifest, ValidationResult> _validate;
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private HashSet<string> _retiredProjectIds = new(StringComparer.OrdinalIgnoreCase);
    private AbodeToWebManifest? _current;
    private AbodeToWebManifest? _persistedCurrent;

    public ManifestProvider(AbodeToWebPaths paths, YamlManifestParser parser, Func<AbodeToWebManifest, ValidationResult> validate)
    {
        _paths = paths;
        _parser = parser;
        _validate = validate;
    }

    public AbodeToWebManifest Current
    {
        get
        {
            _lock.EnterReadLock();
            try { return _current ?? throw new InvalidOperationException("AbodeToWeb configuration has not been loaded."); }
            finally { _lock.ExitReadLock(); }
        }
    }

    public void SetRetiredProjectIds(IEnumerable<string> appIds)
    {
        ArgumentNullException.ThrowIfNull(appIds);
        _lock.EnterWriteLock();
        try { _retiredProjectIds = appIds.ToHashSet(StringComparer.OrdinalIgnoreCase); }
        finally { _lock.ExitWriteLock(); }
    }

    public void RetireProjectId(string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        _lock.EnterWriteLock();
        try { _retiredProjectIds.Add(appId); }
        finally { _lock.ExitWriteLock(); }
    }

    public bool IsProjectIdRetired(string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        _lock.EnterReadLock();
        try { return _retiredProjectIds.Contains(appId); }
        finally { _lock.ExitReadLock(); }
    }

    public IReadOnlyList<string> CurrentRetiredProjectIds()
    {
        _lock.EnterReadLock();
        try
        {
            if (_current is null || _retiredProjectIds.Count == 0) return [];
            return _current.Apps.Select(app => app.Id)
                .Where(_retiredProjectIds.Contains)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        finally { _lock.ExitReadLock(); }
    }

    public async Task<ManifestLoadResult> LoadAsync(bool allowLastKnownGood = true, CancellationToken cancellationToken = default)
    {
        try
        {
            var loaded = await ParseAndValidateAsync(_paths.ConfigurationFile, cancellationToken);
            SetCurrent(loaded.Persisted, loaded.Runtime);
            await SaveLastKnownGoodAsync(cancellationToken);
            return new ManifestLoadResult(true, loaded.Runtime, [], false);
        }
        catch (Exception exception) when (allowLastKnownGood && File.Exists(LastKnownGoodPath))
        {
            try
            {
                var fallback = await ParseAndValidateAsync(LastKnownGoodPath, cancellationToken);
                SetCurrent(fallback.Persisted, fallback.Runtime);
                return new ManifestLoadResult(false, fallback.Runtime, [ToError(exception)], true);
            }
            catch
            {
                return new ManifestLoadResult(false, null, [ToError(exception)], false);
            }
        }
        catch (Exception exception)
        {
            return new ManifestLoadResult(false, null, [ToError(exception)], false);
        }
    }

    public ValidationResult ValidateText(string text)
    {
        try { return Validate(_parser.Parse(text)); }
        catch (Exception exception)
        {
            var result = new ValidationResult();
            var error = ToError(exception);
            result.Add(error.Code, error.Path, error.Message);
            return result;
        }
    }

    /// <summary>
    /// Validates a manifest that will be persisted without resolving environment templates in-place.
    /// </summary>
    public ValidationResult ValidatePersisted(AbodeToWebManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        try { return Validate(_parser.Expand(manifest)); }
        catch (Exception exception)
        {
            var result = new ValidationResult();
            var error = ToError(exception);
            result.Add(error.Code, error.Path, error.Message);
            return result;
        }
    }

    public async Task WriteAtomicAsync(AbodeToWebManifest manifest, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try { await WriteAtomicCoreAsync(manifest, cancellationToken); }
        finally { _writeLock.Release(); }
    }

    public async Task<AbodeToWebManifest> UpdateAtomicAsync(
        Func<AbodeToWebManifest, AbodeToWebManifest> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var next = update(PersistedCurrent);
            await WriteAtomicCoreAsync(next, cancellationToken);
            return Current;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task WriteAtomicCoreAsync(AbodeToWebManifest manifest, CancellationToken cancellationToken)
    {
        var runtime = _parser.Expand(manifest);
        var validation = Validate(runtime);
        if (!validation.IsValid) throw new InvalidOperationException("Refusing to write an invalid AbodeToWeb manifest: " + validation.Errors.First().Message);
        _paths.EnsureDirectories();

        var serializer = new SerializerBuilder()
            .WithNamingConvention(ManifestNamingConvention.Instance)
            .WithEnumNamingConvention(HyphenatedNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .DisableAliases()
            .Build();
        var content = serializer.Serialize(manifest);
        var persistedRoundTrip = _parser.ParseUnexpanded(content);
        var roundTrip = _parser.Expand(persistedRoundTrip);
        var roundTripValidation = Validate(roundTrip);
        if (!roundTripValidation.IsValid) throw new InvalidOperationException("Serialized manifest did not pass validation.");

        var temporary = _paths.ConfigurationFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await WriteDurableTextAsync(temporary, content, cancellationToken);
            if (File.Exists(_paths.ConfigurationFile)) File.Replace(temporary, _paths.ConfigurationFile, null, ignoreMetadataErrors: true);
            else File.Move(temporary, _paths.ConfigurationFile);
        }
        finally
        {
            // A failed or cancelled write must not leave an apparently usable
            // manifest beside the authoritative file.
            File.Delete(temporary);
        }
        SetCurrent(persistedRoundTrip, roundTrip);
        await SaveLastKnownGoodAsync(cancellationToken);
    }

    private async Task<ParsedManifest> ParseAndValidateAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("AbodeToWeb configuration file was not found.", path);
        var text = await File.ReadAllTextAsync(path, cancellationToken);
        var persisted = _parser.ParseUnexpanded(text);
        var runtime = _parser.Expand(persisted);
        var validation = Validate(runtime);
        if (!validation.IsValid)
            throw new ManifestValidationException(validation.Errors.ToArray());
        return new ParsedManifest(persisted, runtime);
    }

    private ValidationResult Validate(AbodeToWebManifest manifest)
    {
        var result = _validate(manifest);
        HashSet<string> retired;
        _lock.EnterReadLock();
        try { retired = new HashSet<string>(_retiredProjectIds, StringComparer.OrdinalIgnoreCase); }
        finally { _lock.ExitReadLock(); }
        for (var index = 0; index < manifest.Apps.Count; index++)
        {
            if (retired.Contains(manifest.Apps[index].Id))
                result.Add("project.id.retired", $"apps[{index}].id",
                    $"Project ID '{manifest.Apps[index].Id}' was permanently retired and cannot be reused. Choose a new project ID.");
        }
        return result;
    }

    private async Task SaveLastKnownGoodAsync(CancellationToken cancellationToken)
    {
        var temporary = LastKnownGoodPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var source = new FileStream(_paths.ConfigurationFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             bufferSize: 4096, FileOptions.Asynchronous))
            {
                await source.CopyToAsync(destination, cancellationToken);
                await destination.FlushAsync(cancellationToken);
                destination.Flush(flushToDisk: true);
            }
            if (File.Exists(LastKnownGoodPath)) File.Replace(temporary, LastKnownGoodPath, null, ignoreMetadataErrors: true);
            else File.Move(temporary, LastKnownGoodPath);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static async Task WriteDurableTextAsync(string path, string content, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 4096, FileOptions.Asynchronous);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 4096, leaveOpen: true);
        await writer.WriteAsync(content.AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private AbodeToWebManifest PersistedCurrent
    {
        get
        {
            _lock.EnterReadLock();
            try { return _persistedCurrent ?? throw new InvalidOperationException("AbodeToWeb configuration has not been loaded."); }
            finally { _lock.ExitReadLock(); }
        }
    }

    private void SetCurrent(AbodeToWebManifest persisted, AbodeToWebManifest runtime)
    {
        _lock.EnterWriteLock();
        try
        {
            _persistedCurrent = persisted;
            _current = runtime;
        }
        finally { _lock.ExitWriteLock(); }
    }

    private string LastKnownGoodPath => _paths.ConfigurationFile + ".last-good";

    private sealed record ParsedManifest(AbodeToWebManifest Persisted, AbodeToWebManifest Runtime);

    private static ValidationIssue ToError(Exception exception) => exception switch
    {
        ManifestFormatException format => new ValidationIssue("manifest.format", format.Path ?? "$", format.Message),
        ManifestValidationException validation when validation.Errors.Count > 0 => validation.Errors[0],
        FileNotFoundException => new ValidationIssue("manifest.not_found", "$", "AbodeToWeb configuration file was not found."),
        _ => new ValidationIssue("manifest.read_failed", "$", "AbodeToWeb could not read the configuration file.")
    };

    public void Dispose()
    {
        _writeLock.Dispose();
        _lock.Dispose();
    }

    private sealed class ManifestNamingConvention : INamingConvention
    {
        internal static ManifestNamingConvention Instance { get; } = new();
        public string Apply(string value) => value.Equals(nameof(AbodeToWebManifest.AbodeToWeb), StringComparison.Ordinal)
            ? "abodeToWeb" : CamelCaseNamingConvention.Instance.Apply(value);
        public string Reverse(string value) => value.Equals("abodeToWeb", StringComparison.Ordinal)
            ? nameof(AbodeToWebManifest.AbodeToWeb)
            : CamelCaseNamingConvention.Instance.Reverse(value);
    }
}

public sealed class ManifestValidationException(IReadOnlyList<ValidationIssue> errors)
    : Exception("The AbodeToWeb manifest is invalid.")
{
    public IReadOnlyList<ValidationIssue> Errors { get; } = errors;
}
