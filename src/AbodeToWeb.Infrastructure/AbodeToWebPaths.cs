namespace AbodeToWeb.Infrastructure;

public sealed record AbodeToWebPaths
{
    public required string DataRoot { get; init; }
    public required string ConfigurationFile { get; init; }
    public required string DatabaseFile { get; init; }
    public required string LogsRoot { get; init; }
    public required string DeploymentsRoot { get; init; }
    public required string SecretsRoot { get; init; }
    public required string BackupsRoot { get; init; }

    public string StateRoot => Path.GetDirectoryName(DatabaseFile)
        ?? throw new InvalidOperationException("The AbodeToWeb database path must have a parent directory.");

    public string ConfigurationDirectory => Path.GetDirectoryName(ConfigurationFile)
        ?? throw new InvalidOperationException("The AbodeToWeb configuration path must have a parent directory.");

    public static AbodeToWebPaths Resolve()
    {
        var defaultRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "AbodeToWeb");
        var dataRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("ABODE_TO_WEB_DATA_ROOT") ?? defaultRoot);
        var configurationFile = Path.GetFullPath(
            Environment.GetEnvironmentVariable("ABODE_TO_WEB_CONFIG_PATH")
            ?? Path.Combine(dataRoot, "config", "abode-to-web.yaml"));

        return new AbodeToWebPaths
        {
            DataRoot = dataRoot,
            ConfigurationFile = configurationFile,
            DatabaseFile = Path.Combine(dataRoot, "state", "abode-to-web.db"),
            LogsRoot = Path.Combine(dataRoot, "logs"),
            DeploymentsRoot = Path.Combine(dataRoot, "deployments"),
            SecretsRoot = Path.Combine(dataRoot, "secrets"),
            BackupsRoot = Path.Combine(dataRoot, "backups")
        };
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigurationFile)!);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabaseFile)!);
        Directory.CreateDirectory(LogsRoot);
        Directory.CreateDirectory(DeploymentsRoot);
        Directory.CreateDirectory(SecretsRoot);
        Directory.CreateDirectory(BackupsRoot);
    }
}
