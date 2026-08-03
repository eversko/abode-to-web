using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using AbodeToWeb.Core.Processes;
using AbodeToWeb.Infrastructure.Runtime;

namespace AbodeToWeb.IntegrationTests;

public sealed class SuspendedProcessLauncherTests
{
    private static readonly Lock EnvironmentLock = new();

    [Fact]
    public async Task Trusted_launch_redirects_output_and_resumes_after_job_assignment()
    {
        if (!OperatingSystem.IsWindows()) return;

        var command = new CommandSpec
        {
            Executable = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = ["/d", "/c", "echo abode-to-web-launch-ok"],
            TimeoutSeconds = 10
        };
        var startInfo = ProcessStartInfoFactory.Create(command, Path.GetTempPath(), inheritParentEnvironment: true);

        using var launched = SuspendedProcessLauncher.Start(startInfo, "trusted-launch-test", isolationKey: null);
        await launched.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var output = await launched.StandardOutput.ReadToEndAsync();

        Assert.Equal(0, launched.Process.ExitCode);
        Assert.Contains("abode-to-web-launch-ok", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_one_native_wait_does_not_lose_the_process_exit_signal()
    {
        if (!OperatingSystem.IsWindows()) return;

        var command = new CommandSpec
        {
            Executable = Path.Combine(Environment.SystemDirectory, "ping.exe"),
            Arguments = ["127.0.0.1", "-n", "2"],
            TimeoutSeconds = 10
        };
        var startInfo = ProcessStartInfoFactory.Create(command, Path.GetTempPath(), inheritParentEnvironment: true);

        using var launched = SuspendedProcessLauncher.Start(startInfo, "cancelled-wait-test", isolationKey: null);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => launched.Process.WaitForExitAsync(cancellation.Token));
        await launched.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(launched.Process.HasExited);
        Assert.Equal(0, launched.Process.ExitCode);
    }

    [Fact]
    public void Isolated_launch_fails_closed_when_runtime_credential_is_missing()
    {
        if (!OperatingSystem.IsWindows()) return;

        var command = new CommandSpec
        {
            Executable = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = ["/d", "/c", "exit 0"],
            TimeoutSeconds = 10
        };
        var startInfo = ProcessStartInfoFactory.Create(command, Path.GetTempPath());

        lock (EnvironmentLock)
        {
            var original = Environment.GetEnvironmentVariable("ABODE_TO_WEB_RUNTIME_CREDENTIAL_PATH");
            try
            {
                Environment.SetEnvironmentVariable("ABODE_TO_WEB_RUNTIME_CREDENTIAL_PATH", null);
                var exception = Assert.Throws<RuntimeCredentialException>(() =>
                    SuspendedProcessLauncher.Start(startInfo, "isolated-launch-test", "test-app"));
                Assert.Contains("dedicated runtime identity is not configured", exception.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Environment.SetEnvironmentVariable("ABODE_TO_WEB_RUNTIME_CREDENTIAL_PATH", original);
            }
        }
    }

    [Fact]
    public async Task Provisioned_runtime_identity_can_launch_when_machine_probe_is_enabled()
    {
        if (!OperatingSystem.IsWindows()) return;
        var credentialPath = Environment.GetEnvironmentVariable("ABODE_TO_WEB_RUNTIME_CREDENTIAL_PATH");
        var probeRoot = Environment.GetEnvironmentVariable("ABODE_TO_WEB_RUNTIME_TEST_ROOT");
        if (string.IsNullOrWhiteSpace(credentialPath) || string.IsNullOrWhiteSpace(probeRoot)) return;

        var workingDirectory = Path.Combine(Path.GetFullPath(probeRoot), $"probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var command = new CommandSpec
            {
                Executable = Path.Combine(Environment.SystemDirectory, "whoami.exe"),
                Arguments = [],
                TimeoutSeconds = 10
            };
            var startInfo = ProcessStartInfoFactory.Create(command, workingDirectory);

            using var launched = SuspendedProcessLauncher.Start(startInfo, "provisioned-runtime-probe", "runtime-probe");
            await launched.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var output = await launched.StandardOutput.ReadToEndAsync();

            Assert.Equal(0, launched.Process.ExitCode);
            Assert.Contains("AbodeToWebRuntime", output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(workingDirectory, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData("{\"version\":1,\"protection\":\"dpapi-local-machine-v1\",\"username\":\"AbodeToWebRuntime\",\"domain\":\".\",\"protectedPassword\":\"AQ==\",\"unknown\":true}")]
    [InlineData("{\"version\":1,\"version\":1,\"protection\":\"dpapi-local-machine-v1\",\"username\":\"AbodeToWebRuntime\",\"domain\":\".\",\"protectedPassword\":\"AQ==\"}")]
    [InlineData("{\"version\":1,\"protection\":\"dpapi-local-machine-v1\",\"username\":\"AbodeToWebRuntime\",\"protectedPassword\":\"AQ==\"}")]
    public void Credential_envelope_rejects_unknown_duplicate_and_missing_fields(string json)
    {
        if (!OperatingSystem.IsWindows()) return;

        var path = Path.Combine(Path.GetTempPath(), $"abode-to-web-runtime-credential-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        RestrictTestFile(path);
        var command = new CommandSpec
        {
            Executable = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = ["/d", "/c", "exit 0"],
            TimeoutSeconds = 10
        };
        var startInfo = ProcessStartInfoFactory.Create(command, Path.GetTempPath());

        lock (EnvironmentLock)
        {
            var original = Environment.GetEnvironmentVariable("ABODE_TO_WEB_RUNTIME_CREDENTIAL_PATH");
            try
            {
                Environment.SetEnvironmentVariable("ABODE_TO_WEB_RUNTIME_CREDENTIAL_PATH", path);
                var exception = Assert.Throws<RuntimeCredentialException>(() =>
                    SuspendedProcessLauncher.Start(startInfo, "credential-schema-test", "test-app"));
                Assert.Contains("version 1 schema", exception.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Environment.SetEnvironmentVariable("ABODE_TO_WEB_RUNTIME_CREDENTIAL_PATH", original);
                File.Delete(path);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictTestFile(string path)
    {
        var currentSid = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("The test identity has no SID.");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(currentSid);
        security.AddAccessRule(new FileSystemAccessRule(currentSid, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }
}
