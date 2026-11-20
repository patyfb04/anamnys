namespace Anamnys.Server.Data.Entities;

// F-27: progress of one provider's backfill of legacy transcripts (design/specs/2026-11-17-pseudonymization-design.md §5, §6).
// Running while FinishedAt is null; the worker resumes such a row after a restart.
public class PseudonymizationBackfill
{
    public Guid ProviderId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public int Total { get; set; }
    public int Done { get; set; }
    public int Failed { get; set; }
}
