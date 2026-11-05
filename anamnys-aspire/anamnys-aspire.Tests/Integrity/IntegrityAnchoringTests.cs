using Anamnys.Server.Data.Entities;
using Anamnys.Server.Integrity;
using Anamnys.Tests.Retention;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using QuestPDF.Infrastructure;
using static Anamnys.Tests.Integrity.IntegrityTestData;

namespace Anamnys.Tests.Integrity;

// Timestamping the chain head with an RFC 3161 authority (design/specs/2026-11-03-integrity-chain-design.md §6, §10).
[Collection(SharedAppHostCollection.Name)]
public class IntegrityAnchoringTests(SharedAppHostFixture fixture)
{
    private static readonly string HeadHash = new('a', 64);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    static IntegrityAnchoringTests() => QuestPDF.Settings.License = LicenseType.Community;

    [Fact]
    public async Task StampAsync_OverHttp_ReturnsATokenThatVerifiesForThatHeadOnly()
    {
        // Arrange
        using var fake = new FakeTimestampAuthority();
        var http = fake.Http();

        // Act
        var token = await http.StampAsync(HeadHash, Ct);

        // Assert
        http.Verify(token, HeadHash).Should().Be(fake.GenTime);
        http.Verify(token, new string('b', 64)).Should().BeNull();
        fake.Requests.Should().Be(1);
    }

    [Fact]
    public void Verify_TamperedToken_Fails()
    {
        // Arrange
        using var fake = new FakeTimestampAuthority();
        var token = fake.Token(TimestampTokens.Imprint(HeadHash), null);
        var tampered = (byte[])token.Clone();
        tampered[^10] ^= 0x01;
        var truncated = token[..^1];

        // Act / Assert
        fake.Verify(token, HeadHash).Should().NotBeNull();
        fake.Verify(tampered, HeadHash).Should().BeNull();
        fake.Verify(truncated, HeadHash).Should().BeNull();
        fake.Verify([], HeadHash).Should().BeNull();
    }

    [Fact]
    public void Verify_TokenFromAnotherRoot_Fails()
    {
        // Arrange
        using var configured = new FakeTimestampAuthority();
        using var other = new FakeTimestampAuthority();
        var token = other.Token(TimestampTokens.Imprint(HeadHash), null);

        // Act / Assert
        other.Verify(token, HeadHash).Should().NotBeNull();
        configured.Verify(token, HeadHash).Should().BeNull();
        TimestampTokens.Verify(token, HeadHash, null).Should().BeNull();
    }

    [Fact]
    public async Task StampAsync_AuthorityAnsweringWithAnotherRoot_Throws()
    {
        // Arrange: the authority signs with its own root, the server trusts another.
        using var authority = new FakeTimestampAuthority();
        using var trusted = new FakeTimestampAuthority();
        var http = new HttpTimestampAuthority(
            new HttpClient(new RelayHandler(authority)),
            new IntegrityOptions { TsaUrl = FakeTimestampAuthority.FakeUrl, TsaRootCertificate = trusted.RootPem });

        // Act
        var stamp = async () => await http.StampAsync(HeadHash, Ct);

        // Assert
        await stamp.Should().ThrowAsync<System.Security.Cryptography.CryptographicException>();
    }

    [Fact]
    public async Task AnchorAsync_StoresAVerifiedTokenForTheHead_AndTheChainReportsIt()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct, withCrp: true);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        await SignedNoteAsync(s, provider, patient, Now, Ct);
        var note = await SignedNoteAsync(s, provider, patient, Now.AddMinutes(1), Ct);
        using var fake = new FakeTimestampAuthority();
        var http = fake.Http();
        await using var db = s.CreateDbContext();

        // Act
        var anchor = await IntegrityAnchoring.AnchorAsync(db, provider, http, Ct);
        var again = await IntegrityAnchoring.AnchorAsync(db, provider, http, Ct);
        var chain = await IntegrityVerifier.VerifyChainAsync(db, provider, http, Now, Ct);
        var subject = await IntegrityVerifier.VerifySubjectAsync(db, provider, IntegritySeal.Note, note, http, Ct);

        // Assert
        var head = (await SealsAsync(s, provider, Ct))[^1];
        anchor.Should().NotBeNull();
        again.Should().BeNull("nothing new was sealed since");
        var stored = await db.IntegrityAnchors.AsNoTracking().SingleAsync(a => a.ProviderId == provider, Ct);
        stored.Sequence.Should().Be(2);
        stored.HeadHash.Should().Be(head.Hash);
        stored.TsaUrl.Should().Be(FakeTimestampAuthority.FakeUrl);
        stored.AnchoredAt.Should().Be(fake.GenTime);
        http.Verify(stored.Token, stored.HeadHash).Should().Be(fake.GenTime);
        chain.FirstProblem.Should().BeNull();
        chain.LastAnchor.Should().Be(new AnchorInfo(2, fake.GenTime, FakeTimestampAuthority.FakeUrl));
        subject!.Anchored.Should().BeTrue();
        (await IntegrityAnchoring.PendingProvidersAsync(db, Ct, provider)).Should().BeEmpty();
    }

    [Fact]
    public async Task VerifyChainAsync_StoredTokenTamperedOrForAnotherHead_IsAnInvalidAnchor()
    {
        // Arrange: two anchors inserted directly, a tampered token at 1 and a valid token whose head is not the seal's at 2.
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct, withCrp: true);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        await SignedNoteAsync(s, provider, patient, Now, Ct);
        await SignedNoteAsync(s, provider, patient, Now.AddMinutes(1), Ct);
        var seals = await SealsAsync(s, provider, Ct);
        using var fake = new FakeTimestampAuthority();
        var tampered = fake.Token(TimestampTokens.Imprint(seals[0].Hash), null);
        tampered[^10] ^= 0x01;
        var otherHead = new string('e', 64);
        await InsertAnchorAsync(s, provider, 1, seals[0].Hash, tampered);
        await InsertAnchorAsync(s, provider, 2, otherHead, fake.Token(TimestampTokens.Imprint(otherHead), null));
        await using var db = s.CreateDbContext();

        // Act
        var chain = await IntegrityVerifier.VerifyChainAsync(db, provider, fake, Now, Ct);
        var subject = await IntegrityVerifier.VerifySubjectAsync(db, provider, IntegritySeal.Note, seals[1].SubjectId, fake, Ct);

        // Assert
        chain.FirstProblem.Should().Be(new ChainProblem(1, null, ChainProblemKind.AnchorInvalid));
        chain.LastAnchor.Should().BeNull();
        subject!.Anchored.Should().BeFalse();
    }

    [Fact]
    public async Task VerifySubjectAsync_ForgedAnchorRowAtItsSequence_IsNotAnchored_AValidOneIs()
    {
        // Arrange: a row with the right head hash but a garbage token, at the subject's own sequence.
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct, withCrp: true);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        var forgedNote = await SignedNoteAsync(s, provider, patient, Now, Ct);
        var validNote = await SignedNoteAsync(s, provider, patient, Now.AddMinutes(1), Ct);
        var seals = await SealsAsync(s, provider, Ct);
        using var fake = new FakeTimestampAuthority();
        await InsertAnchorAsync(s, provider, 1, seals[0].Hash, [0x30, 0x03, 0x02, 0x01, 0x00]);
        await using var db = s.CreateDbContext();

        // Act
        var forged = await IntegrityVerifier.VerifySubjectAsync(db, provider, IntegritySeal.Note, forgedNote, fake, Ct);
        await InsertAnchorAsync(s, provider, 2, seals[1].Hash, fake.Token(TimestampTokens.Imprint(seals[1].Hash), null));
        var coveredLater = await IntegrityVerifier.VerifySubjectAsync(db, provider, IntegritySeal.Note, forgedNote, fake, Ct);
        var valid = await IntegrityVerifier.VerifySubjectAsync(db, provider, IntegritySeal.Note, validNote, fake, Ct);

        // Assert
        forged!.Anchored.Should().BeFalse();
        coveredLater!.Anchored.Should().BeTrue("a valid anchor at a later sequence covers the earlier seal");
        valid!.Anchored.Should().BeTrue();
    }

    [Fact]
    public async Task TsaDown_SigningIsUnaffected_AndTheNextRunAnchors()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct, withCrp: true);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        using var fake = new FakeTimestampAuthority { Down = true };
        var http = fake.Http();
        await using var db = s.CreateDbContext();

        // Act: sign, the night's run cannot reach the authority, sign again, the next night it is back.
        await SignedNoteAsync(s, provider, patient, Now, Ct);
        var down = await IntegrityJobSteps.RunAsync(db, http, true, NullLogger.Instance, Now, Ct, provider);
        var signedWhileDown = await SignedNoteAsync(s, provider, patient, Now.AddHours(1), Ct);
        fake.Down = false;
        var up = await IntegrityJobSteps.RunAsync(db, http, true, NullLogger.Instance, Now.AddDays(1), Ct, provider);

        // Assert
        down.AnchorFailures.Should().Be(1);
        down.Anchored.Should().Be(0);
        down.Problems.Should().Be(0);
        (await IntegrityVerifier.VerifySubjectAsync(db, provider, IntegritySeal.Note, signedWhileDown, http, Ct))!.Status.Should().Be(SubjectStatus.Intact);
        up.Anchored.Should().Be(1);
        up.AnchorFailures.Should().Be(0);
        (await db.IntegrityAnchors.AsNoTracking().SingleAsync(a => a.ProviderId == provider, Ct)).Sequence.Should().Be(2);
        fake.Requests.Should().Be(2);
    }

    private static Task InsertAnchorAsync(RetentionScratch s, Guid provider, long sequence, string headHash, byte[] token) =>
        s.ExecuteAsync(
            """
            INSERT INTO "IntegrityAnchors" ("ProviderId","Sequence","HeadHash","TsaUrl","Token","AnchoredAt")
            VALUES (@p, @seq, @head, 'https://tsa.test/tsr', @token, now())
            """, Ct, ("p", provider), ("seq", sequence), ("head", headHash), ("token", token));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StampAsync_ResponseOverTheCap_IsRefusedBeforeParsing(bool withContentLength)
    {
        // Arrange: a valid response padded past 64 KiB, with or without a Content-Length.
        using var fake = new FakeTimestampAuthority();
        var oversized = new byte[HttpTimestampAuthority.MaxResponseBytes + 1];
        var http = new HttpTimestampAuthority(
            new HttpClient(new FixedHandler(oversized, withContentLength)),
            new IntegrityOptions { TsaUrl = FakeTimestampAuthority.FakeUrl, TsaRootCertificate = fake.RootPem });

        // Act
        var stamp = async () => await http.StampAsync(HeadHash, Ct);

        // Assert
        (await stamp.Should().ThrowAsync<System.Security.Cryptography.CryptographicException>())
            .WithMessage("*too large*");
    }

    private sealed class FixedHandler(byte[] body, bool withContentLength) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpContent content = withContentLength ? new ByteArrayContent(body) : new StreamContent(new NonSeekableStream(body));
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content });
        }
    }

    // No length to compute, so no Content-Length header: the cap must hold while reading.
    private sealed class NonSeekableStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class RelayHandler(FakeTimestampAuthority tsa) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(tsa.Respond(body)) };
        }
    }
}
