using Microsoft.Data.SqlClient;

namespace HomeworkValidator.Core.Execution;

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
