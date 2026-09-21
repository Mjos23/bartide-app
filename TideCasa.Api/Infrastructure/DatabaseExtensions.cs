using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace TideCasa.Api.Infrastructure;

/// <summary>Explicit provider boundaries. Existing writes remain serialized per application schema.</summary>
public static class DatabaseExtensions
{
    public static bool IsPostgreSql(this DbConnection connection) => connection is NpgsqlConnection;
    public static string Sql(this DbConnection connection, string sqlite, string postgres) => connection.IsPostgreSql() ? postgres : sqlite;

    public static DbTransaction BeginTransaction(this DbConnection connection, bool deferred)
    {
        if (connection is SqliteConnection sqlite) return sqlite.BeginTransaction(deferred);
        var transaction = connection.BeginTransaction(deferred ? IsolationLevel.RepeatableRead : IsolationLevel.ReadCommitted);
        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            // Read snapshots cannot accidentally mutate data. Writers acquire the same lock
            // before reading invariants, across processes and pool connections. Read Committed
            // takes a fresh statement snapshot after a competing writer releases this lock.
            command.CommandText = deferred ? "SET TRANSACTION READ ONLY"
                : "SELECT pg_advisory_xact_lock(hashtext(current_database()),hashtext(current_schema()))";
            command.ExecuteNonQuery();
            return transaction;
        }
        catch { transaction.Dispose(); throw; }
    }

    public static void AddWithValue(this DbParameterCollection parameters, string name, object? value)
    {
        if (parameters is SqliteParameterCollection sqlite)
        {
            sqlite.AddWithValue(name, value ?? DBNull.Value);
            return;
        }
        var parameter = new NpgsqlParameter { ParameterName = name, Value = value is bool flag ? (flag ? 1L : 0L) : value ?? DBNull.Value };
        if (value is null or DBNull) parameter.DbType = DbType.String;
        parameters.Add(parameter);
    }

    public static int ReadInt32(this DbDataReader reader, int ordinal) => checked(Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture));
    public static long ReadInt64(this DbDataReader reader, int ordinal) => Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    public static bool ReadBoolean(this DbDataReader reader, int ordinal) => reader.GetValue(ordinal) switch
    {
        bool value => value,
        var value => Convert.ToInt64(value, CultureInfo.InvariantCulture) switch { 0 => false, 1 => true, _ => throw new InvalidOperationException("Invalid stored flag.") }
    };

    public static bool IsConstraintViolation(DbException error) => error is SqliteException { SqliteErrorCode: 19 }
        || error is PostgresException { SqlState: "23502" or "23503" or "23505" or "23514" or "23P01" };
}
