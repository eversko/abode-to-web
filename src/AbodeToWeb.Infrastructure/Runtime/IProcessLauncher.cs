using System.Diagnostics;

namespace AbodeToWeb.Infrastructure.Runtime;

/// <summary>
/// Launches a process under the requested AbodeToWeb isolation boundary.
/// Production code uses <see cref="DedicatedRuntimeProcessLauncher"/>; tests can inject an explicit substitute.
/// </summary>
public interface IProcessLauncher
{
    LaunchedProcess Start(ProcessStartInfo startInfo, string jobName, string? isolationKey);
}

public sealed class DedicatedRuntimeProcessLauncher : IProcessLauncher
{
    public static DedicatedRuntimeProcessLauncher Instance { get; } = new();

    private DedicatedRuntimeProcessLauncher() { }

    public LaunchedProcess Start(ProcessStartInfo startInfo, string jobName, string? isolationKey) =>
        SuspendedProcessLauncher.Start(startInfo, jobName, isolationKey);
}
