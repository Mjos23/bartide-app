using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Data.Common;

namespace TideCasa.Api.Infrastructure;

/// <summary>
/// Installs the reviewed, schema-only legacy baseline. This is not a D1 data importer.
/// Its scripts and history commit together; an untracked existing application database
/// requires a separate reviewed import/baseline procedure.
/// </summary>
public sealed class SchemaMigrator(ApplicationDatabase database)
{
    private const string ResourcePrefix = "TideCasa.Api.Migrations.";
    private const string HistoryTable = "tide_schema_migrations";
    private static readonly string[] ExpectedSources =
    [
        "drizzle/0000_optimal_shaman.sql", "drizzle/0001_wooden_wendell_rand.sql",
        "drizzle/0002_closed_rumiko_fujikawa.sql", "drizzle/0003_jittery_epoch.sql",
        "drizzle/0004_even_sally_floyd.sql", "drizzle/0005_acoustic_ego.sql",
        "drizzle/0006_perfect_union_jack.sql", "drizzle/0007_spicy_vulcan.sql",
        "drizzle/0008_yielding_elektra.sql", "db/independent/0001_auth.sql",
        "db/independent/0002_stripe.sql", "db/independent/0003_contact.sql",
        "db/independent/0004_maintenance.sql", "db/independent/0005_referrals.sql"
    ];

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var (manifestHash, migrations) = LoadMigrations();
        await using var connection = await database.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        var existingTables = await TablesAsync(connection, transaction, cancellationToken);
        var hasHistory = existingTables.Contains(HistoryTable);

        if (!hasHistory && existingTables.Any(name => name != "demo_requests"))
            throw new InvalidOperationException("Existing application tables have no migration history. A reviewed migration is required; no schema changes were applied.");

        await using (var create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS tide_schema_migrations (
                    version INTEGER PRIMARY KEY,
                    resource_name TEXT NOT NULL UNIQUE,
                    source_path TEXT NOT NULL,
                    sha256 TEXT NOT NULL,
                    manifest_sha256 TEXT NOT NULL,
                    applied_at TEXT NOT NULL
                );
                """;
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        var history = new List<HistoryEntry>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT version,resource_name,source_path,sha256,manifest_sha256 FROM tide_schema_migrations ORDER BY version";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                history.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        }

        if (history.Count == 0)
        {
            if (existingTables.Any(name => name is not ("demo_requests" or HistoryTable)))
                throw new InvalidOperationException("Existing application tables have no recorded baseline. A reviewed migration is required.");
            var appliedAt = DateTimeOffset.UtcNow.ToString("O");
            foreach (var migration in migrations)
            {
                await using var apply = connection.CreateCommand();
                apply.Transaction = transaction;
                apply.CommandText = migration.Sql;
                await apply.ExecuteNonQueryAsync(cancellationToken);

                await using var record = connection.CreateCommand();
                record.Transaction = transaction;
                record.CommandText = """
                    INSERT INTO tide_schema_migrations(version,resource_name,source_path,sha256,manifest_sha256,applied_at)
                    VALUES ($version,$resource,$source,$hash,$manifest,$now)
                    """;
                record.Parameters.AddWithValue("$version", migration.Entry.Version);
                record.Parameters.AddWithValue("$resource", migration.Entry.ResourceName);
                record.Parameters.AddWithValue("$source", migration.Entry.Source);
                record.Parameters.AddWithValue("$hash", migration.Entry.Sha256);
                record.Parameters.AddWithValue("$manifest", manifestHash);
                record.Parameters.AddWithValue("$now", appliedAt);
                await record.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        else
        {
            // This baseline is one atomic installation, never an inferred partial import.
            if (history.Count != migrations.Count)
                throw new InvalidOperationException("The migration baseline is incomplete or unknown. A reviewed migration is required.");
            for (var index = 0; index < history.Count; index++)
            {
                var row = history[index];
                var expected = migrations[index].Entry;
                if (row.Version != expected.Version || row.ResourceName != expected.ResourceName ||
                    row.Source != expected.Source || row.Sha256 != expected.Sha256 || row.ManifestSha256 != manifestHash)
                    throw new InvalidOperationException("Migration history or checksums differ from the embedded baseline. No schema changes were applied.");
            }
        }

        var tables = await TablesAsync(connection, transaction, cancellationToken);
        const string tablePattern = """(?im)^\s*CREATE\s+TABLE(?:\s+IF\s+NOT\s+EXISTS)?\s+[`"]?([A-Za-z_][A-Za-z0-9_]*)""";
        foreach (var migration in migrations)
            foreach (Match match in Regex.Matches(migration.Sql, tablePattern))
                if (!tables.Contains(match.Groups[1].Value))
                    throw new InvalidOperationException("A recorded application table is missing. A reviewed migration is required.");

        await using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = "PRAGMA foreign_key_check";
            await using var violations = await check.ExecuteReaderAsync(cancellationToken);
            if (await violations.ReadAsync(cancellationToken))
                throw new InvalidOperationException("The application database failed its foreign-key check. No migration was committed.");
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static (string Hash, List<LoadedMigration> Migrations) LoadMigrations()
    {
        var assembly = typeof(SchemaMigrator).Assembly;
        byte[] Resource(string name)
        {
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException("An embedded migration resource is missing.");
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
        var manifestBytes = Resource(ResourcePrefix + "manifest.json");
        var manifest = JsonSerializer.Deserialize<MigrationManifest>(manifestBytes,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (manifest is null || manifest.FormatVersion != 1 || manifest.Migrations is null || manifest.Migrations.Count != ExpectedSources.Length)
            throw new InvalidOperationException("The embedded migration manifest is invalid.");
        var resources = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        if (resources.Count != ExpectedSources.Length)
            throw new InvalidOperationException("The embedded migration resources do not match the baseline.");
        var loaded = new List<LoadedMigration>();
        for (var index = 0; index < ExpectedSources.Length; index++)
        {
            var entry = manifest.Migrations[index];
            var source = ExpectedSources[index];
            var group = source.StartsWith("drizzle/", StringComparison.Ordinal) ? "drizzle" : "independent";
            var resourceName = ResourcePrefix + $"{index + 1:D4}_{group}_{Path.GetFileName(source)}";
            if (entry.Version != index + 1 || entry.Source != source || entry.ResourceName != resourceName || !resources.Contains(resourceName))
                throw new InvalidOperationException("The migration manifest has an unexpected order or source.");
            var bytes = Resource(resourceName);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (hash != entry.Sha256)
                throw new InvalidOperationException("An embedded migration checksum differs from its manifest.");
            using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            loaded.Add(new(entry, reader.ReadToEnd()));
        }
        return (Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant(), loaded);
    }

    private static async Task<HashSet<string>> TablesAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' AND name NOT GLOB 'sqlite_*'";
        var tables = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) tables.Add(reader.GetString(0));
        return tables;
    }

    private sealed record MigrationManifest(int FormatVersion, List<ManifestEntry> Migrations);
    private sealed record ManifestEntry(int Version, string ResourceName, string Source, string Sha256);
    private sealed record LoadedMigration(ManifestEntry Entry, string Sql);
    private sealed record HistoryEntry(int Version, string ResourceName, string Source, string Sha256, string ManifestSha256);
}
