using System.Diagnostics;
using System.Text.Json;
using AbodeToWeb.Core.Configuration;
using AbodeToWeb.Infrastructure.Configuration;
using AbodeToWeb.Infrastructure.Logging;
using AbodeToWeb.Infrastructure.Processes;
using AbodeToWeb.Server.Services;

namespace AbodeToWeb.IntegrationTests;

public sealed class LocalProjectQueryServiceTests
{
    [Fact]
    public async Task Local_projects_return_git_facts_without_persisting_repository_metadata_to_logs()
    {
        const string commitSubject = "synthetic-commit-metadata-marker";
        using var temporary = new TemporaryAbodeToWebDirectory();
        var repository = temporary.GetPath("projects", "sample-site");
        Directory.CreateDirectory(repository);
        RunGit(repository, "init", "-b", "main", ".");
        await File.WriteAllTextAsync(Path.Combine(repository, "README.md"), "# Sample Site", CancellationToken.None);
        RunGit(repository, "add", "README.md");
        RunGit(repository, "commit", "-m", commitSubject);

        var manifest = new AbodeToWebManifest
        {
            ManifestVersion = 1,
            AbodeToWeb = new AbodeToWebOptions { ProjectsRoots = [temporary.GetPath("projects")] },
            LocalProjects =
            [
                new LocalProjectManifest
                {
                    Id = "sample-site",
                    Name = "Sample Site",
                    RepositoryPath = repository,
                    PlannedHostname = "sample.example.com"
                },
                new LocalProjectManifest
                {
                    Id = "missing-project",
                    Name = "Missing",
                    RepositoryPath = temporary.GetPath("projects", "does-not-exist")
                }
            ]
        };
        await File.WriteAllTextAsync(
            temporary.Paths.ConfigurationFile,
            JsonSerializer.Serialize(manifest, JsonManifestParser.CreateSerializerOptions()),
            CancellationToken.None);
        var parser = new YamlManifestParser();
        using var provider = new ManifestProvider(
            temporary.Paths,
            parser,
            value => new ManifestValidator().Validate(value));
        var loaded = await provider.LoadAsync(allowLastKnownGood: false, CancellationToken.None);
        Assert.True(loaded.Success);
        var logs = new BoundedLogStore(temporary.Paths, new LogStreamHub(), maxFileBytes: 1024 * 1024, retainedFiles: 2);
        var runner = new StructuredCommandRunner(logs, new CurrentIdentityTestProcessLauncher());
        var service = new LocalProjectQueryService(provider, runner);

        var results = await service.GetAsync(CancellationToken.None);

        Assert.Equal(2, results.Count);
        var missing = Assert.Single(results, project => project.Id == "missing-project");
        Assert.False(missing.Exists);
        Assert.Null(missing.Branch);
        Assert.NotNull(missing.SafeStatusMessage);

        var sampleSite = Assert.Single(results, project => project.Id == "sample-site");
        Assert.True(sampleSite.Exists);
        Assert.Equal("main", sampleSite.Branch);
        Assert.Equal("sample.example.com", sampleSite.PlannedHostname);
        Assert.Equal(commitSubject, sampleSite.LastCommitMessage);
        Assert.Equal("AbodeToWeb Tests", sampleSite.LastCommitAuthor);
        Assert.NotNull(sampleSite.LastCommitSha);
        Assert.Equal(sampleSite.LastCommitSha![..7], sampleSite.LastCommitShortSha);
        Assert.NotNull(sampleSite.LastCommitDate);
        Assert.Null(sampleSite.SafeStatusMessage);

        var commandLogs = await logs.QueryAsync(new LogQuery(
            AbodeToWebLogKind.Build,
            "local-project-git",
            Limit: 50));
        Assert.DoesNotContain(commandLogs, entry => entry.Message.Contains(commitSubject, StringComparison.Ordinal));
        Assert.Contains(commandLogs, entry => entry.Message == "[sensitive command output redacted]");
        Assert.Contains(commandLogs, entry => entry.Message == "Command completed.");
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        using var process = new Process();
        process.StartInfo.FileName = "git";
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        process.StartInfo.WorkingDirectory = workingDirectory;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.Environment["GIT_CONFIG_GLOBAL"] = "NUL";
        process.StartInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        process.StartInfo.Environment["GIT_AUTHOR_NAME"] = "AbodeToWeb Tests";
        process.StartInfo.Environment["GIT_AUTHOR_EMAIL"] = "tests@abode-to-web.local";
        process.StartInfo.Environment["GIT_COMMITTER_NAME"] = "AbodeToWeb Tests";
        process.StartInfo.Environment["GIT_COMMITTER_EMAIL"] = "tests@abode-to-web.local";
        Assert.True(process.Start(), "git could not be started.");
        var error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "git timed out.");
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
    }
}
