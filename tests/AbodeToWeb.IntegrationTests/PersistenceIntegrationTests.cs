using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using AbodeToWeb.Core.Configuration;
using AbodeToWeb.Infrastructure.Gateway;
using AbodeToWeb.Infrastructure.Persistence;
using AbodeToWeb.Infrastructure.Security;
using Microsoft.Data.Sqlite;

namespace AbodeToWeb.IntegrationTests;

public sealed class PersistenceIntegrationTests
{
    [Fact]
    public async Task Startup_marks_unfinished_operations_as_interrupted_once()
    {
        using var temporary = new TemporaryAbodeToWebDirectory();
        var database = new AbodeToWebDatabase(temporary.Paths);
        await database.InitializeAsync();
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        await database.UpsertOperationAsync(new OperationRecord(
            "queued-op", "deploy", "sample-static", "queued", 0, "Queued", now, null, null, null, null));
        await database.UpsertOperationAsync(new OperationRecord(
            "running-op", "restart", "sample-docs", "running", 40, "Restarting", now, now, null, null, null));
        await database.UpsertOperationAsync(new OperationRecord(
            "complete-op", "stop", "demo", "succeeded", 100, "Complete", now, now, now, null, null));

        await new AbodeToWebDatabase(temporary.Paths).InitializeAsync();

        foreach (var id in new[] { "queued-op", "running-op" })
        {
            var interrupted = await database.GetOperationAsync(id);
            Assert.NotNull(interrupted);
            Assert.Equal("failed", interrupted.Status);
            Assert.Equal("operation.interrupted", interrupted.ErrorCode);
            Assert.NotNull(interrupted.CompletedUtc);
        }
        Assert.Equal("succeeded", (await database.GetOperationAsync("complete-op"))!.Status);
        var audit = await database.GetAuditEventsAsync();
        Assert.Equal(2, audit.Count(item => item.Outcome == "interrupted"));

        await new AbodeToWebDatabase(temporary.Paths).InitializeAsync();
        audit = await database.GetAuditEventsAsync();
        Assert.Equal(2, audit.Count(item => item.Outcome == "interrupted"));
    }

    [Fact]
    public async Task Initialize_applies_each_migration_once_and_is_idempotent()
    {
        using var temporary = new TemporaryAbodeToWebDirectory();
        var database = new AbodeToWebDatabase(temporary.Paths);

        await database.InitializeAsync();
        await database.InitializeAsync();

        await using var connection = new SqliteConnection($"Data Source={database.DatabaseFile}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT group_concat(version, ',') FROM (SELECT version FROM schema_migrations ORDER BY version);";
        var versions = Assert.IsType<string>(await command.ExecuteScalarAsync());
        Assert.Equal("1,2,3", versions);

        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('project_state','deployments','operations','audit_events','route_state','retired_projects');";
        Assert.Equal(6L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task State_records_round_trip_update_and_survive_online_backup()
    {
        using var temporary = new TemporaryAbodeToWebDirectory();
        var database = new AbodeToWebDatabase(temporary.Paths);
        await database.InitializeAsync();
        var now = new DateTimeOffset(2026, 7, 14, 18, 30, 0, TimeSpan.Zero);

        var project = new ProjectStateRecord(
            "sample-static", "running", "running", "healthy", "deployment-a", 1234, now.AddMinutes(-2),
            @"C:\Program Files\nodejs\node.exe", 1, null, now, null);
        await database.UpsertProjectStateAsync(project);
        var updatedProject = project with
        {
            RuntimeState = "stopped",
            HealthState = "unknown",
            ProcessId = null,
            ProcessStartedUtc = null,
            UpdatedUtc = now.AddMinutes(1),
            LastError = "Stopped for integration test."
        };
        await database.UpsertProjectStateAsync(updatedProject);

        var deployment = new DeploymentRecord(
            "deployment-a", "sample-static", "operation-a", new string('a', 40), "main",
            temporary.GetPath("deployments", "sample-static", "a"), "building", now.AddMinutes(-5), null,
            null, null, null, "Initial release", "AbodeToWeb Tests", false, null, 51234);
        await database.InsertDeploymentAsync(deployment);
        var completedDeployment = deployment with
        {
            State = "succeeded",
            CompletedUtc = now,
            DurationMilliseconds = 300_000,
            BuildOutcome = "succeeded",
            HealthOutcome = "healthy",
            IsKnownGood = true
        };
        await database.UpdateDeploymentAsync(completedDeployment);

        var operation = new OperationRecord(
            "operation-a", "deploy", "sample-static", "queued", 0, "Queued", now.AddMinutes(-5), null, null, null, null);
        await database.UpsertOperationAsync(operation);
        var completedOperation = operation with
        {
            Status = "succeeded",
            Progress = 100,
            Message = "Deployment complete",
            StartedUtc = now.AddMinutes(-5),
            CompletedUtc = now
        };
        await database.UpsertOperationAsync(completedOperation);

        var auditId = await database.AppendAuditAsync(new AuditEventRecord(
            0, now, "local-user", "deploy", "sample-static", "succeeded", "operation-a", "{\"commit\":\"aaaaaaa\"}", "127.0.0.1"));
        var route = new RouteStateRecord("sample-static", "sample-static.example.com", "deployment-a", 51234, "online", null, now);
        await database.UpsertRouteAsync(route);

        Assert.Equal(updatedProject, Assert.Single(await database.GetProjectStatesAsync()));
        Assert.Equal(completedDeployment, Assert.Single(await database.GetDeploymentsAsync("sample-static")));
        Assert.Equal(completedDeployment, await database.GetDeploymentAsync("deployment-a"));
        Assert.Equal(completedOperation, await database.GetOperationAsync("operation-a"));
        Assert.Equal(completedOperation, Assert.Single(await database.GetRecentOperationsAsync()));
        Assert.Equal(auditId, Assert.Single(await database.GetAuditEventsAsync()).Id);
        Assert.Equal(route, Assert.Single(await database.GetRoutesAsync()));

        var backupPath = temporary.GetPath("backups", "online.db");
        await database.BackupAsync(backupPath);

        var backupPaths = temporary.Paths with { DatabaseFile = backupPath };
        var backup = new AbodeToWebDatabase(backupPaths);
        Assert.Equal(updatedProject, Assert.Single(await backup.GetProjectStatesAsync()));
        Assert.Equal(completedDeployment, Assert.Single(await backup.GetDeploymentsAsync("sample-static")));
        Assert.Equal(route, Assert.Single(await backup.GetRoutesAsync()));
    }

    [Fact]
    public async Task Dynamic_route_is_not_restored_as_healthy_until_reconciliation_owns_the_process()
    {
        using var temporary = new TemporaryAbodeToWebDirectory();
        var database = new AbodeToWebDatabase(temporary.Paths);
        await database.InitializeAsync();
        await database.UpsertRouteAsync(new RouteStateRecord(
            "sample-static", "sample-static.example.com", "deployment-a", 51_001, "healthy", null, DateTimeOffset.UtcNow));
        var manifest = new AbodeToWebManifest
        {
            ManifestVersion = 1,
            AbodeToWeb = new AbodeToWebOptions { ProjectsRoots = [temporary.Root] },
            Apps =
            [
                new AppManifest
                {
                    Id = "sample-static",
                    Name = "Sample Static",
                    Deployment = new DeploymentOptions(),
                    Routing = new RoutingOptions { Hostname = "sample-static.example.com" }
                }
            ]
        };

        var registry = new GatewayRouteRegistry(database);
        await registry.InitializeAsync(manifest);

        Assert.True(registry.TryGetByApp("sample-static", out var route));
        Assert.Equal(GatewayRouteStatus.Starting, route.Status);
        Assert.Equal("starting", Assert.Single(await database.GetRoutesAsync()).Status);
    }

    [Fact]
    public async Task Concurrent_secret_replacements_are_serialized_without_losing_keys()
    {
        using var temporary = new TemporaryAbodeToWebDirectory();
        var secrets = new SecretFileStore(temporary.Paths);

        await Task.WhenAll(
            secrets.ReplaceAsync("sample-static", new Dictionary<string, string?> { ["FIRST_TOKEN"] = "first-value" }),
            secrets.ReplaceAsync("sample-static", new Dictionary<string, string?> { ["SECOND_TOKEN"] = "second-value" }));

        var values = secrets.ReadForProcess("sample-static");
        Assert.Equal("first-value", values["FIRST_TOKEN"]);
        Assert.Equal("second-value", values["SECOND_TOKEN"]);
        Assert.Equal(2, secrets.ChangeVersion);

        if (OperatingSystem.IsWindows())
        {
            AssertWindowsSecretAcl(Path.Combine(temporary.Paths.SecretsRoot, "sample-static.env"));
        }
    }

    [Fact]
    public async Task Environment_example_contains_only_sorted_key_names_and_is_bounded()
    {
        using var temporary = new TemporaryAbodeToWebDirectory();
        var secrets = new SecretFileStore(temporary.Paths);
        const string value = "must-never-appear-in-an-example";
        await secrets.ReplaceAsync("sample-static", new Dictionary<string, string?>
        {
            ["Z_TOKEN"] = value,
            ["API_KEY"] = "another-private-value"
        });

        var example = secrets.CreateNamesOnlyExample("sample-static");
        var content = System.Text.Encoding.UTF8.GetString(example);

        Assert.Equal("API_KEY=\nZ_TOKEN=\n", content);
        Assert.DoesNotContain(value, content, StringComparison.Ordinal);
        Assert.DoesNotContain("another-private-value", content, StringComparison.Ordinal);
        Assert.InRange(example.Length, 0, 65_536);
        Assert.Throws<ArgumentException>(() => secrets.CreateNamesOnlyExample("../sample-static"));
    }

    [Fact]
    public async Task Secret_registration_enforces_the_environment_example_key_limit()
    {
        using var temporary = new TemporaryAbodeToWebDirectory();
        var secrets = new SecretFileStore(temporary.Paths);
        var maximum = Enumerable.Range(0, 500).ToDictionary(
            index => $"KEY_{index:D3}",
            _ => (string?)string.Empty,
            StringComparer.Ordinal);
        await secrets.ReplaceAsync("sample-static", maximum);

        var example = secrets.CreateNamesOnlyExample("sample-static");
        Assert.InRange(example.Length, 1, 65_536);
        await Assert.ThrowsAsync<ArgumentException>(() => secrets.ReplaceAsync(
            "sample-static", new Dictionary<string, string?> { ["ONE_TOO_MANY"] = string.Empty }));
        Assert.DoesNotContain("ONE_TOO_MANY", System.Text.Encoding.UTF8.GetString(secrets.CreateNamesOnlyExample("sample-static")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retiring_project_clears_mutable_pointers_and_secrets_but_retains_history()
    {
        using var temporary = new TemporaryAbodeToWebDirectory();
        var database = new AbodeToWebDatabase(temporary.Paths);
        await database.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        await database.UpsertProjectStateAsync(new ProjectStateRecord(
            "sample-static", "running", "running", "healthy", "deployment-a", 1234, now,
            "node.exe", 0, null, now, null));
        await database.UpsertRouteAsync(new RouteStateRecord(
            "sample-static", "sample-static.example.com", "deployment-a", 51_001, "healthy", null, now));
        var deployment = new DeploymentRecord(
            "deployment-a", "sample-static", "operation-a", new string('a', 40), "main",
            temporary.GetPath("deployments", "sample-static", "a"), "Succeeded", now, now,
            10, "succeeded", "healthy", "Release", "Test", true, null, 51_001);
        await database.InsertDeploymentAsync(deployment);
        var secrets = new SecretFileStore(temporary.Paths);
        await secrets.ReplaceAsync("sample-static", new Dictionary<string, string?> { ["API_KEY"] = "old-secret" });

        await database.RetireProjectAsync("sample-static");
        await secrets.DeleteAsync("sample-static");

        Assert.True(await database.IsProjectIdRetiredAsync("SAMPLE-STATIC"));
        Assert.Contains("sample-static", await database.GetRetiredProjectIdsAsync());
        Assert.Empty(await database.GetProjectStatesAsync());
        Assert.Empty(await database.GetRoutesAsync());
        Assert.Equal(deployment, await database.GetDeploymentAsync(deployment.Id));
        Assert.Empty(secrets.ListKeys("sample-static"));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Production_secret_acl_preserves_only_system_administrators_and_service_sid()
    {
        if (!OperatingSystem.IsWindows()) return;

        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administratorsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var localServiceSid = new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null);
        var serviceSid = new SecurityIdentifier("S-1-5-80-1-2-3-4-5");
        var parent = CreateProductionSecretsDirectorySecurity(systemSid, administratorsSid, serviceSid);

        var permissions = WindowsSecretFilePermissions.CreateForTrustedParent(parent, serviceSid);
        var fileSecurity = permissions.CreateFileSecurity();
        var owner = Assert.IsType<SecurityIdentifier>(fileSecurity.GetOwner(typeof(SecurityIdentifier)));
        Assert.Equal(serviceSid, owner);
        Assert.True(fileSecurity.AreAccessRulesProtected);

        var rules = fileSecurity.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .ToArray();
        Assert.Equal(3, rules.Length);
        Assert.Equal(
            new[] { systemSid.Value, administratorsSid.Value, serviceSid.Value }.Order(),
            rules.Select(rule => ((SecurityIdentifier)rule.IdentityReference).Value).Order());
        Assert.All(rules, rule =>
        {
            Assert.False(rule.IsInherited);
            Assert.Equal(InheritanceFlags.None, rule.InheritanceFlags);
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        });
        Assert.DoesNotContain(rules, rule => rule.IdentityReference.Equals(localServiceSid));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Production_secret_acl_rejects_a_local_service_grant()
    {
        if (!OperatingSystem.IsWindows()) return;

        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administratorsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var localServiceSid = new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null);
        var serviceSid = new SecurityIdentifier("S-1-5-80-1-2-3-4-5");
        var parent = CreateProductionSecretsDirectorySecurity(systemSid, administratorsSid, serviceSid);
        parent.AddAccessRule(new FileSystemAccessRule(
            localServiceSid,
            FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        Assert.Throws<SecretFilePermissionException>(() =>
            WindowsSecretFilePermissions.CreateForTrustedParent(parent, serviceSid));
    }

    [SupportedOSPlatform("windows")]
    private static void AssertWindowsSecretAcl(string path)
    {
        var localServiceSid = new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null);
        var security = new FileInfo(path)
            .GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        Assert.True(security.AreAccessRulesProtected);
        Assert.DoesNotContain(
            security.GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>(),
            rule => rule.IdentityReference.Equals(localServiceSid));
    }

    [SupportedOSPlatform("windows")]
    private static DirectorySecurity CreateProductionSecretsDirectorySecurity(
        SecurityIdentifier systemSid,
        SecurityIdentifier administratorsSid,
        SecurityIdentifier serviceSid)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(administratorsSid);
        foreach (var (sid, rights) in new[]
                 {
                     (systemSid, FileSystemRights.FullControl),
                     (administratorsSid, FileSystemRights.FullControl),
                     (serviceSid, FileSystemRights.Modify)
                 })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                rights,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }
        return security;
    }
}
