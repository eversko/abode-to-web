using System.Diagnostics;
using AbodeToWeb.Infrastructure.Runtime;

namespace AbodeToWeb.IntegrationTests;

/// <summary>
/// Explicit test seam. Production code always defaults to DedicatedRuntimeProcessLauncher.
/// </summary>
internal sealed class CurrentIdentityTestProcessLauncher : IProcessLauncher
{
    public LaunchedProcess Start(ProcessStartInfo startInfo, string jobName, string? isolationKey) =>
        SuspendedProcessLauncher.Start(startInfo, jobName, isolationKey: null);
}
