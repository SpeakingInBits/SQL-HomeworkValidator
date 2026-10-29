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

            var result = await ReadAsync(reader, command, MaxRows,
                $"The query returned more than {MaxRows:N0} rows. Check your WHERE clause and joins.", ct);

            if (await reader.NextResultAsync(ct))
                throw new QueryFailedException("The query returned more than one result set.");

            return result;
        }
        catch (SqlException ex)
        {
            throw Describe(ex, TimeoutSeconds, "query");
        }
    }

    /// <summary>Reads the current result set, stopping with <paramref name="tooManyRows"/> past <paramref name="maxRows"/>.</summary>
    internal static async Task<ResultTable> ReadAsync(SqlDataReader reader, SqlCommand command, int maxRows, string tooManyRows, CancellationToken ct)
    {
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
        var rows = new List<object?[]>();
        while (await reader.ReadAsync(ct))
        {
            if (rows.Count == maxRows)
            {
                command.Cancel();
                throw new QueryFailedException(tooManyRows);
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

        return new ResultTable(columns, rows);
    }

    /// <summary>Turns a SqlException into a message for the student, with line numbers.</summary>
    internal static QueryFailedException Describe(SqlException ex, int timeoutSeconds, string what)
    {
        if (ex.Number == -2)
            return new QueryFailedException($"The {what} took longer than {timeoutSeconds} seconds and was stopped.");

        var messages = ex.Errors.Cast<SqlError>()
            .Select(e => e.LineNumber > 0 ? $"Line {e.LineNumber}: {e.Message}" : e.Message)
            .Distinct();
        return new QueryFailedException(string.Join(Environment.NewLine, messages));
    }
}
