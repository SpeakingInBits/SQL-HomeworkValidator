using System.Security.Cryptography;
using System.Text;
using HomeworkValidator.Core.Content;
using HomeworkValidator.Core.Sql;
using Microsoft.Data.SqlClient;

namespace HomeworkValidator.Core.Execution;

public enum DatabaseState
{
    Missing,
    Current,
    Outdated,

    /// <summary>A database with the validator's name exists but the validator didn't create it. It is never touched.</summary>
    NotManaged,
}

public sealed record DatabaseStatus(string Name, CourseDatabase Database, Dataset Dataset, DatabaseState State);

/// <summary>
/// Creates and maintains the validator's own databases, named HV_&lt;CourseDb&gt;_&lt;dataset&gt;.
/// Every database it creates is marked with an extended property, and it only ever drops databases carrying that mark,
/// so a student's own databases are never touched.
/// </summary>
public sealed class DatabaseManager(ServerConnection server)
{
    public const string Prefix = "HV_";

    /// <summary>The low-privilege database user student queries run as.</summary>
    public const string ReaderUser = "hv_reader";

    /// <summary>The database user student DML scripts run as: read, write, and create or drop tables, in this database only.</summary>
    public const string WriterUser = "hv_writer";

    /// <summary>
    /// Part of every database's version. Raise it when the way databases are built changes (such as a new database user),
    /// so existing databases are rebuilt.
    /// </summary>
    private const int BuildVersion = 3;

    private const string ManagedProperty = "HV_Managed";
    private const string VersionProperty = "HV_ContentVersion";

    public static string DatabaseName(CourseDatabase database, Dataset dataset) => $"{Prefix}{database.Name}_{dataset.Name}";

    public async Task<IReadOnlyList<DatabaseStatus>> GetStatusAsync(ContentLibrary content, CancellationToken ct = default)
    {
        var result = new List<DatabaseStatus>();
        foreach (var database in content.Databases)
        {
            foreach (var dataset in database.Datasets)
                result.Add(await GetStatusAsync(database, dataset, ct));
        }

        return result;
    }

    public async Task<DatabaseStatus> GetStatusAsync(CourseDatabase database, Dataset dataset, CancellationToken ct = default)
    {
        var name = DatabaseName(database, dataset);
        var (managed, version) = await ReadMarkersAsync(name, ct);
        var state = managed switch
        {
            null => DatabaseState.Missing,
            false => DatabaseState.NotManaged,
            true when version == ContentVersion(database, dataset) => DatabaseState.Current,
            true => DatabaseState.Outdated,
        };
        return new DatabaseStatus(name, database, dataset, state);
    }

    /// <summary>Builds the database if it's missing or out of date. Returns true if it had to be built.</summary>
    public async Task<bool> EnsureAsync(CourseDatabase database, Dataset dataset, CancellationToken ct = default)
    {
        var status = await GetStatusAsync(database, dataset, ct);
        switch (status.State)
        {
            case DatabaseState.Current:
                return false;
            case DatabaseState.NotManaged:
                throw new InvalidOperationException(
                    $"A database named {status.Name} already exists on {server.Server} but wasn't created by the validator. " +
                    "Rename or drop it, then try again.");
            case DatabaseState.Outdated:
                await DropAsync(status.Name, ct);
                break;
        }

        await CreateAsync(status.Name, database, dataset, ct);
        return true;
    }

    /// <summary>Drops and rebuilds a database, e.g. after a student's COMMIT saved changes the validator meant to roll back.</summary>
    public async Task RebuildAsync(CourseDatabase database, Dataset dataset, CancellationToken ct = default)
    {
        var status = await GetStatusAsync(database, dataset, ct);
        if (status.State == DatabaseState.NotManaged)
            return;
        if (status.State != DatabaseState.Missing)
            await DropAsync(status.Name, ct);
        await CreateAsync(status.Name, database, dataset, ct);
    }

    /// <summary>Drops every database the validator created. Returns their names.</summary>
    public async Task<IReadOnlyList<string>> DropAllAsync(CancellationToken ct = default)
    {
        var names = new List<string>();
        await using (var master = await server.OpenMasterAsync(ct))
        {
            await using var command = new SqlCommand("SELECT name FROM sys.databases WHERE name LIKE 'HV[_]%'", master);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                names.Add(reader.GetString(0));
        }

        var dropped = new List<string>();
        foreach (var name in names)
        {
            var (managed, _) = await ReadMarkersAsync(name, ct);
            if (managed == true)
            {
                await DropAsync(name, ct);
                dropped.Add(name);
            }
        }

        return dropped;
    }

    private async Task CreateAsync(string name, CourseDatabase database, Dataset dataset, CancellationToken ct)
    {
        await using (var master = await server.OpenMasterAsync(ct))
        {
            await ExecuteAsync(master, $"CREATE DATABASE {Quote(name)}", ct);
        }

        // Mark it first so a build that fails halfway can still be cleaned up; the version is written last.
        await using var connection = await server.OpenAsync(name, ct: ct);
        await SetPropertyAsync(connection, ManagedProperty, "1", ct);

        await RunScriptAsync(connection, database.SchemaSql, $"{database.Name} schema", ct);
        await RunScriptAsync(connection, dataset.Sql, $"{database.Name} '{dataset.Name}' dataset", ct);

        await ExecuteAsync(connection,
            $"CREATE USER {Quote(ReaderUser)} WITHOUT LOGIN; ALTER ROLE db_datareader ADD MEMBER {Quote(ReaderUser)}; " +
            $"CREATE USER {Quote(WriterUser)} WITHOUT LOGIN; ALTER ROLE db_datareader ADD MEMBER {Quote(WriterUser)}; " +
            $"ALTER ROLE db_datawriter ADD MEMBER {Quote(WriterUser)}; ALTER ROLE db_ddladmin ADD MEMBER {Quote(WriterUser)}; " +
            // Creating a foreign key needs REFERENCES on the table it points to, which db_ddladmin doesn't include.
            $"GRANT REFERENCES TO {Quote(WriterUser)};", ct);
        await SetPropertyAsync(connection, VersionProperty, ContentVersion(database, dataset), ct);
    }

    private async Task DropAsync(string name, CancellationToken ct)
    {
        SqlConnection.ClearAllPools();
        await using var master = await server.OpenMasterAsync(ct);
        await ExecuteAsync(master,
            $"ALTER DATABASE {Quote(name)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {Quote(name)};", ct);
    }

    /// <summary>Returns (null, null) if the database doesn't exist, (false, null) if it isn't the validator's.</summary>
    private async Task<(bool? Managed, string? Version)> ReadMarkersAsync(string name, CancellationToken ct)
    {
        await using var master = await server.OpenMasterAsync(ct);
        await using (var exists = new SqlCommand("SELECT DB_ID(@name)", master))
        {
            exists.Parameters.AddWithValue("@name", name);
            if (await exists.ExecuteScalarAsync(ct) is DBNull or null)
                return (null, null);
        }

        await using var command = new SqlCommand(
            $"SELECT name, CAST(value AS nvarchar(200)) FROM {Quote(name)}.sys.extended_properties " +
            "WHERE class = 0 AND name IN (@managed, @version)", master);
        command.Parameters.AddWithValue("@managed", ManagedProperty);
        command.Parameters.AddWithValue("@version", VersionProperty);

        bool managed = false;
        string? version = null;
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (reader.GetString(0) == ManagedProperty)
                managed = true;
            else
                version = reader.GetString(1);
        }

        return (managed, version);
    }

    /// <summary>Runs a content script batch by batch. A failure is a problem with the content, not the connection.</summary>
    private static async Task RunScriptAsync(SqlConnection connection, string script, string description, CancellationToken ct)
    {
        foreach (var batch in BatchSplitter.Split(script))
        {
            try
            {
                await ExecuteAsync(connection, batch, ct, timeoutSeconds: 120);
            }
            catch (SqlException ex)
            {
                var start = batch.TrimStart().ReplaceLineEndings(" ");
                throw new ContentException(
                    $"The {description} script failed: {ex.Message} (in the batch starting '{start[..Math.Min(80, start.Length)]}')");
            }
        }
    }

    private static async Task SetPropertyAsync(SqlConnection connection, string property, string value, CancellationToken ct)
    {
        await using var command = new SqlCommand("EXEC sys.sp_addextendedproperty @name = @n, @value = @v", connection);
        command.Parameters.AddWithValue("@n", property);
        command.Parameters.AddWithValue("@v", value);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql, CancellationToken ct, int timeoutSeconds = 30)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = timeoutSeconds };
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>A hash of the schema and dataset scripts, so a content change triggers a rebuild.</summary>
    private static string ContentVersion(CourseDatabase database, Dataset dataset)
    {
        var bytes = Encoding.UTF8.GetBytes(
            $"build {BuildVersion}\n" + database.SchemaSql.ReplaceLineEndings("\n") + "\n--dataset--\n" + dataset.Sql.ReplaceLineEndings("\n"));
        return Convert.ToHexString(SHA256.HashData(bytes))[..16];
    }

    private static string Quote(string identifier) => $"[{identifier.Replace("]", "]]")}]";
}
