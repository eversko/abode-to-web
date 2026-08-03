using AbodeToWeb.Core.Security;

namespace AbodeToWeb.Core.Tests;

public sealed class EnvironmentVariablePolicyTests
{
    [Theory]
    [InlineData("DATABASE_URL")]
    [InlineData("NPM_TOKEN")]
    [InlineData("ASPNETCORE_ENVIRONMENT")]
    [InlineData("NODE_ENV")]
    public void Ordinary_application_keys_are_allowed(string key) =>
        Assert.True(EnvironmentVariablePolicy.IsAllowedSecretKey(key));

    [Theory]
    [InlineData("PATH")]
    [InlineData("NODE_OPTIONS")]
    [InlineData("DOTNET_STARTUP_HOOKS")]
    [InlineData("GIT_CONFIG_COUNT")]
    [InlineData("NPM_CONFIG_SCRIPT_SHELL")]
    [InlineData("ABODE_TO_WEB_MANAGED")]
    [InlineData("PSModulePath")]
    public void Process_loading_and_control_keys_are_rejected(string key) =>
        Assert.False(EnvironmentVariablePolicy.IsAllowedSecretKey(key));
}
