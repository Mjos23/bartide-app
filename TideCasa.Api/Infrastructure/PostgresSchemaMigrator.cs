using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TideCasa.Api.Infrastructure;

/// <summary>
/// Installs the PostgreSQL baseline atomically in the configured, isolated application
/// schema. This never imports records or infers history from pre-existing tables.
/// </summary>
public sealed class PostgresSchemaMigrator(ApplicationDatabase database)
{
    private const string ResourcePrefix = "TideCasa.Api.PostgresMigrations.";
    private const string HistoryTable = "tide_postgres_migrations";
    private static readonly string[] HistoryColumns =
        ["version", "resource_name", "sha256", "manifest_sha256", "schema_sha256", "applied_at"];

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!database.IsPostgreSql)
            throw new InvalidOperationException("The PostgreSQL migrator requires the PostgreSQL storage provider.");
        var bundle = LoadBaseline();
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        // Exactly the same key as application writes. Acquire before inspecting history
        // so concurrent startups and invariant-protecting writes cannot race the DDL.
        await ExecuteAsync(connection, transaction,
            "SELECT pg_advisory_xact_lock(hashtext(current_database()),hashtext(current_schema()))", cancellationToken);
        await using (var scope = connection.CreateCommand())
        {
            scope.Transaction = transaction;
            scope.CommandText = "SELECT current_schema(),array_to_string(current_schemas(false),',')";
            await using var reader = await scope.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0) ||
                reader.GetString(0) != database.Schema || reader.GetString(1) != database.Schema)
                throw new InvalidOperationException("PostgreSQL migrations require only the isolated application schema in search_path.");
        }

        var objects = await ReadRowsAsync(connection, transaction, """
            SELECT c.relname::text,c.relkind::text
            FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname=current_schema() AND c.relkind IN ('r','p','v','m','S','f')
            UNION ALL
            SELECT p.proname::text,'function'::text
            FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
            WHERE n.nspname=current_schema()
            """, cancellationToken);
        var hasLedger = objects.Any(row => row[0] == HistoryTable && row[1] == "r");
        if (!hasLedger && objects.Count != 0)
            throw new InvalidOperationException("The PostgreSQL application schema is not empty and has no migration ledger. Use the reviewed import procedure.");

        if (!hasLedger)
            await ExecuteAsync(connection, transaction, """
                CREATE TABLE tide_postgres_migrations (
                    version BIGINT NOT NULL PRIMARY KEY,
                    resource_name TEXT COLLATE "C" NOT NULL UNIQUE,
                    sha256 TEXT COLLATE "C" NOT NULL,
                    manifest_sha256 TEXT COLLATE "C" NOT NULL,
                    schema_sha256 TEXT COLLATE "C" NOT NULL,
                    applied_at TEXT COLLATE "C" NOT NULL
                )
                """, cancellationToken);

        var metadata = await ReadMetadataAsync(connection, transaction, bundle.Manifest, cancellationToken);
        ValidateLedger(metadata);
        var history = new List<HistoryEntry>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT version,resource_name,sha256,manifest_sha256,schema_sha256 FROM tide_postgres_migrations ORDER BY version";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                history.Add(new(Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
                    reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
                if (history.Count > 1)
                    throw new InvalidOperationException("The PostgreSQL migration history contains unknown versions. A reviewed migration is required.");
            }
        }

        if (history.Count == 0)
        {
            if (objects.Any(row => row[0] != HistoryTable || row[1] != "r"))
                throw new InvalidOperationException("The PostgreSQL schema has untracked objects. No baseline was inferred and no application data was changed.");
            // The complete initial baseline creates 77 tables and 87 foreign keys.
            // Allow bounded DDL time without changing normal query or lock deadlines.
            await ExecuteAsync(connection, transaction, bundle.Sql, cancellationToken, commandTimeoutSeconds: 120);
            metadata = await ReadMetadataAsync(connection, transaction, bundle.Manifest, cancellationToken);
            ValidateBaseline(metadata, bundle.Manifest);
            var schemaHash = SchemaHash(metadata);
            await using var record = connection.CreateCommand();
            record.Transaction = transaction;
            record.CommandText = """
                INSERT INTO tide_postgres_migrations(version,resource_name,sha256,manifest_sha256,schema_sha256,applied_at)
                VALUES (@version,@resource,@script,@manifest,@schema,@now)
                """;
            record.Parameters.AddWithValue("@version", bundle.Entry.Version);
            record.Parameters.AddWithValue("@resource", bundle.Entry.ResourceName);
            record.Parameters.AddWithValue("@script", bundle.Entry.Sha256);
            record.Parameters.AddWithValue("@manifest", bundle.ManifestHash);
            record.Parameters.AddWithValue("@schema", schemaHash);
            record.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await record.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            var entry = history[0];
            if (entry.Version != bundle.Entry.Version || entry.ResourceName != bundle.Entry.ResourceName ||
                entry.Sha256 != bundle.Entry.Sha256 || entry.ManifestSha256 != bundle.ManifestHash)
                throw new InvalidOperationException("The PostgreSQL migration history or immutable checksums differ from the embedded baseline. No schema changes were applied.");
            ValidateBaseline(metadata, bundle.Manifest);
            if (entry.SchemaSha256 != SchemaHash(metadata))
                throw new InvalidOperationException("The recorded PostgreSQL schema has drifted. Review table, column, constraint, index, trigger and parsing-function definitions before starting.");
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static BaselineBundle LoadBaseline()
    {
        var assembly = typeof(PostgresSchemaMigrator).Assembly;
        byte[] Resource(string name)
        {
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException("An embedded PostgreSQL migration resource is missing.");
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
        var bytes = Resource(ResourcePrefix + "manifest.json");
        var manifest = JsonSerializer.Deserialize<MigrationManifest>(bytes,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (manifest is null || manifest.FormatVersion != 1 || manifest.Migrations is not { Count: 1 } ||
            manifest.Tables is not { Count: 77 } || manifest.Functions is not { Count: 2 })
            throw new InvalidOperationException("The embedded PostgreSQL baseline manifest is invalid.");
        var entry = manifest.Migrations[0];
        if (entry.Version != 1 || entry.ResourceName != ResourcePrefix + "0001_baseline.sql" ||
            !Regex.IsMatch(entry.Sha256, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("The PostgreSQL migration manifest has an unexpected version or checksum.");
        var scripts = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal)).ToArray();
        if (scripts.Length != 1 || scripts[0] != entry.ResourceName)
            throw new InvalidOperationException("The embedded PostgreSQL scripts do not match their manifest.");
        static bool Identifier(string value) => Regex.IsMatch(value, "^[a-z][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant);
        if (manifest.Tables.Select(table => table.Name).Distinct(StringComparer.Ordinal).Count() != manifest.Tables.Count ||
            manifest.Tables.Any(table => !Identifier(table.Name) || table.Name == HistoryTable || table.Columns is not { Count: > 0 } ||
                table.Constraints is null || table.Indexes is null ||
                table.Columns.Any(column => !Identifier(column.Name) || column.Type is not ("text" or "bigint"))))
            throw new InvalidOperationException("The PostgreSQL baseline inventory is invalid.");
        var sql = Resource(entry.ResourceName);
        if (Hash(sql) != entry.Sha256)
            throw new InvalidOperationException("An embedded PostgreSQL migration checksum differs from its manifest.");
        return new(manifest, entry, Hash(bytes), new UTF8Encoding(false, true).GetString(sql));
    }

    private static async Task<SchemaMetadata> ReadMetadataAsync(DbConnection connection, DbTransaction transaction,
        MigrationManifest manifest, CancellationToken cancellationToken)
    {
        var tables = manifest.Tables.Select(table => table.Name).Append(HistoryTable).ToHashSet(StringComparer.Ordinal);
        var functions = manifest.Functions.Select(function => function.Name).ToHashSet(StringComparer.Ordinal);
        var rows = new List<string[]>();
        async Task Read(string kind, string sql, HashSet<string> names)
        {
            foreach (var row in await ReadRowsAsync(connection, transaction, sql, cancellationToken))
                if (names.Contains(row[0])) rows.Add([kind, .. row]);
        }
        await Read("table", """
            SELECT c.relname::text,c.relkind::text,c.relrowsecurity::text,c.relforcerowsecurity::text,c.relpersistence::text
            FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname=current_schema() AND c.relkind IN ('r','p','v','m','f')
            """, tables);
        await Read("column", """
            SELECT c.relname::text,a.attnum::text,a.attname::text,pg_catalog.format_type(a.atttypid,a.atttypmod),
                a.attnotnull::text,COALESCE(pg_catalog.pg_get_expr(d.adbin,d.adrelid),''),
                COALESCE(coll.collname::text,''),a.attidentity::text,a.attgenerated::text
            FROM pg_catalog.pg_attribute a JOIN pg_catalog.pg_class c ON c.oid=a.attrelid
            JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            LEFT JOIN pg_catalog.pg_attrdef d ON d.adrelid=a.attrelid AND d.adnum=a.attnum
            LEFT JOIN pg_catalog.pg_collation coll ON coll.oid=a.attcollation
            WHERE n.nspname=current_schema() AND a.attnum>0 AND NOT a.attisdropped
            """, tables);
        await Read("constraint", """
            SELECT c.relname::text,k.conname::text,k.contype::text,pg_catalog.pg_get_constraintdef(k.oid,true),
                k.convalidated::text,k.condeferrable::text,k.condeferred::text
            FROM pg_catalog.pg_constraint k JOIN pg_catalog.pg_class c ON c.oid=k.conrelid
            JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname=current_schema() AND k.contype IN ('p','u','c','f','x')
            """, tables);
        await Read("index", """
            SELECT c.relname::text,i.relname::text,x.indisunique::text,x.indisprimary::text,
                x.indisvalid::text,x.indisready::text,pg_catalog.pg_get_indexdef(i.oid)
            FROM pg_catalog.pg_index x JOIN pg_catalog.pg_class c ON c.oid=x.indrelid
            JOIN pg_catalog.pg_class i ON i.oid=x.indexrelid
            JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname=current_schema()
            """, tables);
        await Read("trigger", """
            SELECT c.relname::text,t.tgname::text,t.tgenabled::text,pg_catalog.pg_get_triggerdef(t.oid,true)
            FROM pg_catalog.pg_trigger t JOIN pg_catalog.pg_class c ON c.oid=t.tgrelid
            JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname=current_schema() AND NOT t.tgisinternal
            """, tables);
        await Read("function", """
            SELECT p.proname::text,pg_catalog.oidvectortypes(p.proargtypes),pg_catalog.pg_get_function_result(p.oid),
                p.provolatile::text,p.proisstrict::text,p.prosecdef::text,p.proparallel::text,
                COALESCE(array_to_string(p.proconfig,','),''),pg_catalog.pg_get_functiondef(p.oid)
            FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
            WHERE n.nspname=current_schema() AND p.prokind='f'
            """, functions);
        return new(rows);
    }

    private static void ValidateLedger(SchemaMetadata metadata)
    {
        var columns = metadata.Rows.Where(row => row[0] == "column" && row[1] == HistoryTable)
            .OrderBy(row => int.Parse(row[2], CultureInfo.InvariantCulture)).ToArray();
        if (columns.Length != HistoryColumns.Length || columns.Where((row, index) =>
                row[3] != HistoryColumns[index] || row[4] != (index == 0 ? "bigint" : "text") || row[5] != "true").Any())
            throw new InvalidOperationException("The PostgreSQL migration ledger structure is missing or invalid.");
    }

    private static void ValidateBaseline(SchemaMetadata metadata, MigrationManifest manifest)
    {
        ValidateLedger(metadata);
        foreach (var table in manifest.Tables)
        {
            var relation = metadata.Rows.SingleOrDefault(row => row[0] == "table" && row[1] == table.Name);
            if (relation is null || relation[2] != "r" || relation[3] != "false" || relation[4] != "false" || relation[5] != "p")
                throw new InvalidOperationException($"The PostgreSQL application table '{table.Name}' is missing or has an unexpected definition.");
            var columns = metadata.Rows.Where(row => row[0] == "column" && row[1] == table.Name)
                .OrderBy(row => int.Parse(row[2], CultureInfo.InvariantCulture)).ToArray();
            if (columns.Length != table.Columns.Count)
                throw new InvalidOperationException($"The PostgreSQL column inventory differs for '{table.Name}'.");
            for (var index = 0; index < columns.Length; index++)
            {
                var row = columns[index];
                var expected = table.Columns[index];
                if (row[3] != expected.Name || row[4] != expected.Type || row[5] != (expected.NotNull ? "true" : "false") ||
                    row[7] != (expected.Type == "text" ? "C" : "") || row[8] != "" || row[9] != "")
                    throw new InvalidOperationException($"The PostgreSQL column definition differs for '{table.Name}.{expected.Name}'.");
            }
            var constraints = metadata.Rows.Where(row => row[0] == "constraint" && row[1] == table.Name).ToArray();
            if (constraints.Length != table.Constraints.Count || table.Constraints.Any(expected =>
                    !constraints.Any(row => row[2] == expected.Name && row[3] == expected.Kind && row[5] == "true" &&
                        row[6] == (expected.Kind == "f" ? "true" : "false") && row[7] == "false")))
                throw new InvalidOperationException($"The PostgreSQL constraints differ for '{table.Name}'.");
            var indexes = metadata.Rows.Where(row => row[0] == "index" && row[1] == table.Name).ToArray();
            var expectedIndexes = table.Indexes.Select(index => (index.Name, index.Unique, Primary: false))
                .Concat(table.Constraints.Where(constraint => constraint.Kind is "p" or "u")
                    .Select(constraint => (constraint.Name, Unique: true, Primary: constraint.Kind == "p"))).ToArray();
            if (indexes.Length != expectedIndexes.Length || expectedIndexes.Any(expected => !indexes.Any(row =>
                    row[2] == expected.Name && row[3] == (expected.Unique ? "true" : "false") &&
                    row[4] == (expected.Primary ? "true" : "false") && row[5] == "true" && row[6] == "true")))
                throw new InvalidOperationException($"The PostgreSQL indexes differ for '{table.Name}'.");
        }
        if (metadata.Rows.Any(row => row[0] == "trigger"))
            throw new InvalidOperationException("Unexpected application triggers require a reviewed PostgreSQL migration.");
        var functions = metadata.Rows.Where(row => row[0] == "function").ToArray();
        if (functions.Length != manifest.Functions.Count || manifest.Functions.Any(expected => !functions.Any(row =>
                row[1] == expected.Name && row[2] == expected.ArgumentTypes && row[3] == expected.ReturnType &&
                row[4] == expected.Volatility && row[5] == "true" && row[6] == "false" && row[7] == "s" && row[8] == "search_path=pg_catalog")))
            throw new InvalidOperationException("The PostgreSQL safe parsing functions are missing or differ from the baseline.");
    }

    private static string SchemaHash(SchemaMetadata metadata)
    {
        // Hash only catalog metadata, never application values. JSON separates values
        // unambiguously; ordinal sorting makes retrieval order irrelevant.
        var rows = metadata.Rows.Select(row => JsonSerializer.Serialize(row)).Order(StringComparer.Ordinal);
        return Hash(Encoding.UTF8.GetBytes(string.Join('\n', rows)));
    }

    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static async Task<List<string[]>> ReadRowsAsync(DbConnection connection, DbTransaction transaction,
        string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        var rows = new List<string[]>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new string[reader.FieldCount];
            for (var index = 0; index < row.Length; index++)
                row[index] = reader.IsDBNull(index) ? "" : reader.GetString(index);
            rows.Add(row);
        }
        return rows;
    }

    private static async Task ExecuteAsync(DbConnection connection, DbTransaction transaction, string sql,
        CancellationToken cancellationToken, int? commandTimeoutSeconds = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        if (commandTimeoutSeconds is { } timeout) command.CommandTimeout = timeout;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record MigrationManifest(int FormatVersion, List<ManifestEntry> Migrations, List<TableEntry> Tables, List<FunctionEntry> Functions);
    private sealed record ManifestEntry(int Version, string ResourceName, string Sha256);
    private sealed record TableEntry(string Name, List<ColumnEntry> Columns, List<ConstraintEntry> Constraints, List<IndexEntry> Indexes);
    private sealed record ColumnEntry(string Name, string Type, bool NotNull);
    private sealed record ConstraintEntry(string Name, string Kind);
    private sealed record IndexEntry(string Name, bool Unique);
    private sealed record FunctionEntry(string Name, string ArgumentTypes, string ReturnType, string Volatility);
    private sealed record BaselineBundle(MigrationManifest Manifest, ManifestEntry Entry, string ManifestHash, string Sql);
    private sealed record HistoryEntry(int Version, string ResourceName, string Sha256, string ManifestSha256, string SchemaSha256);
    private sealed record SchemaMetadata(List<string[]> Rows);
}
