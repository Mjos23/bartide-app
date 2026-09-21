using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace TideCasa.Api.Infrastructure;

public sealed class ApplicationDatabase : IDisposable
{
    private readonly string connectionString;
    private readonly NpgsqlDataSource? postgres;
    private readonly bool createSchema;
    public bool IsPostgreSql { get; }
    public string Schema { get; } = "";

    public ApplicationDatabase(IConfiguration configuration, IHostEnvironment environment)
    {
        var provider = configuration["Storage:Provider"] ?? "Sqlite";
        IsPostgreSql = provider.Equals("PostgreSql", StringComparison.OrdinalIgnoreCase);
        if (!IsPostgreSql && configuration["ReverseProxy:Provider"] == "DigitalOceanAppPlatform")
            throw new InvalidOperationException("App Platform requires PostgreSQL durable storage.");
        if (IsPostgreSql)
        {
            Schema = configuration["Storage:PostgresSchema"] ?? "tide_casa";
            if (!Regex.IsMatch(Schema, "^tide_[a-z0-9_]{1,50}$", RegexOptions.CultureInvariant))
                throw new InvalidOperationException("Choose a separate tide_ application schema; shared provider schemas are not permitted.");
            var configuredConnection = configuration.GetConnectionString("Application");
            if (string.IsNullOrWhiteSpace(configuredConnection))
                throw new InvalidOperationException("PostgreSQL requires the private Application connection string.");
            NpgsqlConnectionStringBuilder settings;
            try { settings = new(configuredConnection); }
            catch { throw new InvalidOperationException("The PostgreSQL connection settings are invalid."); }
            if (!environment.IsDevelopment() && settings.SslMode != SslMode.VerifyFull)
                throw new InvalidOperationException("Production PostgreSQL connections require SSL Mode=VerifyFull.");
            settings.SearchPath = Schema;
            settings.MaxPoolSize = Math.Clamp(settings.MaxPoolSize, 1, 8);
            settings.MinPoolSize = 0;
            settings.Timeout = 10;
            settings.CommandTimeout = 20;
            settings.IncludeErrorDetail = false;
            settings.LogParameters = false;
            settings.ApplicationName = "TideCasa.Api";
            connectionString = settings.ConnectionString;
            postgres = NpgsqlDataSource.Create(connectionString);
            createSchema = environment.IsDevelopment() && configuration.GetValue<bool>("Storage:CreatePostgresSchema");
            return;
        }
        if (!provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose the Sqlite or PostgreSql storage provider.");
        var configured = configuration["Storage:DatabasePath"] ?? "App_Data/tide-casa.db";
        var path = Path.GetFullPath(configured, environment.ContentRootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            DefaultTimeout = 10
        }.ToString();
    }

    public async Task<DbConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        if (postgres is not null)
        {
            var db = await postgres.OpenConnectionAsync(cancellationToken);
            try
            {
                await using var setup = db.CreateCommand();
                if (createSchema)
                {
                    setup.CommandText = $"CREATE SCHEMA IF NOT EXISTS \"{Schema}\"";
                    await setup.ExecuteNonQueryAsync(cancellationToken);
                }
                setup.CommandText = "SET TIME ZONE 'UTC'; SET lock_timeout='10s'; SET idle_in_transaction_session_timeout='60s'";
                await setup.ExecuteNonQueryAsync(cancellationToken);
                setup.CommandText = "SELECT current_schema()";
                if (await setup.ExecuteScalarAsync(cancellationToken) as string != Schema)
                    throw new InvalidOperationException("The isolated PostgreSQL schema must be provisioned before starting the application.");
                return db;
            }
            catch { await db.DisposeAsync(); throw; }
        }
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public void Dispose() => postgres?.Dispose();
}
