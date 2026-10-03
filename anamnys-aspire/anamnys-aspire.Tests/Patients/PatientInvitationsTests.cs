using Anamnys.Server.Email;
using Anamnys.Server.Patients;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Tests.Patients;

// PatientInvitations against the real Postgres, with a fake sender capturing every message.
// See design/specs/2026-10-03-portal-invitation-design.md §7.
[Collection(SharedAppHostCollection.Name)]
public class PatientInvitationsTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly Uri PortalBase = new("http://localhost:5274/patient");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Invite_StoresAHashedPendingInvitation_AndEmailsTheContactAddress()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var email = $"ana.{Guid.NewGuid():N}@mail.test";
        var record = await seed.AddPatientAsync("Ana", "Silva", email: email, cancellationToken: Ct);
        var sender = new FakeSender();
        await using var db = seed.CreateDbContext();

        // Act
        var outcome = await PatientInvitations.InviteAsync(db, seed.ProviderId, record, sender, PortalBase, Now, Ct);

        // Assert
        outcome.Should().Be(InviteOutcome.Sent);
        var message = sender.Messages.Should().ContainSingle().Subject;
        message.ToEmail.Should().Be(email);
        message.Text.Should().Contain("Test Provider convidou você");
        var token = TokenFrom(message);
        var stored = await db.PatientInvitations.SingleAsync(i => i.PatientId == record, Ct);
        stored.TokenHash.Should().Equal(PatientInvitationTokens.Hash(token));
        stored.Email.Should().Be(email);
        stored.ExpiresAt.Should().Be(Now.AddDays(7));
        (await db.PatientInvitations.CountAsync(i => i.PatientId == record, Ct)).Should().Be(1);
        (await PatientInvitations.StatusAsync(db, record, null, Now, Ct)).Status.Should().Be("invited");
    }

    [Fact]
    public async Task Invite_Again_RevokesThePreviousOne()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var record = await seed.AddPatientAsync("Ana", "Silva", email: $"ana.{Guid.NewGuid():N}@mail.test", cancellationToken: Ct);
        var sender = new FakeSender();
        await using var db = seed.CreateDbContext();

        // Act
        await PatientInvitations.InviteAsync(db, seed.ProviderId, record, sender, PortalBase, Now, Ct);
        await PatientInvitations.InviteAsync(db, seed.ProviderId, record, sender, PortalBase, Now.AddMinutes(5), Ct);

        // Assert
        var rows = await db.PatientInvitations.Where(i => i.PatientId == record).OrderBy(i => i.CreatedAt).ToListAsync(Ct);
        rows.Should().HaveCount(2);
        rows[0].RevokedAt.Should().NotBeNull();
        rows[1].RevokedAt.Should().BeNull();
        var accountId = await seed.AddAccountAsync(Ct, rows[0].Email);
        (await PatientInvitations.AcceptAsync(db, accountId, TokenFrom(sender.Messages[0]), Now.AddMinutes(6), Ct)).Outcome
            .Should().Be(AcceptOutcome.Invalid, "the first link was revoked by the re-send");
    }

    [Fact]
    public async Task Invite_IsRefusedForArchivedActiveForeignOrUnconfigured()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var archived = await seed.AddPatientAsync("A", "Arquivada", email: "a@mail.test", archivedAt: Now, cancellationToken: Ct);
        var active = await seed.AddPatientAsync("B", "Ativa", email: "b@mail.test", cancellationToken: Ct);
        await seed.BindPortalAccountAsync(active, Ct);
        var other = await seed.AddProviderAsync(Ct);
        var foreign = await seed.AddPatientAsync("C", "Alheia", email: "c@mail.test", providerId: other, cancellationToken: Ct);
        var plain = await seed.AddPatientAsync("D", "Normal", email: "d@mail.test", cancellationToken: Ct);
        var sender = new FakeSender();
        await using var db = seed.CreateDbContext();

        // Act + Assert
        (await PatientInvitations.InviteAsync(db, seed.ProviderId, archived, sender, PortalBase, Now, Ct)).Should().Be(InviteOutcome.Archived);
        (await PatientInvitations.InviteAsync(db, seed.ProviderId, active, sender, PortalBase, Now, Ct)).Should().Be(InviteOutcome.AlreadyActive);
        (await PatientInvitations.InviteAsync(db, seed.ProviderId, foreign, sender, PortalBase, Now, Ct)).Should().Be(InviteOutcome.NotFound);
        (await PatientInvitations.InviteAsync(db, seed.ProviderId, plain, null, PortalBase, Now, Ct)).Should().Be(InviteOutcome.EmailNotConfigured);
        sender.Messages.Should().BeEmpty();
        (await db.PatientInvitations.CountAsync(i => i.PatientId == plain, Ct)).Should().Be(0);
    }

    [Fact]
    public async Task Invite_WhenTheSendFails_LeavesNothingPending()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var record = await seed.AddPatientAsync("Ana", "Silva", email: "ana@mail.test", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var outcome = await PatientInvitations.InviteAsync(db, seed.ProviderId, record, new FakeSender(fail: true), PortalBase, Now, Ct);

        // Assert
        outcome.Should().Be(InviteOutcome.SendFailed);
        (await db.PatientInvitations.SingleAsync(i => i.PatientId == record, Ct)).RevokedAt.Should().NotBeNull();
        (await PatientInvitations.StatusAsync(db, record, null, Now, Ct)).Status.Should().Be("none");
    }

    [Fact]
    public async Task Cancel_And_RemoveAccess()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var invited = await seed.AddPatientAsync("Ana", "Silva", email: "ana@mail.test", cancellationToken: Ct);
        var linked = await seed.AddPatientAsync("Bia", "Souza", email: "bia@mail.test", cancellationToken: Ct);
        var account = await seed.BindPortalAccountAsync(linked, Ct);
        await using var db = seed.CreateDbContext();
        await PatientInvitations.InviteAsync(db, seed.ProviderId, invited, new FakeSender(), PortalBase, Now, Ct);

        // Act
        var cancelled = await PatientInvitations.CancelAsync(db, seed.ProviderId, invited, Now, Ct);
        var removed = await PatientInvitations.RemoveAccessAsync(db, seed.ProviderId, linked, Ct);

        // Assert
        cancelled.Should().BeTrue();
        removed.Should().BeTrue();
        (await PatientInvitations.StatusAsync(db, invited, null, Now, Ct)).Status.Should().Be("none");
        (await db.Patients.SingleAsync(p => p.Id == linked, Ct)).AccountId.Should().BeNull();
        (await db.PatientAccounts.AnyAsync(a => a.Id == account, Ct)).Should().BeTrue("removing access keeps the account");
    }

    [Fact]
    public async Task Accept_WithTheSameEmail_LinksTheRecord()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var email = $"ana.{Guid.NewGuid():N}@mail.test";
        var record = await seed.AddPatientAsync("Ana", "Silva", email: email, cancellationToken: Ct);
        var sender = new FakeSender();
        await using var db = seed.CreateDbContext();
        await PatientInvitations.InviteAsync(db, seed.ProviderId, record, sender, PortalBase, Now, Ct);
        var account = await seed.AddAccountAsync(Ct, email.ToUpperInvariant());

        // Act
        var result = await PatientInvitations.AcceptAsync(db, account, TokenFrom(sender.Messages[0]), Now.AddDays(1), Ct);

        // Assert
        result.Outcome.Should().Be(AcceptOutcome.Accepted);
        result.ProviderName.Should().Be("Test Provider");
        await using var check = seed.CreateDbContext();
        (await check.Patients.SingleAsync(p => p.Id == record, Ct)).AccountId.Should().Be(account);
        var invitation = await check.PatientInvitations.SingleAsync(i => i.PatientId == record, Ct);
        invitation.AcceptedAt.Should().Be(Now.AddDays(1));
        invitation.AcceptedAccountId.Should().Be(account);
        (await PatientInvitations.StatusAsync(check, record, account, Now, Ct)).Status.Should().Be("active");
    }

    [Fact]
    public async Task Accept_IsRefusedForMismatchExpiredUsedOrAlreadyLinked()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var email = $"ana.{Guid.NewGuid():N}@mail.test";
        var record = await seed.AddPatientAsync("Ana", "Silva", email: email, cancellationToken: Ct);
        var sender = new FakeSender();
        await using var db = seed.CreateDbContext();
        await PatientInvitations.InviteAsync(db, seed.ProviderId, record, sender, PortalBase, Now, Ct);
        var token = TokenFrom(sender.Messages[0]);
        var stranger = await seed.AddAccountAsync(Ct);
        var owner = await seed.AddAccountAsync(Ct, email);

        // Act + Assert
        (await PatientInvitations.AcceptAsync(db, owner, "not-a-token", Now, Ct)).Outcome.Should().Be(AcceptOutcome.Invalid);
        (await PatientInvitations.AcceptAsync(db, stranger, token, Now, Ct)).Outcome.Should().Be(AcceptOutcome.EmailMismatch);
        (await PatientInvitations.AcceptAsync(db, owner, token, Now.AddDays(8), Ct)).Outcome.Should().Be(AcceptOutcome.Invalid);
        (await PatientInvitations.AcceptAsync(db, owner, token, Now, Ct)).Outcome.Should().Be(AcceptOutcome.Accepted);
        (await PatientInvitations.AcceptAsync(db, stranger, token, Now, Ct)).Outcome.Should().Be(AcceptOutcome.Invalid, "a token is single use");
    }

    [Fact]
    public async Task Accept_Again_ByTheAccountThatAcceptedIt_StillSucceeds()
    {
        // Arrange — a reload of the acceptance page re-submits the same token.
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var email = $"ana.{Guid.NewGuid():N}@mail.test";
        var record = await seed.AddPatientAsync("Ana", "Silva", email: email, cancellationToken: Ct);
        var sender = new FakeSender();
        await using var db = seed.CreateDbContext();
        await PatientInvitations.InviteAsync(db, seed.ProviderId, record, sender, PortalBase, Now, Ct);
        var token = TokenFrom(sender.Messages[0]);
        var owner = await seed.AddAccountAsync(Ct, email);
        await PatientInvitations.AcceptAsync(db, owner, token, Now, Ct);

        // Act
        var again = await PatientInvitations.AcceptAsync(db, owner, token, Now.AddMinutes(1), Ct);

        // Assert
        again.Outcome.Should().Be(AcceptOutcome.Accepted);
        again.ProviderName.Should().Be("Test Provider");
    }

    [Fact]
    public async Task Accept_WhenTheAccountAlreadyHasARecordOfThatProvider_IsAlreadyLinked()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var email = $"ana.{Guid.NewGuid():N}@mail.test";
        var account = await seed.AddAccountAsync(Ct, email);
        var existing = await seed.AddPatientAsync("Ana", "Antiga", email: email, cancellationToken: Ct);
        await seed.LinkAccountAsync(existing, account, Ct);
        var duplicate = await seed.AddPatientAsync("Ana", "Duplicada", email: email, cancellationToken: Ct);
        var sender = new FakeSender();
        await using var db = seed.CreateDbContext();
        await PatientInvitations.InviteAsync(db, seed.ProviderId, duplicate, sender, PortalBase, Now, Ct);

        // Act
        var result = await PatientInvitations.AcceptAsync(db, account, TokenFrom(sender.Messages[0]), Now, Ct);

        // Assert
        result.Outcome.Should().Be(AcceptOutcome.AlreadyLinked);
        await using var check = seed.CreateDbContext();
        (await check.Patients.SingleAsync(p => p.Id == duplicate, Ct)).AccountId.Should().BeNull();
    }

    [Fact]
    public async Task Archiving_RevokesThePendingInvitation()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var email = $"ana.{Guid.NewGuid():N}@mail.test";
        var record = await seed.AddPatientAsync("Ana", "Silva", email: email, cancellationToken: Ct);
        var sender = new FakeSender();
        await using var db = seed.CreateDbContext();
        await PatientInvitations.InviteAsync(db, seed.ProviderId, record, sender, PortalBase, Now, Ct);
        var account = await seed.AddAccountAsync(Ct, email);

        // Act
        await PatientRecords.SetArchivedAsync(db, seed.ProviderId, record, true, Now, Ct);
        var result = await PatientInvitations.AcceptAsync(db, account, TokenFrom(sender.Messages[0]), Now, Ct);

        // Assert
        result.Outcome.Should().Be(AcceptOutcome.Invalid);
    }

    private static string TokenFrom(EmailMessage message)
    {
        var marker = "/convite/";
        var start = message.Text.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        return new string(message.Text[start..].TakeWhile(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
    }

    private sealed class FakeSender(bool fail = false) : IEmailSender
    {
        public List<EmailMessage> Messages { get; } = [];

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            if (fail)
            {
                throw new EmailSendException("rejected");
            }
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }
}
