namespace Anamnys.Server.Data.Entities;

// Guard years per provider and record kind (RetentionRules_GuardYears_ck: >= 5). The retention clock uses the
// 'prontuario' row; a provider without one gets 5 years under Res. CFP 001/2009
// (design/specs/2026-10-28-retention-panel-design.md §2).
public class RetentionRule
{
    public const string Prontuario = "prontuario";
    public const int DefaultGuardYears = 5;
    public const string DefaultBasis = "Res. CFP 001/2009";

    public Guid Id { get; set; }
    public Guid ProviderId { get; set; }
    public string RecordKind { get; set; } = Prontuario;
    public int GuardYears { get; set; } = DefaultGuardYears;
    public string Basis { get; set; } = DefaultBasis;
}
