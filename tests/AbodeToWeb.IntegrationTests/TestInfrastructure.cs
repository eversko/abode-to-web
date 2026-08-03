using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using AbodeToWeb.Infrastructure;

namespace AbodeToWeb.IntegrationTests;

internal sealed class TemporaryAbodeToWebDirectory : IDisposable
{
    private bool _disposed;

    internal TemporaryAbodeToWebDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "AbodeToWeb.IntegrationTests", Guid.NewGuid().ToString("N"));
        Paths = new AbodeToWebPaths
        {
            DataRoot = Root,
            ConfigurationFile = Path.Combine(Root, "config", "abode-to-web.yaml"),
            DatabaseFile = Path.Combine(Root, "state", "abode-to-web.db"),
            LogsRoot = Path.Combine(Root, "logs"),
            DeploymentsRoot = Path.Combine(Root, "deployments"),
            SecretsRoot = Path.Combine(Root, "secrets"),
            BackupsRoot = Path.Combine(Root, "backups")
        };
        Paths.EnsureDirectories();
    }

    internal string Root { get; }

    internal AbodeToWebPaths Paths { get; }

    internal string GetPath(params string[] components) =>
        components.Aggregate(Root, Path.Combine);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // SQLite's native busy timeout is five seconds. A cancellation can
        // finish just after the test body and return a handle to the pool, so
        // allow cleanup to outlast that bounded native operation.
        const int maximumDeleteAttempts = 20;
        for (var attempt = 0; attempt < maximumDeleteAttempts && Directory.Exists(Root); attempt++)
        {
            try
            {
                // A canceled background operation can return its connection to
                // the pool after an earlier clear. Clear on every retry so that
                // late-returned handles cannot keep the test database locked.
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException) when (attempt < maximumDeleteAttempts - 1)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
            catch (UnauthorizedAccessException) when (attempt < maximumDeleteAttempts - 1)
            {
                ClearReadOnlyAttributes(Root);
                Thread.Sleep(50 * (attempt + 1));
            }
        }
        if (Directory.Exists(Root))
            throw new IOException($"Temporary integration-test directory remained locked after {maximumDeleteAttempts} cleanup attempts: {Root}");
    }

    private static void ClearReadOnlyAttributes(string root)
    {
        // Git marks pack/object files read-only, which blocks recursive deletes on Windows.
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}

internal static class IntegrationTestWait
{
    internal static async Task UntilAsync(
        Func<bool> condition,
        TimeSpan timeout,
        string failureMessage,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (condition())
            {
                return;
            }

            await Task.Delay(25, cancellationToken);
        }

        Assert.Fail(failureMessage);
    }

    internal static async Task UntilAsync(
        Func<Task<bool>> condition,
        TimeSpan timeout,
        string failureMessage,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await condition())
            {
                return;
            }

            await Task.Delay(25, cancellationToken);
        }

        Assert.Fail(failureMessage);
    }

    internal static async Task<T> UntilAsync<T>(
        Func<T?> probe,
        TimeSpan timeout,
        string failureMessage,
        CancellationToken cancellationToken = default)
        where T : class
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = probe();
            if (result is not null)
            {
                return result;
            }

            await Task.Delay(25, cancellationToken);
        }

        throw new Xunit.Sdk.XunitException(failureMessage);
    }

    internal static int GetAvailableLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
