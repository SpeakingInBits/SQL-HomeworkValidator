using Microsoft.Data.SqlClient;

namespace HomeworkValidator.Core.Execution;

public sealed record ServerInfo(string Version, string Edition, bool CanCreateDatabases);

/// <summary>How to reach the student's SQL Server. Builds connection strings for the server and for each validator database.</summary>
public sealed class ServerConnection
{
    private readonly SqlConnectionStringBuilder _builder;

    public ServerConnection(string connectionString)
    {
        _builder = new SqlConnectionStringBuilder(connectionString);
    }

    /// <summary>
    /// Typical student setups: Windows authentication on localhost (SQL Server installed locally),
    /// or SQL authentication as sa on localhost,1433 (the Docker image on macOS and Linux).
    /// Local installs and the Docker image use self-signed certificates, so the certificate is trusted.
    /// </summary>
    public static ServerConnection Create(string? server, string? user, string? password)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server ?? (OperatingSystem.IsWindows() ? "localhost" : "localhost,1433"),
            TrustServerCertificate = true,
            ApplicationName = "HomeworkValidator",
            ConnectTimeout = 10,
        };

        if (user is null && OperatingSystem.IsWindows())
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.UserID = user ?? "sa";
            builder.Password = password ?? "";
        }

        return new ServerConnection(builder.ConnectionString);
    }

    public string Server => _builder.DataSource;

    /// <summary>Connects and reports the server version and whether the login can create the validator's databases.</summary>
    public async Task<ServerInfo> TestAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenMasterAsync(ct);
        await using var command = new SqlCommand(
            "SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(50)), CAST(SERVERPROPERTY('Edition') AS nvarchar(100)), " +
            "HAS_PERMS_BY_NAME(NULL, NULL, 'CREATE ANY DATABASE')", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new ServerInfo(reader.GetString(0), reader.GetString(1), reader.GetInt32(2) == 1);
    }

    public Task<SqlConnection> OpenMasterAsync(CancellationToken ct = default) => OpenAsync("master", pooling: true, ct);

    public async Task<SqlConnection> OpenAsync(string database, bool pooling = true, CancellationToken ct = default)
    {
        var builder = new SqlConnectionStringBuilder(_builder.ConnectionString)
        {
            InitialCatalog = database,
            Pooling = pooling,
        };
        var connection = new SqlConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
