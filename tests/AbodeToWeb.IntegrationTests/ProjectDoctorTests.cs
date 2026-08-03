using System.Security.Cryptography;
using System.Text.Json;
using AbodeToWeb.Server.Services;

namespace AbodeToWeb.IntegrationTests;

public sealed class ProjectDoctorTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Doctor_is_deterministic_read_only_and_does_not_emit_secrets_remotes_or_paths()
    {
        using var project = new TemporaryDoctorProject();
        project.Write("package.json", """
            {
              "scripts": {
                "build": "vite build --token ultra-private-doctor-secret"
              },
              "devDependencies": {
                "vite": "1.0.0"
              }
            }
            """);
        project.Write("package-lock.json", "{\"lockfileVersion\":3}");
        project.Write("dist/index.html", "<!doctype html><title>Built sample</title>");
        project.Write(".env", "DEPLOY_TOKEN=ultra-private-doctor-secret\nOWNER=private-user");
        project.Write(".git/HEAD", "ref: refs/heads/main\n");
        project.Write(".git/config", """
            [core]
                worktree = D:/Unrelated/private-location
            [remote "origin"]
                url = https://private-user:ultra-private-doctor-secret@github.com/private-user/private-project.git
            """);
        var before = project.Snapshot();

        var first = await ProjectInspector.CreateDoctorReportAsync(project.Root);
        var second = await ProjectInspector.CreateDoctorReportAsync(project.Root);
        var firstJson = JsonSerializer.Serialize(first, JsonOptions);
        var secondJson = JsonSerializer.Serialize(second, JsonOptions);
        var after = project.Snapshot();

        Assert.Equal(firstJson, secondJson);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var entry in before)
        {
            Assert.True(after.TryGetValue(entry.Key, out var current));
            Assert.Equal(entry.Value, current);
        }
        Assert.False(first.ChangesMade);
        Assert.Equal("supported", first.Status);
        Assert.True(first.Git.IsRepository);
        Assert.Equal("main", first.Git.CurrentBranch);
        Assert.True(first.Git.RemoteConfigured);
        Assert.False(first.Git.WorkingTreeInspected);
        Assert.Equal("vite", first.Detected.Framework);
        Assert.Equal("static", first.Detected.Adapter);
        Assert.Equal("package-lock.json", first.Detected.LockFile);
        Assert.True(first.Detected.StaticOutputPresent);
        Assert.All(first.CommandSuggestions, suggestion =>
        {
            Assert.True(suggestion.Experimental);
            Assert.False(suggestion.Selected);
            Assert.StartsWith("Experimental project execution:", suggestion.Reason, StringComparison.Ordinal);
        });
        Assert.False(first.ProposedApplication.Enabled);
        Assert.False(first.ProposedApplication.Autostart);
        Assert.Equal("disabled", first.ProposedApplication.Visibility);
        Assert.False(first.ProposedApplication.ProjectExecutionEnabled);
        Assert.False(first.ProposedApplication.InstallCommandEnabled);
        Assert.False(first.ProposedApplication.BuildCommandEnabled);
        Assert.False(first.ProposedApplication.StartCommandEnabled);
        Assert.False(first.ProposedApplication.CommandsSelected);
        Assert.Equal(["APPROVE LOCAL SETUP", "APPROVE PUBLISH"], first.ApprovalActions.Select(action => action.Phrase));
        Assert.DoesNotContain("ultra-private-doctor-secret", firstJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-user", firstJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Unrelated", firstJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("github.com", firstJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(project.Root, firstJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Doctor_cli_writes_json_to_stdout_without_starting_the_server_or_changing_the_project()
    {
        using var project = new TemporaryDoctorProject();
        project.Write("index.html", "<!doctype html><title>Sample</title>");
        project.Write(".git/HEAD", "ref: refs/heads/main\n");
        project.Write(".git/config", "[remote \"origin\"]\n    url = https://github.com/example/sample.git\n");
        var before = project.Snapshot();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await AbodeToWeb.Server.Program.DoctorProjectCliAsync(
            ["doctor-project", "--path", project.Root, "--json"],
            output,
            error);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        using var document = JsonDocument.Parse(output.ToString());
        var root = document.RootElement;
        Assert.False(root.GetProperty("changesMade").GetBoolean());
        Assert.Equal("supported", root.GetProperty("status").GetString());
        Assert.Equal("static", root.GetProperty("detected").GetProperty("adapter").GetString());
        Assert.Equal("disabled", root.GetProperty("proposedApplication").GetProperty("visibility").GetString());
        Assert.Equal(
            before.OrderBy(entry => entry.Key, StringComparer.Ordinal),
            project.Snapshot().OrderBy(entry => entry.Key, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Doctor_marks_dynamic_projects_unsupported_without_selecting_commands()
    {
        using var project = new TemporaryDoctorProject();
        project.Write("package.json", """
            {
              "scripts": { "build": "next build", "start": "next start" },
              "dependencies": { "next": "1.0.0" }
            }
            """);
        project.Write("package-lock.json", "{\"lockfileVersion\":3}");
        project.Write(".git/HEAD", "ref: refs/heads/main\n");
        project.Write(".git/config", "[remote \"origin\"]\n    url = ssh://git@example.invalid/sample.git\n");

        var report = await ProjectInspector.CreateDoctorReportAsync(project.Root);

        Assert.Equal("unsupported", report.Status);
        Assert.Equal("next-js", report.Detected.Framework);
        Assert.Equal("node", report.Detected.Adapter);
        Assert.All(report.CommandSuggestions, suggestion =>
        {
            Assert.True(suggestion.Experimental);
            Assert.False(suggestion.Selected);
        });
        Assert.Contains(report.Warnings, warning => warning.Contains("experimental project execution", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(report.RequiredInputs, input => input.Id == "project-execution-decision" && input.Required);
    }

    [Fact]
    public async Task Doctor_redacts_nonstandard_branch_names_that_may_contain_user_identifiers()
    {
        using var project = new TemporaryDoctorProject();
        project.Write("index.html", "<!doctype html><title>Sample</title>");
        project.Write(".git/HEAD", "ref: refs/heads/private-user/private-feature\n");
        project.Write(".git/config", "[remote \"origin\"]\n    url = https://github.com/private-user/sample.git\n");

        var report = await ProjectInspector.CreateDoctorReportAsync(project.Root);
        var json = JsonSerializer.Serialize(report, JsonOptions);

        Assert.Equal("needs-input", report.Status);
        Assert.Equal("branch-name-redacted", report.Git.HeadState);
        Assert.Null(report.Git.CurrentBranch);
        Assert.DoesNotContain("private-user", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-feature", json, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TemporaryDoctorProject : IDisposable
    {
        internal TemporaryDoctorProject()
        {
            Root = Path.Combine(Path.GetTempPath(), "abode-to-web-doctor-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        internal string Root { get; }

        internal void Write(string relativePath, string content)
        {
            var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        internal Dictionary<string, FileSnapshot> Snapshot() => Directory
            .EnumerateFiles(Root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToDictionary(
                path => Path.GetRelativePath(Root, path).Replace(Path.DirectorySeparatorChar, '/'),
                path => new FileSnapshot(
                    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                    File.GetLastWriteTimeUtc(path)),
                StringComparer.Ordinal);

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed record FileSnapshot(string Sha256, DateTime LastWriteTimeUtc);
}
