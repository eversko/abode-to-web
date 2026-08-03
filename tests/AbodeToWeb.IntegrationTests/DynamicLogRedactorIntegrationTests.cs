using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using AbodeToWeb.Core.Configuration;
using AbodeToWeb.Core.Validation;
using AbodeToWeb.Infrastructure.Configuration;
using AbodeToWeb.Infrastructure.Security;
using AbodeToWeb.Server.Services;

namespace AbodeToWeb.IntegrationTests;

public sealed class DynamicLogRedactorIntegrationTests
{
    [Fact]
    public async Task Untrusted_secret_acl_suppresses_the_entire_log_entry()
    {
        if (!OperatingSystem.IsWindows()) return;
        await VerifyUntrustedSecretAclIsSuppressedAsync();
    }

    [SupportedOSPlatform("windows")]
    private static async Task VerifyUntrustedSecretAclIsSuppressedAsync()
    {
        using var temporary = new TemporaryAbodeToWebDirectory();
        using var manifest = new ManifestProvider(
            temporary.Paths, new YamlManifestParser(), _ => new ValidationResult());
        await manifest.WriteAtomicAsync(new AbodeToWebManifest
        {
            ManifestVersion = 1,
            AbodeToWeb = new AbodeToWebOptions { ProjectsRoots = [temporary.Root] },
            Apps =
            [
                new AppManifest
                {
                    Id = "sample-static",
                    Name = "Sample Static",
                    Enabled = false,
                    Deployment = new DeploymentOptions
                    {
                        Adapter = DeploymentAdapter.Static,
                        StaticOutputDirectory = "."
                    },
                    Health = new HealthCheckOptions { Type = HealthCheckType.None },
                    Visibility = AppVisibility.Disabled
                }
            ]
        });
        var secrets = new SecretFileStore(temporary.Paths);
        await secrets.ReplaceAsync("sample-static", new Dictionary<string, string?> { ["API_TOKEN"] = "never-emit-this" });
        var secretPath = Path.Combine(temporary.Paths.SecretsRoot, "sample-static.env");
        var security = new FileInfo(secretPath).GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.Read,
            AccessControlType.Allow));
        new FileInfo(secretPath).SetAccessControl(security);

        var redactor = new DynamicLogRedactor(manifest, secrets);
        var output = redactor.Redact("message containing never-emit-this");

        Assert.Equal("[Log entry suppressed because secret redaction could not be verified.]", output);
        Assert.DoesNotContain("never-emit-this", output, StringComparison.Ordinal);
    }
}
