namespace Anamnys.Server.Data.Entities;

// One run of the backup agent (design/specs/2026-11-05-backup-restore-design.md §2.3, §3). Kind: full | files | retention.
// Outcome: ok | failed. Written by the agent through its own database role; the server only reads it. No clinical data,
// no file names: ErrorCode is a short code, BackupName the WAL-G backup name.
public class BackupRun
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public string Outcome { get; set; } = "";
    public long? Bytes { get; set; }
    public string? ErrorCode { get; set; }
    public string? BackupName { get; set; }
}
