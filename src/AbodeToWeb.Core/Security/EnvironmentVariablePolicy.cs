namespace AbodeToWeb.Core.Security;

public static class EnvironmentVariablePolicy
{
    private static readonly HashSet<string> DangerousKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "PATHEXT", "COMSPEC", "SYSTEMROOT", "WINDIR", "HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA",
        "DOTNET_STARTUP_HOOKS", "DOTNET_ADDITIONAL_DEPS", "DOTNET_SHARED_STORE", "DOTNET_ROOT", "DOTNET_ROOT_X64",
        "DOTNET_ROOT_X86", "DOTNET_HOST_PATH", "DOTNET_MULTILEVEL_LOOKUP",
        "NODE_OPTIONS", "NODE_PATH", "PNPM_HOME",
        "PYTHONPATH", "PYTHONHOME", "PYTHONSTARTUP", "PYTHONBREAKPOINT", "PYTHONINSPECT",
        "PSMODULEPATH", "BASH_ENV", "ENV", "CDPATH",
        "JAVA_TOOL_OPTIONS", "JDK_JAVA_OPTIONS", "_JAVA_OPTIONS", "CLASSPATH",
        "RUBYOPT", "RUBYLIB", "PERL5OPT", "PERL5LIB", "PHPRC", "PHP_INI_SCAN_DIR",
        "RUSTC_WRAPPER", "RUSTC_WORKSPACE_WRAPPER", "CARGO_HOME",
        "MSBUILD_EXE_PATH", "MSBUILDSDKSPATH", "MSBUILDEXTENSIONSPATH", "VSTEST_HOST_DEBUG",
        "ELECTRON_RUN_AS_NODE", "SSLKEYLOGFILE", "WSLENV"
    };

    private static readonly string[] DangerousPrefixes =
    [
        "ABODE_TO_WEB_", "GIT_", "NPM_CONFIG_", "YARN_", "CORECLR_", "COMPLUS_", "COR_", "LD_", "DYLD_"
    ];

    public static bool IsAllowedSecretKey(string key) =>
        !DangerousKeys.Contains(key)
        && !DangerousPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
