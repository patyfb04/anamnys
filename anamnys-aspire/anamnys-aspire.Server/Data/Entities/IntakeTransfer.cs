namespace Anamnys.Server.Data.Entities;

// An answer with a record destination, created when the intake's note is signed and written to the record only when
// the provider confirms it (design/specs/2026-11-12-structured-intake-design.md §3). ResultId is the row written (the
// patient for registration fields); ExcerptRefId points at the note section holding the answer.
public class IntakeTransfer
{
    public Guid Id { get; set; }
    public Guid IntakeId { get; set; }
    public Guid QuestionId { get; set; }
    public string TargetField { get; set; } = "";
    public string Value { get; set; } = "";
    public string Status { get; set; } = IntakeTransferStatus.Pending;
    public DateTimeOffset? DecidedAt { get; set; }
    public Guid? ResultId { get; set; }
    public Guid? ExcerptRefId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public static class IntakeTransferStatus
{
    public const string Pending = "pending";
    public const string Confirmed = "confirmed";
    public const string Ignored = "ignored";
}
