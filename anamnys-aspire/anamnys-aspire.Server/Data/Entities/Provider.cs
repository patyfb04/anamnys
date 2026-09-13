namespace Anamnys.Server.Data.Entities;

public class Provider
{
    public Guid Id { get; set; }
    public Guid ExternalSubject { get; set; }
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    // Brazil's Conselho Regional de Psicologia registration number and region. Paired by
    // a DB check constraint (Providers_Crp_ck): both null or both set. Nullable because
    // the column predates this — providers created before self-registration shipped have
    // neither. Collected as required fields at registration (see the providers realm's
    // declarative user profile config); format-only validated so far, not yet checked
    // against the real CRP registry.
    public string? CrpNumber { get; set; }
    public string? CrpRegion { get; set; }
    public string Specialty { get; set; } = "MentalHealth";
    public string PreferredNoteFormat { get; set; } = "DAP";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
