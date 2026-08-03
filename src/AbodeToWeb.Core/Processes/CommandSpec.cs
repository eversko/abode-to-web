using System.Diagnostics;
using System.Text.Json.Serialization;
using AbodeToWeb.Core.Validation;

namespace AbodeToWeb.Core.Processes;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CommandSpec
{
    public required string Executable { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = [];

    public int TimeoutSeconds { get; init; } = 900;
}

public static class CommandSpecValidator
{
    public static ValidationResult Validate(CommandSpec? command, string path)
    {
        var result = new ValidationResult();
        if (command is null)
        {
            result.Add("command.required", path, "A command is required.");
            return result;
        }

        if (string.IsNullOrWhiteSpace(command.Executable))
        {
            result.Add("command.executable.required", $"{path}.executable", "Executable cannot be empty.");
        }
        else if (ContainsControlOrNull(command.Executable))
        {
            result.Add("command.executable.invalid", $"{path}.executable", "Executable contains a control character.");
        }
        else if (command.Executable.Contains('"'))
        {
            result.Add("command.executable.invalid", $"{path}.executable", "Executable cannot contain a quote character.");
        }

        if (command.TimeoutSeconds is < 1 or > 86_400)
        {
            result.Add("command.timeout.range", $"{path}.timeoutSeconds", "Timeout must be between 1 and 86400 seconds.");
        }

        if (command.Arguments is null)
        {
            result.Add("command.arguments.required", $"{path}.arguments", "Arguments must be an array.");
            return result;
        }

        if (command.Arguments.Count > 256)
        {
            result.Add("command.arguments.limit", $"{path}.arguments", "A command can contain at most 256 arguments.");
        }

        for (var index = 0; index < command.Arguments.Count; index++)
        {
            var argument = command.Arguments[index];
            if (argument is null || ContainsControlOrNull(argument))
            {
                result.Add(
                    "command.argument.invalid",
                    $"{path}.arguments[{index}]",
                    "Arguments cannot be null or contain control characters other than tab.");
            }
        }

        return result;
    }

    private static bool ContainsControlOrNull(string value) =>
        value.Any(character => character == '\0' || (char.IsControl(character) && character != '\t'));
}

public static class ProcessStartInfoFactory
{
    public static ProcessStartInfo Create(
        CommandSpec command,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment = null,
        bool inheritParentEnvironment = false)
    {
        ArgumentNullException.ThrowIfNull(command);

        var validation = CommandSpecValidator.Validate(command, "command");
        if (!validation.IsValid)
        {
            throw new ArgumentException(validation.Errors.First().Message, nameof(command));
        }

        if (string.IsNullOrWhiteSpace(workingDirectory) || !Path.IsPathFullyQualified(workingDirectory))
        {
            throw new ArgumentException("Working directory must be a fully qualified path.", nameof(workingDirectory));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = command.Executable,
            WorkingDirectory = Path.GetFullPath(workingDirectory),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            ErrorDialog = false
        };

        if (!inheritParentEnvironment)
        {
            var inherited = startInfo.Environment.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            startInfo.Environment.Clear();
            foreach (var key in SafeBaselineEnvironmentKeys)
                if (inherited.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value)) startInfo.Environment[key] = value;

            var runtimeRoot = Path.Combine(startInfo.WorkingDirectory, ".abode-to-web-runtime");
            startInfo.Environment["TEMP"] = Path.Combine(runtimeRoot, "temp");
            startInfo.Environment["TMP"] = Path.Combine(runtimeRoot, "temp");
            startInfo.Environment["HOME"] = Path.Combine(runtimeRoot, "home");
            startInfo.Environment["USERPROFILE"] = Path.Combine(runtimeRoot, "home");
            startInfo.Environment["APPDATA"] = Path.Combine(runtimeRoot, "appdata", "roaming");
            startInfo.Environment["LOCALAPPDATA"] = Path.Combine(runtimeRoot, "appdata", "local");
        }

        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                ValidateEnvironmentKey(key);
                if (value is null)
                {
                    startInfo.Environment.Remove(key);
                }
                else
                {
                    if (value.Contains('\0', StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"Environment variable '{key}' contains a null character.", nameof(environment));
                    }

                    startInfo.Environment[key] = value;
                }
            }
        }

        return startInfo;
    }

    private static readonly string[] SafeBaselineEnvironmentKeys =
    [
        "SystemRoot", "WINDIR", "PATH", "PATHEXT", "ComSpec", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432",
        "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "PROCESSOR_IDENTIFIER", "OS"
    ];

    private static void ValidateEnvironmentKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Contains('=', StringComparison.Ordinal) || key.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Environment variable names cannot be empty or contain '=' or null characters.", nameof(key));
        }
    }
}
