using AbodeToWeb.Server.Services;

namespace AbodeToWeb.IntegrationTests;

public sealed class TunnelDiagnosticsServiceTests
{
    [Fact]
    public void FindExecutable_uses_the_conventional_machine_install_directory()
    {
        using var temporary = new TemporaryAbodeToWebDirectory();
        var programFiles = temporary.GetPath("program-files");
        var executable = Path.Combine(programFiles, "cloudflared", "cloudflared.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, string.Empty);

        var resolved = TunnelDiagnosticsService.FindExecutable([programFiles]);

        Assert.Equal(Path.GetFullPath(executable), resolved, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void FindExecutable_does_not_use_a_path_style_root_executable()
    {
        using var temporary = new TemporaryAbodeToWebDirectory();
        var pathEntry = temporary.GetPath("path-entry");
        Directory.CreateDirectory(pathEntry);
        File.WriteAllText(Path.Combine(pathEntry, "cloudflared.exe"), string.Empty);

        var resolved = TunnelDiagnosticsService.FindExecutable([pathEntry]);

        Assert.Null(resolved);
    }
}
