using AbodeToWeb.Server.Gateway;

namespace AbodeToWeb.IntegrationTests;

public sealed class GatewayStaticFileSecurityIntegrationTests
{
    [Fact]
    public async Task Verified_open_rejects_reparse_escape_and_pins_the_opened_file()
    {
        using var temporary = new TemporaryAbodeToWebDirectory();
        var root = temporary.GetPath("static-root");
        var outside = temporary.GetPath("outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);

        var safePath = Path.Combine(root, "safe.txt");
        await File.WriteAllTextAsync(safePath, "safe content");
        using (var opened = GatewayMiddleware.OpenVerifiedStaticFile(root, safePath))
        {
            Assert.NotNull(opened);
            Assert.Throws<IOException>(() => File.Move(safePath, Path.Combine(root, "moved.txt")));
            using var reader = new StreamReader(opened!);
            Assert.Equal("safe content", await reader.ReadToEndAsync());
        }

        var secretPath = Path.Combine(outside, "secret.txt");
        await File.WriteAllTextAsync(secretPath, "outside content");
        var link = Path.Combine(root, "escape.txt");
        File.CreateSymbolicLink(link, secretPath);

        using var escaped = GatewayMiddleware.OpenVerifiedStaticFile(root, link);
        Assert.Null(escaped);
    }
}
