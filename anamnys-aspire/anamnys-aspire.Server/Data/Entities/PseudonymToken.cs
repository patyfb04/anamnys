namespace Anamnys.Server.Data.Entities;

// F-27 (design/specs/2026-11-17-pseudonymization-design.md §4): the number of one third party of a patient. Kind: person |
// organization | school. Fingerprint: HMAC-SHA256 of the normalised name under the patient's secret; the name is never stored.
public class PseudonymToken
{
    public Guid PatientId { get; set; }
    public string Kind { get; set; } = "";
    public byte[] Fingerprint { get; set; } = [];
    public int Number { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
