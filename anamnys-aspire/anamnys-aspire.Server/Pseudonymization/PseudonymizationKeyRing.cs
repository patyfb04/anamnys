using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Npgsql;

namespace Anamnys.Server.Pseudonymization;

// The pseudonymisation secrets' own Data Protection key ring (design/specs/2026-11-17-pseudonymization-design.md §4;
// ruling of 2026-11-18): the protected secrets are durable Postgres data, so their keys live in Postgres too
// ("PseudonymizationKeys") instead of the auth ring in Redis, which does not survive a cache restart. A database backup
// therefore carries the keys with the secrets; outside Development each key is encrypted at rest with the same
// certificate as the auth ring (DataProtection:CertificateThumbprint). The auth ring is not touched.
public static class PseudonymizationKeyRing
{
    public const string ApplicationName = "anamnys-pseudonymization";

    // certificate: protects new keys (and decrypts them). previousCertificates: certificates this ring was protected with
    // before a rotation; they only decrypt older keys, so a rotation never makes a patient secret unreadable.
    // In Development without a certificate, Data Protection's default XML encryptor stays in place (DPAPI, bound to the
    // Windows user, on Windows): a dev DB copied to another machine or user cannot read its secrets.
    public static IDataProtectionProvider Create(
        IXmlRepository repository, X509Certificate2? certificate, ILoggerFactory? loggers = null,
        IReadOnlyCollection<X509Certificate2>? previousCertificates = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (loggers is not null)
        {
            services.AddSingleton(loggers);
        }
        var builder = services.AddDataProtection().SetApplicationName(ApplicationName);
        if (certificate is not null)
        {
            builder.ProtectKeysWithCertificate(certificate);
        }
        var decryption = (previousCertificates ?? []).Prepend(certificate).OfType<X509Certificate2>().ToArray();
        if (decryption.Length > 0)
        {
            builder.UnprotectKeysWithAnyCertificate(decryption);
        }
        services.Configure<KeyManagementOptions>(o => o.XmlRepository = repository);
        return services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();
    }

    // The certificate of DataProtection:CertificateThumbprint (CurrentUser/My), or null when none is configured.
    public static X509Certificate2? Certificate(IConfiguration configuration)
    {
        var thumbprint = configuration["DataProtection:CertificateThumbprint"];
        return string.IsNullOrEmpty(thumbprint) ? null : Find(thumbprint, "DataProtection:CertificateThumbprint");
    }

    // The certificates of DataProtection:PreviousCertificateThumbprints (CurrentUser/My): earlier certificates of the
    // pseudonymisation ring, kept after a rotation to decrypt its older keys. The main (auth) ring does not read this.
    public static IReadOnlyList<X509Certificate2> PreviousCertificates(IConfiguration configuration) =>
        [.. PreviousThumbprints(configuration).Select(t => Find(t, "DataProtection:PreviousCertificateThumbprints"))];

    // A JSON array ("PreviousCertificateThumbprints": ["…", "…"]) or one string separated by commas or semicolons
    // (an environment variable).
    internal static IReadOnlyList<string> PreviousThumbprints(IConfiguration configuration)
    {
        var section = configuration.GetSection("DataProtection:PreviousCertificateThumbprints");
        return [.. section.GetChildren().Select(c => c.Value).Append(section.Value)
            .SelectMany(v => (v ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static X509Certificate2 Find(string thumbprint, string setting)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates
            .Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false)
            .OfType<X509Certificate2>()
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"{setting} '{thumbprint}' was configured but no matching certificate was found in the CurrentUser/My store.");
    }
}

// Data Protection's key XML in "PseudonymizationKeys", one row per element (keys and revocations), by friendly name.
public sealed class PostgresXmlRepository(string connectionString) : IXmlRepository
{
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = new NpgsqlCommand("""SELECT "Xml" FROM "PseudonymizationKeys" ORDER BY "CreatedAt", "FriendlyName" """, connection);
        using var reader = command.ExecuteReader();
        var elements = new List<XElement>();
        while (reader.Read())
        {
            elements.Add(XElement.Parse(reader.GetString(0)));
        }
        return elements;
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = new NpgsqlCommand(
            """
            INSERT INTO "PseudonymizationKeys" ("FriendlyName","Xml") VALUES (@name, @xml)
            ON CONFLICT ("FriendlyName") DO UPDATE SET "Xml" = EXCLUDED."Xml"
            """, connection);
        command.Parameters.AddWithValue("name", friendlyName);
        command.Parameters.AddWithValue("xml", element.ToString(SaveOptions.DisableFormatting));
        command.ExecuteNonQuery();
    }
}
