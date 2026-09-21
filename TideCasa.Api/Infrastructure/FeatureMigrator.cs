using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace TideCasa.Api.Infrastructure;

/// <summary>Append-only C# feature migrations, separate from the immutable imported baseline.</summary>
public sealed class FeatureMigrator(ApplicationDatabase database)
{
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var assembly = typeof(FeatureMigrator).Assembly;
        const string prefix = "TideCasa.Api.FeatureMigrations.";
        var resources = assembly.GetManifestResourceNames().Where(name => name.StartsWith(prefix, StringComparison.Ordinal)
            && name.EndsWith(".sql", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
        await using var db = await database.OpenAsync(ct);
        using var tx = db.BeginTransaction(deferred: false);
        await using (var create = db.CreateCommand())
        {
            create.Transaction = tx;
            create.CommandText = "CREATE TABLE IF NOT EXISTS tide_feature_migrations(version INTEGER PRIMARY KEY,resource_name TEXT NOT NULL UNIQUE,sha256 TEXT NOT NULL,applied_at TEXT NOT NULL)";
            await create.ExecuteNonQueryAsync(ct);
        }
        var history = new List<(int Version, string Resource, string Hash)>();
        await using (var query = db.CreateCommand())
        {
            query.Transaction = tx; query.CommandText = "SELECT version,resource_name,sha256 FROM tide_feature_migrations ORDER BY version";
            await using var reader = await query.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) history.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
        }
        if (history.Count > resources.Length) throw new InvalidOperationException("Feature migration history is newer than this application. Restore the matching application version.");
        var expectedTables = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < resources.Length; index++)
        {
            var resource = resources[index];
            if (!resource.StartsWith(prefix + (index + 1).ToString("D4") + "_", StringComparison.Ordinal))
                throw new InvalidOperationException("Feature migrations must be a contiguous ordered sequence.");
            using var stream = assembly.GetManifestResourceStream(resource) ?? throw new InvalidOperationException("Missing feature migration.");
            using var memory = new MemoryStream(); stream.CopyTo(memory); var bytes = memory.ToArray();
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var sql = new UTF8Encoding(false, true).GetString(bytes);
            foreach (Match match in Regex.Matches(sql, @"(?im)^\s*CREATE\s+TABLE\s+([A-Za-z_][A-Za-z0-9_]*)")) expectedTables.Add(match.Groups[1].Value);
            if (index < history.Count)
            {
                if (history[index] != (index + 1, resource, hash)) throw new InvalidOperationException("An applied feature migration differs from its recorded checksum.");
                continue;
            }
            await using var apply = db.CreateCommand(); apply.Transaction = tx; apply.CommandText = sql;
            await apply.ExecuteNonQueryAsync(ct);
            await using var record = db.CreateCommand(); record.Transaction = tx;
            record.CommandText = "INSERT INTO tide_feature_migrations(version,resource_name,sha256,applied_at) VALUES($version,$resource,$hash,$at)";
            record.Parameters.AddWithValue("$version", index + 1); record.Parameters.AddWithValue("$resource", resource);
            record.Parameters.AddWithValue("$hash", hash); record.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            await record.ExecuteNonQueryAsync(ct);
        }
        foreach (var table in expectedTables)
        {
            await using var present = db.CreateCommand(); present.Transaction = tx;
            present.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name=$name";
            present.Parameters.AddWithValue("$name", table);
            if (Convert.ToInt64(await present.ExecuteScalarAsync(ct)) != 1) throw new InvalidOperationException("A recorded feature table is missing. Restore or review the database before restarting.");
        }
        await using (var check = db.CreateCommand())
        {
            check.Transaction = tx; check.CommandText = "PRAGMA foreign_key_check";
            await using var reader = await check.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct)) throw new InvalidOperationException("Feature migrations failed the database relationship check.");
        }
        await tx.CommitAsync(ct);
    }
}
