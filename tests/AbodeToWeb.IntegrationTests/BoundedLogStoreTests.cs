using System.Text;
using AbodeToWeb.Infrastructure.Logging;

namespace AbodeToWeb.IntegrationTests;

public sealed class BoundedLogStoreTests
{
    [Fact]
    public async Task Bounded_line_reader_preserves_normal_multibyte_lines()
    {
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes("normal €\r\nsecond 界\n"));
        using var reader = new StreamReader(input, Encoding.UTF8);
        var lines = new List<string>();

        await foreach (var line in BoundedTextLineReader.ReadLinesAsync(reader)) lines.Add(line);

        Assert.Equal(["normal €", "second 界"], lines);
    }

    [Fact]
    public async Task Bounded_line_reader_respects_cancellation()
    {
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes("must not be read\n"));
        using var reader = new StreamReader(input, Encoding.UTF8);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in BoundedTextLineReader.ReadLinesAsync(reader, cancellation.Token))
            {
            }
        });
    }

    [Fact]
    public async Task Oversized_direct_append_is_truncated_and_never_exceeds_file_quota()
    {
        using var temporary = new TemporaryAbodeToWebDirectory();
        const long maximumLogFileBytes = 64 * 1024;
        var store = new BoundedLogStore(
            temporary.Paths,
            new LogStreamHub(),
            maxFileBytes: maximumLogFileBytes,
            retainedFiles: 1);
        // Control characters expand when JSON encoded, exercising the serialized-byte
        // cap in addition to the in-memory message cap.
        var oversizedMessage = new string('\u0001', 4 * 1024 * 1024);

        await store.AppendAsync(new AbodeToWebLogEvent(
            DateTimeOffset.UtcNow,
            AbodeToWebLogKind.Runtime,
            AbodeToWebLogSeverity.Information,
            "oversized-direct",
            oversizedMessage,
            "operation",
            "stdout"));
        await store.AppendAsync(new AbodeToWebLogEvent(
            DateTimeOffset.UtcNow,
            AbodeToWebLogKind.Runtime,
            AbodeToWebLogSeverity.Information,
            "oversized-direct",
            "normal line €",
            "operation",
            "stdout"));

        var entries = await store.QueryAsync(new LogQuery(
            AbodeToWebLogKind.Runtime,
            "oversized-direct",
            Limit: 10));
        var oversized = Assert.Single(entries, item => item.Message.Contains("truncated", StringComparison.OrdinalIgnoreCase));
        Assert.Equal('\u0001', oversized.Message[0]);
        Assert.True(oversized.Message.Length < oversizedMessage.Length);
        Assert.Contains(entries, item => item.Message == "normal line €");
        Assert.All(
            Directory.EnumerateFiles(temporary.Paths.LogsRoot, "*.jsonl*", SearchOption.AllDirectories),
            path => Assert.True(new FileInfo(path).Length <= maximumLogFileBytes,
                $"Log file '{path}' exceeded its {maximumLogFileBytes}-byte quota."));
    }
}
