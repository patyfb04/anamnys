using System.Formats.Asn1;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using Anamnys.Server.Integrity;

namespace Anamnys.Tests.Integrity;

// A local RFC 3161 authority (design/specs/2026-11-03-integrity-chain-design.md §6): a generated root signs a TSA
// certificate (critical EKU id-kp-timeStamping), which signs real TimeStampTokens. Root is what the server is configured
// with. Used directly as an ITimestampAuthority, or behind HttpTimestampAuthority through Http() to drive the real request,
// response and verification path. Down makes every request fail as a network outage would.
internal sealed class FakeTimestampAuthority : ITimestampAuthority, IDisposable
{
    public const string FakeUrl = "https://tsa.test/tsr";
    private const string TstInfoOid = "1.2.840.113549.1.9.16.1.4";
    private const string SigningCertificateV2Oid = "1.2.840.113549.1.9.16.2.47";
    private const string Sha256Oid = "2.16.840.1.101.3.4.2.1";
    private const string PolicyOid = "1.3.6.1.4.1.99999.45.1";

    private readonly X509Certificate2 _signer;

    public FakeTimestampAuthority()
    {
        var notBefore = DateTimeOffset.UtcNow.AddYears(-1);
        var notAfter = DateTimeOffset.UtcNow.AddYears(10);
        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest("CN=Anamnys Test TSA Root", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));
        using var rootWithKey = rootRequest.CreateSelfSigned(notBefore, notAfter);
        Root = X509CertificateLoader.LoadCertificate(rootWithKey.RawData);
        RootPem = rootWithKey.ExportCertificatePem();

        var tsaKey = RSA.Create(2048);
        var tsaRequest = new CertificateRequest("CN=Anamnys Test TSA", tsaKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        tsaRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        tsaRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        tsaRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(TimestampTokens.TimeStampingEku)], true));
        using var tsaPublic = tsaRequest.Create(rootWithKey, notBefore.AddDays(1), notAfter.AddDays(-1), Serial());
        _signer = tsaPublic.CopyWithPrivateKey(tsaKey);
    }

    public X509Certificate2 Root { get; }
    public string RootPem { get; }
    public bool Down { get; set; }
    public int Requests { get; private set; }
    public DateTimeOffset GenTime { get; set; } = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    public string Url => FakeUrl;

    public Task<byte[]> StampAsync(string headHash, CancellationToken ct)
    {
        Requests++;
        if (Down)
        {
            throw new HttpRequestException("TSA unreachable");
        }
        return Task.FromResult(Token(TimestampTokens.Imprint(headHash), Serial()));
    }

    public DateTimeOffset? Verify(byte[] token, string headHash) => TimestampTokens.Verify(token, headHash, Root);

    // The real HTTP authority, configured with this root, talking to this fake.
    public HttpTimestampAuthority Http() =>
        new(new HttpClient(new Handler(this)), new IntegrityOptions { TsaUrl = FakeUrl, TsaRootCertificate = RootPem });

    // A TimeStampToken over this imprint: TSTInfo signed in CMS with the ESS signing-certificate-v2 attribute.
    public byte[] Token(byte[] imprint, ReadOnlyMemory<byte>? nonce)
    {
        var info = new Rfc3161TimestampTokenInfo(new Oid(PolicyOid), new Oid(Sha256Oid), imprint, Serial(), GenTime, nonce: nonce);
        var cms = new SignedCms(new ContentInfo(new Oid(TstInfoOid), info.Encode()));
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, _signer)
        {
            DigestAlgorithm = new Oid(Sha256Oid),
            IncludeOption = X509IncludeOption.EndCertOnly,
        };
        signer.SignedAttributes.Add(new AsnEncodedData(SigningCertificateV2Oid, SigningCertificateV2(_signer)));
        cms.ComputeSignature(signer);
        return cms.Encode();
    }

    // TimeStampResp ::= SEQUENCE { status PKIStatusInfo (granted), timeStampToken }.
    public byte[] Respond(byte[] requestDer)
    {
        if (!Rfc3161TimestampRequest.TryDecode(requestDer, out var request, out _))
        {
            throw new InvalidOperationException("Not a timestamp request.");
        }
        var token = Token(request.GetMessageHash().ToArray(), request.GetNonce());
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence())
            {
                writer.WriteInteger(0);
            }
            writer.WriteEncodedValue(token);
        }
        return writer.Encode();
    }

    public void Dispose()
    {
        _signer.Dispose();
        Root.Dispose();
    }

    // SigningCertificateV2 ::= SEQUENCE { certs SEQUENCE OF ESSCertIDv2 }, ESSCertIDv2 with the default SHA-256.
    private static byte[] SigningCertificateV2(X509Certificate2 certificate)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        using (writer.PushSequence())
        using (writer.PushSequence())
        {
            writer.WriteOctetString(SHA256.HashData(certificate.RawData));
        }
        return writer.Encode();
    }

    private static byte[] Serial()
    {
        var serial = RandomNumberGenerator.GetBytes(8);
        serial[0] = (byte)((serial[0] & 0x7F) | 0x01);
        return serial;
    }

    private sealed class Handler(FakeTimestampAuthority tsa) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            tsa.Requests++;
            if (tsa.Down)
            {
                throw new HttpRequestException("TSA unreachable");
            }
            var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(tsa.Respond(body)) };
            response.Content.Headers.ContentType = new("application/timestamp-reply");
            return response;
        }
    }
}
