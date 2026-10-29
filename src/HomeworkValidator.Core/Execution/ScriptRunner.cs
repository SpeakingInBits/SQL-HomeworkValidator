using Microsoft.Data.SqlClient;

namespace HomeworkValidator.Core.Execution;

/// <summary>The contents of every table in a database at one moment, keyed by table name.</summary>
public sealed record DatabaseSnapshot(IReadOnlyDictionary<string, ResultTable> Tables);

/// <param name="Snapshot">Every table after the script ran.</param>
/// <param name="EndedTransaction">
/// The script's own COMMIT or ROLLBACK ended the validator's transaction, so its changes may have been saved.
/// The database then has to be rebuilt.
/// </param>
public sealed record ScriptRun(DatabaseSnapshot Snapshot, bool EndedTransaction)
{
    /// <summary>The database's design after the script; only captured for DDL questions.</summary>
    public SchemaSnapshot? Schema { get; init; }

    /// <summary>What each of the question's probes did, in order; only for DDL questions.</summary>
    public IReadOnlyList<ProbeResult> Probes { get; init; } = [];
}

/// <param name="Error">SQL Server's message when the probe failed; null when it succeeded.</param>
/// <param name="Result">The probe's last result set, if it returned one.</param>
public sealed record ProbeResult(string Name, string? Error, ResultTable? Result)
{
    public bool Succeeded => Error is null;
}

/// <summary>
/// Runs a script that changes data and captures every table afterwards, without keeping the changes:
/// everything happens inside a transaction that's rolled back at the end.
/// </summary>
/// <remarks>
/// Rolled-back INSERTs still use up identity values, so each run first resets every identity counter to the
/// table's highest value. That way the reference solution and the student's script get the same IDs.
/// </remarks>
public sealed class ScriptRunner(ServerConnection server)
{
    public int TimeoutSeconds { get; init; } = 15;

    /// <summary>Tables bigger than this aren't captured in full; the course tables have a few hundred rows.</summary>
    public int MaxRowsPerTable { get; init; } = 50_000;

    /// <param name="setup">Trusted batches that set up the starting state, e.g. an earlier question's reference solution.</param>
    /// <param name="script">The batches to check.</param>
    /// <param name="restricted">
    /// Run <paramref name="script"/> as a database user that can read, write and create or drop tables in this database only.
    /// Student scripts always run restricted.
    /// </param>
    /// <param name="probes">
    /// For DDL questions: scripts to try after the main script, each rolled back on its own, to see how the design behaves.
    /// When given, the schema is captured too.
    /// </param>
    public async Task<ScriptRun> RunAsync(string database, IReadOnlyList<string> setup, IReadOnlyList<string> script, bool restricted,
        CancellationToken ct = default, IReadOnlyList<(string Name, string Sql)>? probes = null)
    {
        // Unpooled, because the impersonation below can't be undone and must not leak to other work.
        await using var connection = await server.OpenAsync(database, pooling: false, ct);
        await ExecuteAsync(connection, "SET XACT_ABORT OFF; BEGIN TRANSACTION;", ct);

        try
        {
            await ResetIdentitiesAsync(connection, ct);

            foreach (var batch in setup)
            {
                try
                {
                    await ExecuteAsync(connection, batch, ct);
                }
                catch (SqlException ex)
                {
                    throw new InvalidOperationException($"The question's setup failed: {ex.Message}");
                }
            }

            if (restricted)
                await ExecuteAsync(connection, $"EXECUTE AS USER = '{DatabaseManager.WriterUser}' WITH NO REVERT;", ct);

            foreach (var batch in script)
            {
                try
                {
                    await ExecuteAsync(connection, batch, ct);
                }
                catch (SqlException ex)
                {
                    throw QueryRunner.Describe(ex, TimeoutSeconds, "script");
                }
            }

            var endedTransaction = await ScalarAsync<int>(connection, "SELECT @@TRANCOUNT", ct) == 0;
            var run = new ScriptRun(await SnapshotAsync(connection, ct), endedTransaction);
            if (probes is null)
                return run;

            return run with
            {
                Schema = await SchemaReader.ReadAsync(connection, ct),
                Probes = endedTransaction ? [] : await RunProbesAsync(connection, probes, ct),
            };
        }
        finally
        {
            // Whatever happened, throw the changes away. If the script already ended the transaction there's nothing to roll back.
            if (connection.State == System.Data.ConnectionState.Open)
                await ExecuteAsync(connection, "IF @@TRANCOUNT > 0 ROLLBACK;", CancellationToken.None);
        }
    }

    /// <summary>
    /// Runs each probe inside a savepoint and rolls it back, so probes don't affect each other.
    /// A probe that fails records SQL Server's message; one that succeeds records its last result set.
    /// </summary>
    private async Task<IReadOnlyList<ProbeResult>> RunProbesAsync(SqlConnection connection, IReadOnlyList<(string Name, string Sql)> probes, CancellationToken ct)
    {
        var results = new List<ProbeResult>();
        foreach (var (name, sql) in probes)
        {
            await ExecuteAsync(connection, "SAVE TRANSACTION hv_probe;", ct);
            string? error = null;
            ResultTable? result = null;
            try
            {
                await using var command = new SqlCommand(sql, connection) { CommandTimeout = TimeoutSeconds };
                await using var reader = await command.ExecuteReaderAsync(ct);
                do
                {
                    if (reader.FieldCount > 0)
                        result = await QueryRunner.ReadAsync(reader, command, 1_000, $"Probe '{name}' returned too many rows.", ct);
                }
                while (await reader.NextResultAsync(ct));
            }
            catch (SqlException ex)
            {
                // Only the first message: "The statement has been terminated." and similar follow-ups add nothing.
                error = ex.Errors.Count > 0 ? ex.Errors[0].Message : ex.Message;
            }

            results.Add(new ProbeResult(name, error, result));

            // A failed probe can leave the transaction unusable; then the remaining probes can't run either.
            var state = await ScalarAsync<short>(connection, "SELECT CAST(XACT_STATE() AS smallint)", ct);
            if (state != 1)
                break;
            await ExecuteAsync(connection, "ROLLBACK TRANSACTION hv_probe;", ct);
        }

        return results;
    }

    /// <summary>Reads every user table. Used for the state after a script, and in tests.</summary>
    public async Task<DatabaseSnapshot> SnapshotAsync(SqlConnection connection, CancellationToken ct)
    {
        var names = new List<string>();
        await using (var list = new SqlCommand(
            "SELECT QUOTENAME(s.name) + '.' + QUOTENAME(t.name), t.name FROM sys.tables t " +
            "JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE t.is_ms_shipped = 0 ORDER BY t.name", connection))
        await using (var reader = await list.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                names.Add(reader.GetString(0) + "\u001F" + reader.GetString(1));
        }

        var tables = new Dictionary<string, ResultTable>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in names)
        {
            var (quoted, name) = (entry.Split('\u001F')[0], entry.Split('\u001F')[1]);
            await using var command = new SqlCommand($"SELECT * FROM {quoted}", connection) { CommandTimeout = TimeoutSeconds };
            await using var reader = await command.ExecuteReaderAsync(ct);
            tables[name] = await QueryRunner.ReadAsync(reader, command, MaxRowsPerTable,
                $"Table {name} has more than {MaxRowsPerTable:N0} rows, too many to compare.", ct);
        }

        return new DatabaseSnapshot(tables);
    }

    /// <summary>Sets every identity counter to the table's highest current value, so the next INSERT gets max + 1.</summary>
    private async Task ResetIdentitiesAsync(SqlConnection connection, CancellationToken ct)
    {
        // Builds one "SET @max = ...; DBCC CHECKIDENT (...)" pair per identity table and runs them together.
        // Empty tables are skipped; the course databases have none with identity columns.
        const string sql = """
            DECLARE @sql nvarchar(max) = N'DECLARE @max bigint; ';
            SELECT @sql += N'SET @max = (SELECT MAX(' + QUOTENAME(c.name) + N') FROM ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) + N'); ' +
                           N'IF @max IS NOT NULL DBCC CHECKIDENT (' + QUOTENAME(QUOTENAME(s.name) + N'.' + QUOTENAME(t.name), '''') +
                           N', RESEED, @max) WITH NO_INFOMSGS; '
            FROM sys.identity_columns c
            JOIN sys.tables t ON t.object_id = c.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE t.is_ms_shipped = 0;
            EXEC sys.sp_executesql @sql;
            """;
        await ExecuteAsync(connection, sql, ct);
    }

    private async Task ExecuteAsync(SqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = TimeoutSeconds };
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<T> ScalarAsync<T>(SqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new SqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(ct))!;
    }
}
