using AbodeToWeb.Core.Processes;

namespace AbodeToWeb.Core.Tests;

public sealed class CommandSpecTests
{
    [Fact]
    public void Factory_preserves_each_argument_without_shell_concatenation()
    {
        var command = new CommandSpec
        {
            Executable = "node.exe",
            Arguments = ["server.js", "value with spaces", "&&", "echo unsafe"],
            TimeoutSeconds = 30
        };

        var info = ProcessStartInfoFactory.Create(command, Path.GetTempPath());

        Assert.Equal("node.exe", info.FileName);
        Assert.Equal(command.Arguments, info.ArgumentList);
        Assert.False(info.UseShellExecute);
        Assert.True(info.RedirectStandardOutput);
        Assert.True(info.RedirectStandardError);
        Assert.True(info.RedirectStandardInput);
        Assert.True(info.CreateNoWindow);
    }

    [Fact]
    public void Factory_applies_environment_values_and_removals()
    {
        const string removedKey = "ABODE_TO_WEB_CORE_TEST_REMOVE";
        Environment.SetEnvironmentVariable(removedKey, "inherited");
        try
        {
            var info = ProcessStartInfoFactory.Create(
                new CommandSpec { Executable = "app.exe" },
                Path.GetTempPath(),
                new Dictionary<string, string?>
                {
                    ["ABODE_TO_WEB_CORE_TEST"] = "a value",
                    [removedKey] = null
                });

            Assert.Equal("a value", info.Environment["ABODE_TO_WEB_CORE_TEST"]);
            Assert.False(info.Environment.ContainsKey(removedKey));
        }
        finally
        {
            Environment.SetEnvironmentVariable(removedKey, null);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/path")]
    public void Factory_rejects_non_absolute_working_directory(string workingDirectory)
    {
        var command = new CommandSpec { Executable = "app.exe" };

        Assert.Throws<ArgumentException>(() => ProcessStartInfoFactory.Create(command, workingDirectory));
    }

    [Fact]
    public void Validator_rejects_control_characters_and_invalid_timeout()
    {
        var command = new CommandSpec
        {
            Executable = "app\n.exe",
            Arguments = ["good", "bad\rvalue"],
            TimeoutSeconds = 0
        };

        var result = CommandSpecValidator.Validate(command, "$.command");

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, issue => issue.Code == "command.executable.invalid");
        Assert.Contains(result.Errors, issue => issue.Code == "command.argument.invalid");
        Assert.Contains(result.Errors, issue => issue.Code == "command.timeout.range");
    }

    [Fact]
    public void Factory_rejects_invalid_environment_names()
    {
        var command = new CommandSpec { Executable = "app.exe" };

        Assert.Throws<ArgumentException>(() => ProcessStartInfoFactory.Create(
            command,
            Path.GetTempPath(),
            new Dictionary<string, string?> { ["BAD=KEY"] = "value" }));
    }

    [Fact]
    public void Validator_rejects_null_argument_array_from_untrusted_deserialization()
    {
        var command = new CommandSpec { Executable = "app.exe", Arguments = null! };

        var result = CommandSpecValidator.Validate(command, "$.command");

        Assert.Contains(result.Errors, issue => issue.Code == "command.arguments.required");
    }
}
