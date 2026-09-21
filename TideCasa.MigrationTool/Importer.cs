using System.Buffers.Binary;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Npgsql;
using NpgsqlTypes;
using TideCasa.Api.Infrastructure;

namespace TideCasa.MigrationTool;

internal sealed class Importer(ApplicationDatabase database, Options options, ImportReport report)
{
    private const string Ledger = "tide_postgres_migrations";
    private const string KeyRing = "tide_data_protection_keys";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task RunAsync(CancellationToken ct)
    {
        report.Phase = "manifest";
        var manifest = LoadManifest();
        var tables = manifest.Tables.Where(t => t.Name != KeyRing).OrderBy(t => t.Name, StringComparer.Ordinal).ToArray();
        report.Phase = "source_validation";
        EnsureNoSidecars();
        // On Windows this also denies writers/deletion for the complete operation.
        // An immutable URI prevents SQLite from creating journal/WAL/shared-memory files.
        await using var sourceFile = new FileStream(options.Source, FileMode.Open, FileAccess.Read, FileShare.Read);
        report.SourceFileSha256 = await FileDigestAsync(sourceFile, ct);
        var uri = new Uri(options.Source).AbsoluteUri + "?mode=ro&immutable=1";
        await using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = uri, Mode = SqliteOpenMode.ReadOnly, Pooling = false, ForeignKeys = true
        }.ConnectionString);
        await source.OpenAsync(ct);
        await using var sourceTransaction = source.BeginTransaction(deferred: true);
        await ExecuteAsync(source, sourceTransaction, "PRAGMA query_only=ON", ct);
        await ValidateSourceAsync(source, sourceTransaction, tables, ct);
        var sourceEncoding = await ReadSourceEncodingAsync(source, sourceTransaction, ct);
        foreach (var table in tables)
        {
            var digest = await DigestAsync(source, sourceTransaction, table, null, ct, sourceEncoding);
            report.Tables.Add(new(table.Name, digest.Count, digest.Hash));
        }
        await CheckSourceUnchangedAsync(sourceFile, ct);

        if (options.Apply)
        {
            report.Phase = "target_baseline";
            // The baseline has its own atomic transaction. Failed imports may leave an
            // empty reviewed baseline, never partially copied application records.
            await new PostgresSchemaMigrator(database).InitializeAsync(ct);
        }
        report.Phase = "target_validation";
        await using var target = await database.OpenAsync(ct);
        await using var transaction = target.BeginTransaction(deferred: !options.Apply);
        var targetTables = await TargetTableNamesAsync(target, transaction, ct);
        if (targetTables.Count == 0)
        {
            if (options.Apply) throw new ImportFailure("target_baseline_missing");
            report.BaselineRequired = true;
            await CheckSourceUnchangedAsync(sourceFile, ct);
            report.Status = "dry_run_validated_baseline_required";
            return;
        }
        var expected = manifest.Tables.Select(t => t.Name).Append(Ledger).ToHashSet(StringComparer.Ordinal);
        if (!targetTables.SetEquals(expected)) throw new ImportFailure("target_table_inventory_mismatch");
        if (options.Apply)
        {
            // Also block writers that do not participate in the application's advisory lock.
            await ExecuteAsync(target, transaction,
                "LOCK TABLE " + string.Join(",", tables.Select(t => Qualified(t.Name))) + " IN ACCESS EXCLUSIVE MODE", ct);
        }
        var targetTypes = await ValidateTargetAsync(target, transaction, tables, ct);
        foreach (var table in tables)
        {
            var item = report.Tables.Single(r => r.Name == table.Name);
            await using var count = target.CreateCommand();
            count.Transaction = transaction;
            count.CommandText = "SELECT count(*) FROM " + Qualified(table.Name);
            item.DestinationRows = (long)(await count.ExecuteScalarAsync(ct))!;
        }
        if (report.Tables.Any(t => t.DestinationRows != 0)) throw new ImportFailure("destination_not_empty");
        if (!options.Apply)
        {
            await CheckSourceUnchangedAsync(sourceFile, ct);
            report.Status = "dry_run_validated_empty_destination";
            return;
        }

        await ExecuteAsync(target, transaction, "SET CONSTRAINTS ALL DEFERRED", ct);
        report.Phase = "copying";
        foreach (var table in tables)
            await CopyAsync(source, sourceTransaction, (NpgsqlConnection)target, (NpgsqlTransaction)transaction, table, targetTypes[table.Name], ct);
        report.Phase = "verification";
        await ExecuteAsync(target, transaction, "SET CONSTRAINTS ALL IMMEDIATE", ct);
        foreach (var table in tables)
        {
            var digest = await DigestAsync(target, transaction, table, database.Schema, ct);
            var item = report.Tables.Single(r => r.Name == table.Name);
            item.DestinationRows = digest.Count;
            item.DestinationSha256 = digest.Hash;
            item.Matches = item.SourceRows == digest.Count && item.SourceSha256 == digest.Hash;
            if (item.Matches != true) throw new ImportFailure("destination_digest_mismatch");
        }
        await CheckSourceUnchangedAsync(sourceFile, ct);
        report.Phase = "commit";
        report.CommitAttempted = true;
        await transaction.CommitAsync(ct);
        report.Committed = true;
        report.Status = "committed_and_verified";
        report.Phase = "complete";
    }

    private Manifest LoadManifest()
    {
        var assembly = typeof(PostgresSchemaMigrator).Assembly;
        using var stream = assembly.GetManifestResourceStream("TideCasa.Api.PostgresMigrations.manifest.json")
            ?? throw new ImportFailure("embedded_manifest_missing");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        report.ManifestSha256 = Hex(SHA256.HashData(bytes));
        var manifest = JsonSerializer.Deserialize<Manifest>(bytes, JsonOptions)
            ?? throw new ImportFailure("embedded_manifest_invalid");
        if (manifest.FormatVersion != 1 || manifest.SqliteApplicationTables != 76 || manifest.Tables.Count != 77 ||
            manifest.Tables.Count(t => t.Name == KeyRing) != 1 || manifest.Migrations.Count != 1 ||
            manifest.Tables.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count() != manifest.Tables.Count ||
            manifest.Tables.Any(t => !IsIdentifier(t.Name) || t.Name == Ledger || t.Columns.Count == 0 ||
                t.Columns.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count() != t.Columns.Count ||
                t.Columns.Any(c => !IsIdentifier(c.Name) || c.Type is not ("text" or "bigint"))))
            throw new ImportFailure("embedded_manifest_invalid");
        var migration = manifest.Migrations[0];
        if (migration.ResourceName != "TideCasa.Api.PostgresMigrations.0001_baseline.sql")
            throw new ImportFailure("embedded_baseline_invalid");
        using var sql = assembly.GetManifestResourceStream(migration.ResourceName)
            ?? throw new ImportFailure("embedded_baseline_missing");
        if (Hex(SHA256.HashData(sql)) != migration.Sha256) throw new ImportFailure("embedded_baseline_checksum_mismatch");
        return manifest;
    }

    private async Task ValidateSourceAsync(SqliteConnection source, SqliteTransaction transaction, Table[] tables, CancellationToken ct)
    {
        await using (var command = source.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "PRAGMA integrity_check";
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct) || reader.GetString(0) != "ok" || await reader.ReadAsync(ct))
                throw new ImportFailure("source_integrity_check_failed");
        }
        await using (var command = source.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "PRAGMA foreign_key_check";
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct)) throw new ImportFailure("source_foreign_key_check_failed");
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = source.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT name,type FROM sqlite_schema WHERE type IN ('table','view','trigger') AND name NOT GLOB 'sqlite_*'";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (reader.GetString(1) != "table") throw new ImportFailure("source_unexpected_view_or_trigger");
                names.Add(reader.GetString(0));
            }
        }
        if (!names.SetEquals(tables.Select(t => t.Name))) throw new ImportFailure("source_table_inventory_mismatch");
        foreach (var table in tables)
        {
            await using var command = source.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "PRAGMA table_xinfo(" + Quote(table.Name) + ")";
            await using var reader = await command.ExecuteReaderAsync(ct);
            var index = 0;
            while (await reader.ReadAsync(ct))
            {
                if (index >= table.Columns.Count) throw new ImportFailure("source_column_mapping_mismatch");
                var column = table.Columns[index++];
                var expectedType = column.Type == "bigint" ? "INTEGER" : "TEXT";
                if (reader.GetString(1) != column.Name || !string.Equals(reader.GetString(2), expectedType, StringComparison.OrdinalIgnoreCase) ||
                    (reader.GetInt64(3) != 0) != column.SqliteNotNull || reader.GetInt64(5) != column.PrimaryKeyOrdinal || reader.GetInt64(6) != 0)
                    throw new ImportFailure("source_column_mapping_mismatch");
            }
            if (index != table.Columns.Count) throw new ImportFailure("source_column_mapping_mismatch");
        }
    }

    private static async Task<HashSet<string>> TargetTableNamesAsync(DbConnection target, DbTransaction transaction, CancellationToken ct)
    {
        await using var command = target.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT c.relname,c.relkind::text,c.relrowsecurity,c.relforcerowsecurity,c.relpersistence::text
            FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname=current_schema() AND c.relkind IN ('r','p','v','m','S','f')
            """;
        var tables = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (reader.GetString(1) != "r" || reader.GetBoolean(2) || reader.GetBoolean(3) || reader.GetString(4) != "p")
                throw new ImportFailure("target_unexpected_table_object_or_policy");
            tables.Add(reader.GetString(0));
        }
        return tables;
    }

    private static async Task<Dictionary<string, NpgsqlDbType[]>> ValidateTargetAsync(DbConnection target, DbTransaction transaction, Table[] tables, CancellationToken ct)
    {
        var result = new Dictionary<string, NpgsqlDbType[]>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            await using var command = target.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT a.attname,pg_catalog.format_type(a.atttypid,a.atttypmod),a.attnotnull,a.attidentity::text,a.attgenerated::text
                FROM pg_catalog.pg_attribute a JOIN pg_catalog.pg_class c ON c.oid=a.attrelid
                JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                WHERE n.nspname=current_schema() AND c.relname=@table AND a.attnum>0 AND NOT a.attisdropped ORDER BY a.attnum
                """;
            command.Parameters.Add(new NpgsqlParameter("table", NpgsqlDbType.Text) { Value = table.Name });
            await using var reader = await command.ExecuteReaderAsync(ct);
            var types = new List<NpgsqlDbType>();
            while (await reader.ReadAsync(ct))
            {
                if (types.Count >= table.Columns.Count) throw new ImportFailure("target_column_mapping_mismatch");
                var column = table.Columns[types.Count];
                if (reader.GetString(0) != column.Name || reader.GetString(1) != column.Type || reader.GetBoolean(2) != column.NotNull ||
                    reader.GetString(3) != "" || reader.GetString(4) != "")
                    throw new ImportFailure("target_column_mapping_mismatch");
                types.Add(reader.GetString(1) == "text" ? NpgsqlDbType.Text : NpgsqlDbType.Bigint);
            }
            if (types.Count != table.Columns.Count) throw new ImportFailure("target_column_mapping_mismatch");
            result.Add(table.Name, types.ToArray());
        }
        await using (var command = target.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT count(*) FROM pg_catalog.pg_constraint k JOIN pg_catalog.pg_namespace n ON n.oid=k.connamespace
                WHERE n.nspname=current_schema() AND (NOT k.convalidated OR (k.contype='f' AND NOT k.condeferrable))
                """;
            if ((long)(await command.ExecuteScalarAsync(ct))! != 0) throw new ImportFailure("target_constraints_not_validated_or_deferrable");
            command.CommandText = """
                SELECT count(*) FROM pg_catalog.pg_trigger t JOIN pg_catalog.pg_class c ON c.oid=t.tgrelid
                JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=current_schema() AND NOT t.tgisinternal
                """;
            if ((long)(await command.ExecuteScalarAsync(ct))! != 0) throw new ImportFailure("target_unexpected_trigger");
        }
        return result;
    }

    private async Task CopyAsync(SqliteConnection source, SqliteTransaction sourceTransaction, NpgsqlConnection target,
        NpgsqlTransaction transaction, Table table, NpgsqlDbType[] types, CancellationToken ct)
    {
        await using var select = source.CreateCommand();
        select.Transaction = sourceTransaction;
        select.CommandText = "SELECT " + Columns(table) + " FROM " + Quote(table.Name);
        await using var reader = await select.ExecuteReaderAsync(ct);
        await using var insert = target.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO " + Qualified(table.Name) + " (" + Columns(table) + ") VALUES (" +
            string.Join(",", Enumerable.Range(0, table.Columns.Count).Select(i => "@p" + i)) + ")";
        for (var i = 0; i < types.Length; i++) insert.Parameters.Add(new NpgsqlParameter("p" + i, types[i]));
        await insert.PrepareAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            for (var i = 0; i < types.Length; i++) insert.Parameters[i].Value = CheckedValue(reader.GetValue(i), table.Columns[i]);
            if (await insert.ExecuteNonQueryAsync(ct) != 1) throw new ImportFailure("insert_row_count_mismatch");
            report.RowsCopied++;
        }
    }

    private static async Task<Encoding> ReadSourceEncodingAsync(SqliteConnection source, SqliteTransaction transaction, CancellationToken ct)
    {
        await using var command = source.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA encoding";
        return (string)(await command.ExecuteScalarAsync(ct))! switch
        {
            "UTF-8" => Utf8,
            "UTF-16le" => new UnicodeEncoding(false, false, true),
            "UTF-16be" => new UnicodeEncoding(true, false, true),
            _ => throw new ImportFailure("source_unsupported_text_encoding")
        };
    }

    private static async Task<Digest> DigestAsync(DbConnection connection, DbTransaction transaction, Table table, string? schema,
        CancellationToken ct, Encoding? sourceEncoding = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var rawText = sourceEncoding is null ? "" : string.Concat(table.Columns.Where(c => c.Type == "text")
            .Select(c => ",CAST(" + Quote(c.Name) + " AS BLOB)"));
        command.CommandText = "SELECT " + Columns(table) + rawText + " FROM " + (schema is null ? "" : Quote(schema) + ".") + Quote(table.Name);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<byte[]>();
        while (await reader.ReadAsync(ct))
        {
            using var row = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var rawOrdinal = table.Columns.Count;
            for (var i = 0; i < table.Columns.Count; i++)
            {
                var value = CheckedValue(reader.GetValue(i), table.Columns[i]);
                if (sourceEncoding is not null && table.Columns[i].Type == "text")
                {
                    var raw = reader.GetValue(rawOrdinal++);
                    if (value is not DBNull)
                    {
                        // SQLite's string reader can replace malformed input. Validate the
                        // original TEXT bytes before hashing so this cannot become data loss.
                        try
                        {
                            if (raw is not byte[] bytes || sourceEncoding.GetString(bytes) != (string)value)
                                throw new ImportFailure("source_invalid_unicode");
                        }
                        catch (DecoderFallbackException) { throw new ImportFailure("source_invalid_unicode"); }
                    }
                }
                if (value is DBNull) row.AppendData([0]);
                else if (value is long number)
                {
                    row.AppendData([1]);
                    AppendInt64(row, number);
                }
                else
                {
                    row.AppendData([2]);
                    AppendText(row, (string)value);
                }
            }
            rows.Add(row.GetHashAndReset());
        }
        // Sorting hashes makes the digest independent of provider row order and collation,
        // and retains duplicate multiplicity. Only 32 bytes per row plus list overhead.
        rows.Sort((left, right) => left.AsSpan().SequenceCompareTo(right));
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendText(digest, "tide-import-v1");
        AppendText(digest, table.Name);
        AppendInt64(digest, table.Columns.Count);
        foreach (var column in table.Columns) { AppendText(digest, column.Name); AppendText(digest, column.Type); }
        AppendInt64(digest, rows.Count);
        foreach (var row in rows) digest.AppendData(row);
        return new(rows.Count, Hex(digest.GetHashAndReset()));
    }

    private static object CheckedValue(object value, Column column)
    {
        if (value is DBNull)
        {
            if (column.NotNull) throw new ImportFailure("source_value_incompatible_with_required_column");
            return DBNull.Value;
        }
        if (column.Type == "bigint" && value is long) return value;
        if (column.Type == "text" && value is string text && !text.Contains('\0'))
        {
            try { _ = Utf8.GetByteCount(text); }
            catch (EncoderFallbackException) { throw new ImportFailure("source_invalid_unicode"); }
            return text;
        }
        throw new ImportFailure("source_value_type_not_losslessly_supported");
    }

    private void EnsureNoSidecars()
    {
        if (new[] { "-wal", "-shm", "-journal" }.Any(suffix => File.Exists(options.Source + suffix)))
            throw new ImportFailure("source_must_be_consistent_backup_without_sidecars");
    }

    private async Task CheckSourceUnchangedAsync(FileStream stream, CancellationToken ct)
    {
        EnsureNoSidecars();
        report.SourceUnchanged = await FileDigestAsync(stream, ct) == report.SourceFileSha256;
        if (!report.SourceUnchanged) throw new ImportFailure("source_changed_during_import");
    }

    private static async Task<string> FileDigestAsync(FileStream stream, CancellationToken ct)
    {
        stream.Position = 0;
        return Hex(await SHA256.HashDataAsync(stream, ct));
    }

    private static async Task ExecuteAsync(DbConnection connection, DbTransaction transaction, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    private string Qualified(string table) => Quote(database.Schema) + "." + Quote(table);
    private static string Columns(Table table) => string.Join(",", table.Columns.Select(c => Quote(c.Name)));
    private static bool IsIdentifier(string value) => Regex.IsMatch(value, "^[a-z][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant);
    private static string Quote(string identifier) => IsIdentifier(identifier) ? "\"" + identifier + "\"" : throw new ImportFailure("invalid_identifier");
    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
    private static void AppendText(IncrementalHash hash, string text) { var bytes = Utf8.GetBytes(text); AppendInt64(hash, bytes.LongLength); hash.AppendData(bytes); }
    private static void AppendInt64(IncrementalHash hash, long value) { Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(bytes, value); hash.AppendData(bytes); }

    private sealed record Manifest(int FormatVersion, int SqliteApplicationTables, List<Table> Tables, List<Migration> Migrations);
    private sealed record Migration(string ResourceName, string Sha256);
    private sealed record Table(string Name, List<Column> Columns);
    private sealed record Column(string Name, string Type, bool NotNull, bool SqliteNotNull, int PrimaryKeyOrdinal);
    private sealed record Digest(long Count, string Hash);
}
