namespace Anamnys.Server.Backup;

// design/specs/2026-11-05-backup-restore-design.md §2.2, §2.4. Backup is off by default: with Enabled false (Development
// and the tests) the monitor does nothing. AlertEmail is the administrator who receives the alerts.
public sealed class BackupOptions
{
    public const string Section = "Backup";

    public bool Enabled { get; set; }
    public string AlertEmail { get; set; } = "";
}
