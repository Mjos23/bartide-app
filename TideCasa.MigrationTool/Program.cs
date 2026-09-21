using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Npgsql;
using TideCasa.Api.Infrastructure;

namespace TideCasa.MigrationTool;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        var report = new ImportReport();
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        try
        {
            var options = Options.Parse(args);
            report.Mode = options.Apply ? "apply" : "dry-run";
            // No environment variables, user secrets, appsettings or CLI connection strings.
            var configuration = new ConfigurationBuilder().AddJsonFile(options.Settings, optional: false)
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:CreatePostgresSchema"] = "false" }).Build();
            if (!string.Equals(configuration["Storage:Provider"], "PostgreSql", StringComparison.OrdinalIgnoreCase))
                throw new ImportFailure("postgresql_settings_required");
            if (options.LocalTest)
            {
                var settings = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("Application"));
                if (settings.Host is not ("127.0.0.1" or "::1")) throw new ImportFailure("local_test_requires_literal_loopback");
            }
            using var database = new ApplicationDatabase(configuration, new MigrationEnvironment(options.LocalTest));
            await new Importer(database, options, report).RunAsync(cancellation.Token);
            Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
            return 0;
        }
        catch (Exception exception)
        {
            // Provider error detail can contain complete contact records and credentials.
            // Never serialize exception messages, stacks, paths, query text or parameters.
            report.Status = "failed";
            report.CommitOutcomeUnknown = report.CommitAttempted && !report.Committed;
            report.DatabaseErrorCode = exception is PostgresException postgres ? postgres.SqlState : null;
            report.ErrorCode = exception switch
            {
                ImportFailure failure => failure.Code,
                OperationCanceledException => "cancelled",
                PostgresException { SqlState: "55P03" } => "destination_busy",
                PostgresException => "postgresql_rejected_operation",
                NpgsqlException => "postgresql_connection_or_operation_failed",
                Microsoft.Data.Sqlite.SqliteException => "sqlite_validation_or_read_failed",
                JsonException => "invalid_json_configuration_or_manifest",
                IOException => "file_access_failed",
                UnauthorizedAccessException => "file_access_denied",
                _ => "configuration_or_validation_failed"
            };
            Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
            return 1;
        }
    }
}

internal sealed record Options(string Source, string Settings, bool Apply, bool LocalTest)
{
    public static Options Parse(string[] args)
    {
        string? source = null, settings = null;
        var apply = false;
        var localTest = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--source" when source is null && i + 1 < args.Length: source = args[++i]; break;
                case "--settings" when settings is null && i + 1 < args.Length: settings = args[++i]; break;
                case "--apply" when !apply: apply = true; break;
                case "--local-test" when !localTest: localTest = true; break;
                default: throw new ImportFailure("usage_source_settings_and_optional_apply_local_test");
            }
        }
        if (source is null || settings is null || !Path.IsPathFullyQualified(source) || !Path.IsPathFullyQualified(settings))
            throw new ImportFailure("absolute_source_and_settings_paths_required");
        source = Path.GetFullPath(source);
        settings = Path.GetFullPath(settings);
        if (!File.Exists(source) || !File.Exists(settings) || string.Equals(source, settings, StringComparison.OrdinalIgnoreCase))
            throw new ImportFailure("source_and_settings_files_required");
        return new(source, settings, apply, localTest);
    }
}

internal sealed class MigrationEnvironment(bool localTest) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = localTest ? Environments.Development : Environments.Production;
    public string ApplicationName { get; set; } = "TideCasa.MigrationTool";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

internal sealed class ImportFailure(string code) : Exception
{
    public string Code { get; } = code;
}

internal sealed class ImportReport
{
    public string Mode { get; set; } = "dry-run";
    public string Status { get; set; } = "validating";
    public string Phase { get; set; } = "settings";
    public string? ErrorCode { get; set; }
    public string? DatabaseErrorCode { get; set; }
    public bool Committed { get; set; }
    public bool CommitAttempted { get; set; }
    public bool CommitOutcomeUnknown { get; set; }
    public bool BaselineRequired { get; set; }
    public bool SourceUnchanged { get; set; }
    public long RowsCopied { get; set; }
    public string? SourceFileSha256 { get; set; }
    public string? ManifestSha256 { get; set; }
    public string HashFormat { get; } = "tide-import-v1-sha256-sorted-row-digests";
    public List<TableReport> Tables { get; } = [];
}

internal sealed class TableReport(string name, long sourceRows, string sourceSha256)
{
    public string Name { get; } = name;
    public long SourceRows { get; } = sourceRows;
    public string SourceSha256 { get; } = sourceSha256;
    public long? DestinationRows { get; set; }
    public string? DestinationSha256 { get; set; }
    public bool? Matches { get; set; }
}
