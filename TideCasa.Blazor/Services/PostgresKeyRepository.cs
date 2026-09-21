using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Npgsql;

namespace TideCasa.Blazor.Services;

/// <summary>Durable key-ring storage, encrypted with a separately held deployment secret.</summary>
public sealed class PostgresKeyRepository : IXmlRepository, IDisposable
{
    private readonly NpgsqlDataSource source;
    private readonly byte[] encryptionKey;
    private readonly string schema;

    public PostgresKeyRepository(IConfiguration configuration, IHostEnvironment environment)
    {
        schema = configuration["Storage:PostgresSchema"] ?? "tide_casa";
        if (!Regex.IsMatch(schema, "^tide_[a-z0-9_]{1,50}$", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("Key storage requires an isolated tide_ application schema.");
        try { encryptionKey = Convert.FromBase64String(configuration["DataProtection:EncryptionKey"] ?? ""); }
        catch { throw new InvalidOperationException("Durable key storage requires a valid private encryption key."); }
        if (encryptionKey.Length != 32) throw new InvalidOperationException("Durable key storage requires a 32-byte private encryption key.");
        NpgsqlConnectionStringBuilder settings;
        try { settings = new(configuration.GetConnectionString("Application") ?? ""); }
        catch { throw new InvalidOperationException("The private key-storage connection settings are invalid."); }
        if (string.IsNullOrWhiteSpace(settings.Host) || !environment.IsDevelopment() && settings.SslMode != SslMode.VerifyFull)
            throw new InvalidOperationException("Production key storage requires a configured PostgreSQL host and SSL Mode=VerifyFull.");
        settings.SearchPath = schema;
        settings.MaxPoolSize = 2;
        settings.MinPoolSize = 0;
        settings.Timeout = 10;
        settings.CommandTimeout = 20;
        settings.IncludeErrorDetail = false;
        settings.LogParameters = false;
        settings.ApplicationName = "TideCasa.Blazor.Keys";
        source = NpgsqlDataSource.Create(settings.ConnectionString);
    }

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var connection = source.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,friendly_name,ciphertext FROM tide_data_protection_keys ORDER BY created_at,id LIMIT 1001";
        using var reader = command.ExecuteReader();
        var elements = new List<XElement>();
        while (reader.Read())
        {
            if (elements.Count >= 1000) throw new InvalidOperationException("The key ring requires operator review.");
            var id = reader.GetString(0); var name = reader.GetString(1); var encoded = reader.GetString(2);
            if (!encoded.StartsWith("v1.", StringComparison.Ordinal) || encoded.Length > 350000)
                throw new InvalidOperationException("A stored data-protection key has an invalid envelope.");
            var envelope = Convert.FromBase64String(encoded[3..]);
            if (envelope.Length < 29) throw new InvalidOperationException("A stored data-protection key is incomplete.");
            var plain = new byte[envelope.Length - 28];
            try
            {
                using var cipher = new AesGcm(encryptionKey, 16);
                cipher.Decrypt(envelope.AsSpan(0, 12), envelope.AsSpan(28), envelope.AsSpan(12, 16), plain, AssociatedData(id, name));
                using var buffer = new MemoryStream(plain);
                using var xml = XmlReader.Create(buffer, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 262144 });
                elements.Add(XElement.Load(xml));
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        return elements.AsReadOnly();
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        var plain = Encoding.UTF8.GetBytes(element.ToString(SaveOptions.DisableFormatting));
        try
        {
            if (plain.Length > 262144 || friendlyName.Length > 200) throw new InvalidOperationException("The key-ring record exceeds its storage limit.");
            var id = Guid.NewGuid().ToString("D");
            var envelope = new byte[plain.Length + 28];
            RandomNumberGenerator.Fill(envelope.AsSpan(0, 12));
            using (var cipher = new AesGcm(encryptionKey, 16))
                cipher.Encrypt(envelope.AsSpan(0, 12), plain, envelope.AsSpan(28), envelope.AsSpan(12, 16), AssociatedData(id, friendlyName));
            using var connection = source.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO tide_data_protection_keys(id,friendly_name,ciphertext,created_at) VALUES(@id,@name,@cipher,@now)";
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("name", friendlyName);
            command.Parameters.AddWithValue("cipher", "v1." + Convert.ToBase64String(envelope));
            command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private byte[] AssociatedData(string id, string name) => Encoding.UTF8.GetBytes("TideCasa.Blazor|" + schema + "|" + id + "|" + name);
    public void Dispose() { source.Dispose(); CryptographicOperations.ZeroMemory(encryptionKey); }
}
