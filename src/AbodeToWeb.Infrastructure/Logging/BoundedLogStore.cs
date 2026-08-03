using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace AbodeToWeb.Infrastructure.Logging;

public sealed class BoundedLogStore
{
    private const int MaximumStoredMessageCharacters = 32 * 1024;
    private const int MaximumSerializedEventBytes = 64 * 1024;
    private const int MaximumOperationIdCharacters = 256;
    private const int MaximumStreamCharacters = 64;
    private const string EventTruncationMarker = " [truncated: log event exceeded storage limit]";
    private readonly AbodeToWebPaths _paths;
    private readonly LogStreamHub _hub;
    private readonly Func<string, string> _redact;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly long _maxFileBytes;
    private readonly int _retainedFiles;

    public BoundedLogStore(
        AbodeToWebPaths paths,
        LogStreamHub hub,
        Func<string, string>? redact = null,
        long maxFileBytes = 10 * 1024 * 1024,
        int retainedFiles = 5)
    {
        _paths = paths;
        _hub = hub;
        _redact = redact ?? (value => value);
        _maxFileBytes = Math.Max(64 * 1024, maxFileBytes);
        _retainedFiles = Math.Clamp(retainedFiles, 1, 20);
    }

    public async Task AppendAsync(AbodeToWebLogEvent logEvent, CancellationToken cancellationToken = default)
    {
        var safeEvent = logEvent with
        {
            Source = SanitizeSource(logEvent.Source),
            Message = TruncateWithMarker(
                _redact(logEvent.Message),
                MaximumStoredMessageCharacters,
                EventTruncationMarker),
            OperationId = TruncateOptionalWithMarker(logEvent.OperationId, MaximumOperationIdCharacters, EventTruncationMarker),
            Stream = TruncateOptionalWithMarker(logEvent.Stream, MaximumStreamCharacters, EventTruncationMarker)
        };
        var (boundedEvent, line) = SerializeBounded(safeEvent);
        var path = GetPath(safeEvent.Kind, safeEvent.Source);
        var fileLock = _fileLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await fileLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var currentLength = File.Exists(path) ? new FileInfo(path).Length : 0;
            if (currentLength > 0
                && (currentLength >= _maxFileBytes || line.Length > _maxFileBytes - currentLength))
                Rotate(path);

            await using var destination = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await destination.WriteAsync(line, cancellationToken);
        }
        finally
        {
            fileLock.Release();
        }
        _hub.Publish(boundedEvent);
    }

    public async Task<IReadOnlyList<AbodeToWebLogEvent>> QueryAsync(LogQuery query, CancellationToken cancellationToken = default)
    {
        var path = GetPath(query.Kind, SanitizeSource(query.Source));
        var limit = Math.Clamp(query.Limit, 1, 5_000);
        var fileLock = _fileLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await fileLock.WaitAsync(cancellationToken);
        try
        {
            // Keep enumeration and reading under the same lock used by append/rotation.
            // On Windows, the convenience file APIs do not guarantee compatible sharing
            // modes, and a query racing an append otherwise intermittently fails.
            var files = EnumerateFiles(path).Reverse().ToArray();
            var selected = new Queue<AbodeToWebLogEvent>(limit);
            foreach (var file in files)
            {
                await foreach (var line in File.ReadLinesAsync(file, cancellationToken))
                {
                    AbodeToWebLogEvent? item;
                    try { item = JsonSerializer.Deserialize<AbodeToWebLogEvent>(line, JsonOptions); }
                    catch (JsonException) { continue; }
                    if (item is null) continue;
                    if (query.MinimumSeverity is not null && item.Severity < query.MinimumSeverity) continue;
                    if (!string.IsNullOrWhiteSpace(query.Search)
                        && !item.Message.Contains(query.Search, StringComparison.OrdinalIgnoreCase)) continue;
                    if (selected.Count == limit) selected.Dequeue();
                    selected.Enqueue(item);
                }
            }
            return selected.ToArray();
        }
        finally
        {
            fileLock.Release();
        }
    }

    public async Task CopyBoundedSegmentAsync(LogQuery query, Stream destination, CancellationToken cancellationToken = default)
    {
        var entries = await QueryAsync(query with { Limit = Math.Clamp(query.Limit, 1, 10_000) }, cancellationToken);
        await using var writer = new StreamWriter(destination, new UTF8Encoding(false), leaveOpen: true);
        foreach (var entry in entries)
        {
            await writer.WriteLineAsync($"{entry.Timestamp:O} [{entry.Severity}] [{entry.Stream ?? entry.Kind.ToString()}] {entry.Message}");
        }
        await writer.FlushAsync(cancellationToken);
    }

    public void EnforceTotalQuota(long maximumBytes)
    {
        maximumBytes = Math.Max(_maxFileBytes, maximumBytes);
        if (!Directory.Exists(_paths.LogsRoot)) return;
        var files = new DirectoryInfo(_paths.LogsRoot)
            .EnumerateFiles("*.jsonl*", SearchOption.AllDirectories)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToList();
        long total = files.Sum(file => file.Length);
        foreach (var file in files.OrderBy(file => file.LastWriteTimeUtc))
        {
            if (total <= maximumBytes) break;
            if (file.Name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) continue;
            var length = file.Length;
            file.Delete();
            total -= length;
        }
    }

    public void PruneOlderThan(DateTimeOffset cutoff)
    {
        if (!Directory.Exists(_paths.LogsRoot)) return;
        foreach (var file in new DirectoryInfo(_paths.LogsRoot).EnumerateFiles("*.jsonl.*", SearchOption.AllDirectories))
        {
            if (file.LastWriteTimeUtc < cutoff.UtcDateTime) file.Delete();
        }
    }

    private string GetPath(AbodeToWebLogKind kind, string source)
    {
        var category = kind.ToString().ToLowerInvariant();
        return Path.Combine(_paths.LogsRoot, category, source + ".jsonl");
    }

    private IEnumerable<string> EnumerateFiles(string currentPath)
    {
        for (var index = _retainedFiles; index >= 1; index--)
        {
            var path = currentPath + "." + index;
            if (File.Exists(path)) yield return path;
        }
        if (File.Exists(currentPath)) yield return currentPath;
    }

    private void Rotate(string path)
    {
        var oldest = path + "." + _retainedFiles;
        if (File.Exists(oldest)) File.Delete(oldest);
        for (var index = _retainedFiles - 1; index >= 1; index--)
        {
            var source = path + "." + index;
            if (File.Exists(source)) File.Move(source, path + "." + (index + 1), true);
        }
        File.Move(path, path + ".1", true);
    }

    private (AbodeToWebLogEvent Event, byte[] SerializedLine) SerializeBounded(AbodeToWebLogEvent logEvent)
    {
        var maximumBytes = (int)Math.Min(_maxFileBytes, MaximumSerializedEventBytes);
        var serialized = SerializeLine(logEvent);
        if (serialized.Length <= maximumBytes) return (logEvent, serialized);

        var content = logEvent.Message.EndsWith(EventTruncationMarker, StringComparison.Ordinal)
            ? logEvent.Message[..^EventTruncationMarker.Length]
            : logEvent.Message;
        var fallback = logEvent with { Message = EventTruncationMarker.TrimStart() };
        var fallbackBytes = SerializeLine(fallback);
        if (fallbackBytes.Length > maximumBytes)
        {
            fallback = fallback with { OperationId = null, Stream = null };
            fallbackBytes = SerializeLine(fallback);
        }

        var low = 0;
        var high = content.Length;
        var bestEvent = fallback;
        var bestBytes = fallbackBytes;
        while (low <= high)
        {
            var requestedLength = low + ((high - low) / 2);
            var prefixLength = SafePrefixLength(content, requestedLength);
            var candidate = logEvent with
            {
                Message = content[..prefixLength] + EventTruncationMarker
            };
            var candidateBytes = SerializeLine(candidate);
            if (candidateBytes.Length <= maximumBytes)
            {
                bestEvent = candidate;
                bestBytes = candidateBytes;
                low = requestedLength + 1;
            }
            else
            {
                high = requestedLength - 1;
            }
        }

        return (bestEvent, bestBytes);
    }

    private static byte[] SerializeLine(AbodeToWebLogEvent logEvent)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(logEvent, JsonOptions);
        var line = new byte[json.Length + NewLineBytes.Length];
        json.CopyTo(line, 0);
        NewLineBytes.CopyTo(line, json.Length);
        return line;
    }

    private static string TruncateWithMarker(string value, int maximumCharacters, string marker)
    {
        if (value.Length <= maximumCharacters) return value;
        var prefixLength = SafePrefixLength(value, maximumCharacters - marker.Length);
        return value[..prefixLength] + marker;
    }

    private static string? TruncateOptionalWithMarker(string? value, int maximumCharacters, string marker) =>
        value is null ? null : TruncateWithMarker(value, maximumCharacters, marker);

    private static int SafePrefixLength(string value, int requestedLength)
    {
        var length = Math.Clamp(requestedLength, 0, value.Length);
        if (length > 0 && length < value.Length
            && char.IsHighSurrogate(value[length - 1]) && char.IsLowSurrogate(value[length]))
            length--;
        return length;
    }

    private static string SanitizeSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return "abode-to-web";
        var invalid = Path.GetInvalidFileNameChars();
        var value = new string(source.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return value.Length > 80 ? value[..80] : value;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly byte[] NewLineBytes = Encoding.UTF8.GetBytes(Environment.NewLine);
}

internal static class BoundedTextLineReader
{
    internal const int MaximumLineCharacters = 16 * 1024;
    internal const string TruncationMarker = " [truncated: oversized process output line]";

    internal static async IAsyncEnumerable<string> ReadLinesAsync(
        StreamReader reader,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var buffer = new char[4096];
        var line = new StringBuilder(buffer.Length);
        var discardingRemainder = false;
        var consumeLineFeed = false;

        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0) break;

            for (var index = 0; index < count; index++)
            {
                var character = buffer[index];
                if (consumeLineFeed)
                {
                    consumeLineFeed = false;
                    if (character == '\n') continue;
                }

                if (character is '\r' or '\n')
                {
                    if (!discardingRemainder) yield return line.ToString();
                    line.Clear();
                    discardingRemainder = false;
                    consumeLineFeed = character == '\r';
                    continue;
                }

                if (discardingRemainder) continue;
                if (line.Length < MaximumLineCharacters)
                {
                    line.Append(character);
                    continue;
                }

                if (char.IsLowSurrogate(character) && line.Length > 0 && char.IsHighSurrogate(line[^1]))
                    line.Length--;
                yield return line + TruncationMarker;
                line.Clear();
                discardingRemainder = true;
            }
        }

        if (!discardingRemainder && line.Length > 0) yield return line.ToString();
    }
}
