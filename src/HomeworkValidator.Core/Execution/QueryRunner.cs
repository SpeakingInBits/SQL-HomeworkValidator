using Microsoft.Data.SqlClient;

namespace HomeworkValidator.Core.Execution;

/// <summary>The columns and rows a query returned. Values are as SqlClient returns them; null for SQL NULL.</summary>
public sealed record ResultTable(IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows);

public sealed class QueryFailedException(string message) : Exception(message);

/// <summary>Runs a single query and reads its result set, with a timeout and a row cap.</summary>
public sealed class QueryRunner(ServerConnection server)
{
    public int TimeoutSeconds { get; init; } = 15;
    public int MaxRows { get; init; } = 10_000;

    /// <summary>
    /// Runs <paramref name="sql"/> in <paramref name="database"/>. When <paramref name="restricted"/> is true the query runs as
    /// the read-only database user, which can't change data or reach other databases. Student queries always run restricted.
    /// </summary>
    public async Task<ResultTable> RunAsync(string database, string sql, bool restricted, CancellationToken ct = default)
    {
        try
        {
            // An unpooled connection, because the impersonation below can't be undone and must not leak to other work.
            await using var connection = await server.OpenAsync(database, pooling: !restricted, ct);
            if (restricted)
            {
                await using var impersonate = new SqlCommand(
                    $"EXECUTE AS USER = '{DatabaseManager.ReaderUser}' WITH NO REVERT;", connection);
                await impersonate.ExecuteNonQueryAsync(ct);
            }

            await using var command = new SqlCommand(sql, connection) { CommandTimeout = TimeoutSeconds };
            await using var reader = await command.ExecuteReaderAsync(ct);

            if (reader.FieldCount == 0)
                throw new QueryFailedException("The query didn't return a result set.");

            var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
            var rows = new List<object?[]>();
            while (await reader.ReadAsync(ct))
            {
                if (rows.Count == MaxRows)
                {
                    command.Cancel();
                    throw new QueryFailedException($"The query returned more than {MaxRows:N0} rows. Check your WHERE clause and joins.");
                }

                var values = new object?[reader.FieldCount];
                reader.GetValues(values!);
                for (var i = 0; i < values.Length; i++)
                {
                    if (values[i] is DBNull)
                        values[i] = null;
                }
                rows.Add(values);
            }

            if (await reader.NextResultAsync(ct))
                throw new QueryFailedException("The query returned more than one result set.");

            return new ResultTable(columns, rows);
        }
        catch (SqlException ex) when (ex.Number == -2)
        {
            throw new QueryFailedException($"The query took longer than {TimeoutSeconds} seconds and was stopped.");
        }
        catch (SqlException ex)
        {
            var messages = ex.Errors.Cast<SqlError>()
                .Select(e => e.LineNumber > 0 ? $"Line {e.LineNumber}: {e.Message}" : e.Message)
                .Distinct();
            throw new QueryFailedException(string.Join(Environment.NewLine, messages));
        }
    }
}
