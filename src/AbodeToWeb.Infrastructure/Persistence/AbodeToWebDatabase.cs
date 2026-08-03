using Microsoft.Data.Sqlite;

namespace AbodeToWeb.Infrastructure.Persistence;

public sealed class AbodeToWebDatabase(AbodeToWebPaths paths)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = paths.DatabaseFile,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
        Pooling = true,
        ForeignKeys = true
    }.ToString();

    public string DatabaseFile => paths.DatabaseFile;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        paths.EnsureDirectories();
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, null, "PRAGMA journal_mode=WAL;", cancellationToken);
        await ExecuteAsync(connection, null, "PRAGMA synchronous=NORMAL;", cancellationToken);
        await ExecuteAsync(connection, null, "PRAGMA busy_timeout=5000;", cancellationToken);
        await ExecuteAsync(connection, null, """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER PRIMARY KEY,
                applied_utc TEXT NOT NULL
            );
            """, cancellationToken);

        var version = await ScalarLongAsync(connection, "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;", cancellationToken);
        if (version < 1)
        {
            await ApplyMigrationAsync(connection, 1, Migration1, cancellationToken);
        }

        if (version < 2)
        {
            await ApplyMigrationAsync(connection, 2, Migration2, cancellationToken);
        }

        if (version < 3)
        {
            await ApplyMigrationAsync(connection, 3, Migration3, cancellationToken);
        }

        await RecoverInterruptedOperationsAsync(connection, cancellationToken);
    }

    public async Task UpsertProjectStateAsync(ProjectStateRecord state, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO project_state
                (app_id, desired_state, runtime_state, health_state, active_deployment_id, process_id,
                 process_started_utc, executable_path, restart_count, circuit_open_until_utc, updated_utc, last_error)
            VALUES
                ($appId, $desiredState, $runtimeState, $healthState, $activeDeploymentId, $processId,
                 $processStartedUtc, $executablePath, $restartCount, $circuitOpenUntilUtc, $updatedUtc, $lastError)
            ON CONFLICT(app_id) DO UPDATE SET
                desired_state = excluded.desired_state,
                runtime_state = excluded.runtime_state,
                health_state = excluded.health_state,
                active_deployment_id = excluded.active_deployment_id,
                process_id = excluded.process_id,
                process_started_utc = excluded.process_started_utc,
                executable_path = excluded.executable_path,
                restart_count = excluded.restart_count,
                circuit_open_until_utc = excluded.circuit_open_until_utc,
                updated_utc = excluded.updated_utc,
                last_error = excluded.last_error;
            """;
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, null, sql, cancellationToken,
            ("$appId", state.AppId), ("$desiredState", state.DesiredState),
            ("$runtimeState", state.RuntimeState), ("$healthState", state.HealthState),
            ("$activeDeploymentId", state.ActiveDeploymentId), ("$processId", state.ProcessId),
            ("$processStartedUtc", ToDb(state.ProcessStartedUtc)), ("$executablePath", state.ExecutablePath),
            ("$restartCount", state.RestartCount), ("$circuitOpenUntilUtc", ToDb(state.CircuitOpenUntilUtc)),
            ("$updatedUtc", ToDb(state.UpdatedUtc)), ("$lastError", state.LastError));
    }

    public async Task<IReadOnlyList<ProjectStateRecord>> GetProjectStatesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM project_state ORDER BY app_id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ProjectStateRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ProjectStateRecord(
                reader.GetString(reader.GetOrdinal("app_id")),
                reader.GetString(reader.GetOrdinal("desired_state")),
                reader.GetString(reader.GetOrdinal("runtime_state")),
                reader.GetString(reader.GetOrdinal("health_state")),
                GetNullableString(reader, "active_deployment_id"),
                GetNullableInt(reader, "process_id"),
                FromDb(GetNullableString(reader, "process_started_utc")),
                GetNullableString(reader, "executable_path"),
                reader.GetInt32(reader.GetOrdinal("restart_count")),
                FromDb(GetNullableString(reader, "circuit_open_until_utc")),
                FromDb(reader.GetString(reader.GetOrdinal("updated_utc")))!.Value,
                GetNullableString(reader, "last_error")));
        }
        return result;
    }

    public async Task InsertDeploymentAsync(DeploymentRecord deployment, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO deployments
                (id, app_id, operation_id, commit_sha, branch, worktree_path, state, started_utc, completed_utc,
                 duration_ms, build_outcome, health_outcome, commit_message, commit_author, is_known_good,
                 failure_reason, internal_port)
            VALUES
                ($id, $appId, $operationId, $commitSha, $branch, $worktreePath, $state, $startedUtc, $completedUtc,
                 $durationMs, $buildOutcome, $healthOutcome, $commitMessage, $commitAuthor, $isKnownGood,
                 $failureReason, $internalPort);
            """;
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, null, sql, cancellationToken, DeploymentParameters(deployment));
    }

    public async Task UpdateDeploymentAsync(DeploymentRecord deployment, CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE deployments SET
                state=$state, completed_utc=$completedUtc, duration_ms=$durationMs, build_outcome=$buildOutcome,
                health_outcome=$healthOutcome, commit_message=$commitMessage, commit_author=$commitAuthor,
                is_known_good=$isKnownGood, failure_reason=$failureReason, internal_port=$internalPort
            WHERE id=$id;
            """;
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, null, sql, cancellationToken, DeploymentParameters(deployment));
    }

    public async Task<IReadOnlyList<DeploymentRecord>> GetDeploymentsAsync(string? appId = null, int limit = 50, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = appId is null
            ? "SELECT * FROM deployments ORDER BY started_utc DESC LIMIT $limit;"
            : "SELECT * FROM deployments WHERE app_id=$appId ORDER BY started_utc DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);
        if (appId is not null) command.Parameters.AddWithValue("$appId", appId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<DeploymentRecord>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadDeployment(reader));
        return result;
    }

    public async Task<IReadOnlyList<DeploymentRecord>> GetInterruptedDeploymentsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM deployments
            WHERE state COLLATE NOCASE NOT IN ('Succeeded', 'Failed', 'Cancelled', 'RolledBack')
            ORDER BY started_utc;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<DeploymentRecord>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadDeployment(reader));
        return result;
    }

    public async Task<DeploymentRecord?> GetDeploymentAsync(string deploymentId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM deployments WHERE id=$id;";
        command.Parameters.AddWithValue("$id", deploymentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadDeployment(reader) : null;
    }

    public async Task UpsertOperationAsync(OperationRecord operation, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO operations
                (id, action, target_id, status, progress, message, created_utc, started_utc, completed_utc, error_code, error_message)
            VALUES
                ($id, $action, $targetId, $status, $progress, $message, $createdUtc, $startedUtc, $completedUtc, $errorCode, $errorMessage)
            ON CONFLICT(id) DO UPDATE SET
                status=excluded.status, progress=excluded.progress, message=excluded.message,
                started_utc=excluded.started_utc, completed_utc=excluded.completed_utc,
                error_code=excluded.error_code, error_message=excluded.error_message;
            """;
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, null, sql, cancellationToken,
            ("$id", operation.Id), ("$action", operation.Action), ("$targetId", operation.TargetId),
            ("$status", operation.Status), ("$progress", operation.Progress), ("$message", operation.Message),
            ("$createdUtc", ToDb(operation.CreatedUtc)), ("$startedUtc", ToDb(operation.StartedUtc)),
            ("$completedUtc", ToDb(operation.CompletedUtc)), ("$errorCode", operation.ErrorCode),
            ("$errorMessage", operation.ErrorMessage));
    }

    public async Task<OperationRecord?> GetOperationAsync(string operationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM operations WHERE id=$id;";
        command.Parameters.AddWithValue("$id", operationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadOperation(reader) : null;
    }

    public async Task<IReadOnlyList<OperationRecord>> GetRecentOperationsAsync(int limit = 50, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM operations ORDER BY created_utc DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<OperationRecord>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadOperation(reader));
        return result;
    }

    public async Task<long> AppendAuditAsync(AuditEventRecord auditEvent, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO audit_events
                (occurred_utc, actor, action, target_id, outcome, operation_id, detail_json, remote_address)
            VALUES ($occurredUtc, $actor, $action, $targetId, $outcome, $operationId, $detailJson, $remoteAddress);
            SELECT last_insert_rowid();
            """;
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameters(command,
            ("$occurredUtc", ToDb(auditEvent.OccurredUtc)), ("$actor", auditEvent.Actor),
            ("$action", auditEvent.Action), ("$targetId", auditEvent.TargetId),
            ("$outcome", auditEvent.Outcome), ("$operationId", auditEvent.OperationId),
            ("$detailJson", auditEvent.DetailJson), ("$remoteAddress", auditEvent.RemoteAddress));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<AuditEventRecord>> GetAuditEventsAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM audit_events ORDER BY id DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<AuditEventRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new AuditEventRecord(
                reader.GetInt64(reader.GetOrdinal("id")),
                FromDb(reader.GetString(reader.GetOrdinal("occurred_utc")))!.Value,
                reader.GetString(reader.GetOrdinal("actor")),
                reader.GetString(reader.GetOrdinal("action")),
                reader.GetString(reader.GetOrdinal("target_id")),
                reader.GetString(reader.GetOrdinal("outcome")),
                GetNullableString(reader, "operation_id"),
                reader.GetString(reader.GetOrdinal("detail_json")),
                GetNullableString(reader, "remote_address")));
        }
        return result;
    }

    public async Task UpsertRouteAsync(RouteStateRecord route, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO route_state (app_id, hostname, deployment_id, internal_port, status, static_path, updated_utc)
            VALUES ($appId, $hostname, $deploymentId, $internalPort, $status, $staticPath, $updatedUtc)
            ON CONFLICT(app_id) DO UPDATE SET
                hostname=excluded.hostname, deployment_id=excluded.deployment_id, internal_port=excluded.internal_port,
                status=excluded.status, static_path=excluded.static_path, updated_utc=excluded.updated_utc;
            """;
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, null, sql, cancellationToken,
            ("$appId", route.AppId), ("$hostname", route.Hostname), ("$deploymentId", route.DeploymentId),
            ("$internalPort", route.InternalPort), ("$status", route.Status), ("$staticPath", route.StaticPath),
            ("$updatedUtc", ToDb(route.UpdatedUtc)));
    }

    public async Task<IReadOnlyList<RouteStateRecord>> GetRoutesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM route_state ORDER BY hostname;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<RouteStateRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new RouteStateRecord(
                reader.GetString(reader.GetOrdinal("app_id")), reader.GetString(reader.GetOrdinal("hostname")),
                GetNullableString(reader, "deployment_id"), GetNullableInt(reader, "internal_port"),
                reader.GetString(reader.GetOrdinal("status")), GetNullableString(reader, "static_path"),
                FromDb(reader.GetString(reader.GetOrdinal("updated_utc")))!.Value));
        }
        return result;
    }

    public async Task RetireProjectAsync(string appId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(connection, transaction, """
            INSERT INTO retired_projects (app_id, retired_utc)
            VALUES ($appId, $retiredUtc)
            ON CONFLICT(app_id) DO NOTHING;
            """, cancellationToken, ("$appId", appId), ("$retiredUtc", ToDb(DateTimeOffset.UtcNow)));
        await ExecuteAsync(connection, transaction,
            "DELETE FROM route_state WHERE app_id=$appId;", cancellationToken, ("$appId", appId));
        await ExecuteAsync(connection, transaction,
            "DELETE FROM project_state WHERE app_id=$appId;", cancellationToken, ("$appId", appId));
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> IsProjectIdRetiredAsync(string appId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM retired_projects WHERE app_id=$appId);";
        command.Parameters.AddWithValue("$appId", appId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    public async Task<IReadOnlySet<string>> GetRetiredProjectIdsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT app_id FROM retired_projects ORDER BY app_id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(0));
        return result;
    }

    public async Task BackupAsync(string destination, CancellationToken cancellationToken = default)
    {
        var fullDestination = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);
        await using var source = await OpenAsync(cancellationToken);
        await using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fullDestination }.ToString());
        await target.OpenAsync(cancellationToken);
        source.BackupDatabase(target);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await ExecuteAsync(connection, null, "PRAGMA busy_timeout=5000;", cancellationToken);
        return connection;
    }

    private static async Task ApplyMigrationAsync(SqliteConnection connection, long version, string sql, CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(connection, transaction, sql, cancellationToken);
        await ExecuteAsync(connection, transaction,
            "INSERT INTO schema_migrations(version, applied_utc) VALUES ($version, $appliedUtc);",
            cancellationToken, ("$version", version), ("$appliedUtc", ToDb(DateTimeOffset.UtcNow)));
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task RecoverInterruptedOperationsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        const string message = "AbodeToWeb restarted before this operation completed.";
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(connection, transaction, """
            INSERT INTO audit_events
                (occurred_utc, actor, action, target_id, outcome, operation_id, detail_json, remote_address)
            SELECT $completedUtc, 'AbodeToWeb', action, target_id, 'interrupted', id,
                   '{"error":"AbodeToWeb restarted before this operation completed."}', NULL
            FROM operations
            WHERE status IN ('queued', 'running');
            """, cancellationToken, ("$completedUtc", ToDb(DateTimeOffset.UtcNow)));
        await ExecuteAsync(connection, transaction, """
            UPDATE operations
            SET status='failed', message=$message, completed_utc=$completedUtc,
                error_code='operation.interrupted', error_message=$message
            WHERE status IN ('queued', 'running');
            """, cancellationToken,
            ("$message", message), ("$completedUtc", ToDb(DateTimeOffset.UtcNow)));
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        AddParameters(command, parameters);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParameters(SqliteCommand command, params (string Name, object? Value)[] parameters)
    {
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static (string Name, object? Value)[] DeploymentParameters(DeploymentRecord deployment) =>
    [
        ("$id", deployment.Id), ("$appId", deployment.AppId), ("$operationId", deployment.OperationId),
        ("$commitSha", deployment.CommitSha), ("$branch", deployment.Branch), ("$worktreePath", deployment.WorktreePath),
        ("$state", deployment.State), ("$startedUtc", ToDb(deployment.StartedUtc)),
        ("$completedUtc", ToDb(deployment.CompletedUtc)), ("$durationMs", deployment.DurationMilliseconds),
        ("$buildOutcome", deployment.BuildOutcome), ("$healthOutcome", deployment.HealthOutcome),
        ("$commitMessage", deployment.CommitMessage), ("$commitAuthor", deployment.CommitAuthor),
        ("$isKnownGood", deployment.IsKnownGood ? 1 : 0), ("$failureReason", deployment.FailureReason),
        ("$internalPort", deployment.InternalPort)
    ];

    private static DeploymentRecord ReadDeployment(SqliteDataReader reader) => new(
        reader.GetString(reader.GetOrdinal("id")), reader.GetString(reader.GetOrdinal("app_id")),
        reader.GetString(reader.GetOrdinal("operation_id")), reader.GetString(reader.GetOrdinal("commit_sha")),
        reader.GetString(reader.GetOrdinal("branch")), reader.GetString(reader.GetOrdinal("worktree_path")),
        reader.GetString(reader.GetOrdinal("state")), FromDb(reader.GetString(reader.GetOrdinal("started_utc")))!.Value,
        FromDb(GetNullableString(reader, "completed_utc")), GetNullableLong(reader, "duration_ms"),
        GetNullableString(reader, "build_outcome"), GetNullableString(reader, "health_outcome"),
        GetNullableString(reader, "commit_message"), GetNullableString(reader, "commit_author"),
        reader.GetInt32(reader.GetOrdinal("is_known_good")) == 1, GetNullableString(reader, "failure_reason"),
        GetNullableInt(reader, "internal_port"));

    private static OperationRecord ReadOperation(SqliteDataReader reader) => new(
        reader.GetString(reader.GetOrdinal("id")), reader.GetString(reader.GetOrdinal("action")),
        reader.GetString(reader.GetOrdinal("target_id")), reader.GetString(reader.GetOrdinal("status")),
        reader.GetInt32(reader.GetOrdinal("progress")), reader.GetString(reader.GetOrdinal("message")),
        FromDb(reader.GetString(reader.GetOrdinal("created_utc")))!.Value,
        FromDb(GetNullableString(reader, "started_utc")), FromDb(GetNullableString(reader, "completed_utc")),
        GetNullableString(reader, "error_code"), GetNullableString(reader, "error_message"));

    private static string? GetNullableString(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static int? GetNullableInt(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    private static long? GetNullableLong(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }

    private static string ToDb(DateTimeOffset value) => value.ToUniversalTime().ToString("O");
    private static string? ToDb(DateTimeOffset? value) => value is null ? null : ToDb(value.Value);
    private static DateTimeOffset? FromDb(string? value) => value is null ? null : DateTimeOffset.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind);

    private const string Migration1 = """
        CREATE TABLE project_state (
            app_id TEXT PRIMARY KEY,
            desired_state TEXT NOT NULL,
            runtime_state TEXT NOT NULL,
            health_state TEXT NOT NULL,
            active_deployment_id TEXT NULL,
            process_id INTEGER NULL,
            process_started_utc TEXT NULL,
            executable_path TEXT NULL,
            restart_count INTEGER NOT NULL DEFAULT 0,
            circuit_open_until_utc TEXT NULL,
            updated_utc TEXT NOT NULL,
            last_error TEXT NULL
        );

        CREATE TABLE deployments (
            id TEXT PRIMARY KEY,
            app_id TEXT NOT NULL,
            operation_id TEXT NOT NULL,
            commit_sha TEXT NOT NULL,
            branch TEXT NOT NULL,
            worktree_path TEXT NOT NULL,
            state TEXT NOT NULL,
            started_utc TEXT NOT NULL,
            completed_utc TEXT NULL,
            duration_ms INTEGER NULL,
            build_outcome TEXT NULL,
            health_outcome TEXT NULL,
            commit_message TEXT NULL,
            commit_author TEXT NULL,
            is_known_good INTEGER NOT NULL DEFAULT 0,
            failure_reason TEXT NULL,
            internal_port INTEGER NULL
        );

        CREATE TABLE operations (
            id TEXT PRIMARY KEY,
            action TEXT NOT NULL,
            target_id TEXT NOT NULL,
            status TEXT NOT NULL,
            progress INTEGER NOT NULL,
            message TEXT NOT NULL,
            created_utc TEXT NOT NULL,
            started_utc TEXT NULL,
            completed_utc TEXT NULL,
            error_code TEXT NULL,
            error_message TEXT NULL
        );

        CREATE TABLE audit_events (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            occurred_utc TEXT NOT NULL,
            actor TEXT NOT NULL,
            action TEXT NOT NULL,
            target_id TEXT NOT NULL,
            outcome TEXT NOT NULL,
            operation_id TEXT NULL,
            detail_json TEXT NOT NULL DEFAULT '{}',
            remote_address TEXT NULL
        );

        CREATE TABLE route_state (
            app_id TEXT PRIMARY KEY,
            hostname TEXT NOT NULL UNIQUE COLLATE NOCASE,
            deployment_id TEXT NULL,
            internal_port INTEGER NULL,
            status TEXT NOT NULL,
            static_path TEXT NULL,
            updated_utc TEXT NOT NULL
        );
        """;

    private const string Migration2 = """
        CREATE INDEX ix_deployments_app_started ON deployments(app_id, started_utc DESC);
        CREATE INDEX ix_deployments_good ON deployments(app_id, is_known_good, completed_utc DESC);
        CREATE INDEX ix_operations_created ON operations(created_utc DESC);
        CREATE INDEX ix_audit_occurred ON audit_events(occurred_utc DESC);
        """;

    private const string Migration3 = """
        CREATE TABLE retired_projects (
            app_id TEXT PRIMARY KEY COLLATE NOCASE,
            retired_utc TEXT NOT NULL
        );
        """;
}
