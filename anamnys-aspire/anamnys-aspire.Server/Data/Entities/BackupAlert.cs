namespace Anamnys.Server.Data.Entities;

// One problem episode of one backup check (design/specs/2026-11-05-backup-restore-design.md §2.4, §3). At most one open
// alert per check (partial unique index); NotifiedAt is set once the e-mail went out, so one e-mail per episode.
public class BackupAlert
{
    public Guid Id { get; set; }
    public string Check { get; set; } = "";
    public DateTimeOffset OpenedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public DateTimeOffset? NotifiedAt { get; set; }
}
