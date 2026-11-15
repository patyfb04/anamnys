using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Anamnys.Server.RecordingConsents;

// Filled at signing: who signed, as what, and when. Relationship only for a guardian.
public sealed record ConsentSignature(string SignerName, string Role, string? Relationship, DateTimeOffset SignedAt);

// What the term names. ShowBirthDate is false on the public signing page: the signer proves who they are by typing the
// date of birth, so the page must not show it.
public sealed record ConsentTermData(
    string PatientName,
    DateOnly? PatientBirthDate,
    bool Minor,
    string ProviderName,
    string? CrpNumber,
    string? CrpRegion,
    string? PracticeCity,
    string? ProviderNote,
    ConsentSignature? Signature = null,
    bool ShowBirthDate = true);

// The fixed, versioned Anamnys term (design/specs/2026-11-15-recording-consent-design.md §3). Every mandatory clause is
// here; the provider controls only the optional "Observações do profissional" paragraph.
public static class ConsentTerm
{
    public const string CurrentVersion = "v1";
    public static readonly IReadOnlyList<string> Versions = [CurrentVersion];

    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly TimeZoneInfo Practice = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    public const string Title = "TERMO DE CONSENTIMENTO PARA GRAVAÇÃO DE ÁUDIO DAS SESSÕES";

    public static string Render(string version, ConsentTermData d) => version switch
    {
        "v1" => RenderV1(d),
        _ => throw new ArgumentOutOfRangeException(nameof(version), version, "Unknown consent term version."),
    };

    // The signed text without the date of birth, for the public revocation page (which asks for it).
    public static string MaskBirthDate(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"nascido\(a\) em \d{2}/\d{2}/\d{4}", "data de nascimento confirmada por quem assina");

    public static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static string Crp(string? number, string? region) =>
        string.IsNullOrWhiteSpace(number) ? "CRP não informado" : $"CRP {number}/{region}";

    public static string LocalDateTime(DateTimeOffset at) =>
        TimeZoneInfo.ConvertTime(at, Practice).ToString("dd/MM/yyyy 'às' HH:mm", PtBr);

    private static string RenderV1(ConsentTermData d)
    {
        var sb = new StringBuilder();
        var signature = d.Signature;
        var birth = !d.ShowBirthDate
            ? "data de nascimento confirmada por quem assina"
            : d.PatientBirthDate is { } born ? $"nascido(a) em {born.ToString("dd/MM/yyyy", PtBr)}" : "data de nascimento não informada";

        sb.AppendLine(Title);
        // The literal version of this template: a v1 text stays "v1" whatever CurrentVersion becomes.
        sb.AppendLine("Versão do termo: v1");
        sb.AppendLine();

        sb.AppendLine("1. Partes");
        sb.AppendLine(PatientLine(d.PatientName, birth));
        if (d.Minor)
        {
            sb.AppendLine(signature is { Role: ConsentRules.RoleGuardian } ? GuardianLine(signature) : GuardianPending);
        }
        sb.AppendLine($"Profissional: {d.ProviderName}, psicólogo(a), {Crp(d.CrpNumber, d.CrpRegion)}.");
        sb.AppendLine();

        Clause(sb, "2. Finalidade",
            "O áudio das sessões é gravado somente para que o(a) profissional transcreva o conteúdo e redija o registro clínico "
            + "(prontuário) da sessão. O(A) profissional revisa e assina cada registro. A gravação não é usada para nenhuma outra finalidade.");
        Clause(sb, "3. O que é gravado",
            "Somente o áudio das sessões, presenciais ou online. Não há gravação de vídeo nem de tela.");
        Clause(sb, "4. Processamento",
            "A transcrição é feita no próprio servidor do Anamnys. Nenhum serviço externo de inteligência artificial recebe o áudio ou o texto.");
        Clause(sb, "5. Destruição do áudio",
            "O áudio é destruído logo após a transcrição, inclusive quando a transcrição falha. Ele nunca é guardado em arquivos "
            + "nem em cópias de segurança (backups).");
        Clause(sb, "6. O que é guardado",
            "A transcrição, usada apenas como material de apoio e nunca exportada nem compartilhada, e o registro clínico assinado, "
            + "que é o documento oficial. Ambos são guardados pelo prazo de guarda do prontuário (no mínimo 5 anos, conforme o "
            + "Conselho Federal de Psicologia).");
        Clause(sb, "7. Acesso",
            "Somente o(a) profissional tem acesso à transcrição e ao registro clínico. O(A) paciente pode pedir acesso ao seu prontuário.");
        Clause(sb, "8. Participação voluntária",
            "O consentimento é voluntário. Recusar ou revogar não afeta o atendimento: as sessões continuam normalmente, sem gravação.");
        Clause(sb, "9. Revogação",
            "O consentimento pode ser revogado a qualquer momento, pelo link enviado com a cópia deste termo ou pedindo ao(à) "
            + "profissional. A revogação interrompe as gravações futuras imediatamente e NÃO apaga os registros clínicos já assinados.");
        Clause(sb, "10. Paciente menor de idade",
            "Quando o(a) paciente tem menos de 18 anos, o consentimento é dado pelo(a) responsável legal, e o assentimento do(a) "
            + "próprio(a) paciente é pedido e registrado pelo(a) profissional. Ao completar 18 anos, um novo consentimento é pedido ao(à) paciente.");

        sb.AppendLine("11. Data e local");
        sb.AppendLine(signature is null ? SignaturePending : SignatureLines(signature, d.PracticeCity));

        if (!string.IsNullOrWhiteSpace(d.ProviderNote))
        {
            sb.AppendLine();
            sb.AppendLine("Observações do profissional");
            sb.AppendLine(d.ProviderNote.Trim());
        }
        return sb.ToString().Replace("\r\n", "\n").TrimEnd() + "\n";
    }

    private static string PatientLine(string name, string birth) => $"Paciente: {name}, {birth}.";

    // Whether a sent text still names the patient as the record does now (name and date of birth): when the provider
    // corrected either after sending, the stored text is stale and the term is rendered again from the record.
    public static bool NamesPatient(string text, string patientName, DateOnly? birthDate) =>
        birthDate is { } born
        && text.Split('\n').Contains(PatientLine(patientName, $"nascido(a) em {born.ToString("dd/MM/yyyy", PtBr)}"));

    // The lines a sent text leaves open, filled at signing.
    public const string GuardianPending = "Responsável legal: nome e grau de parentesco informados pelo(a) responsável no momento da assinatura.";
    public const string SignaturePending = "Preenchidos no momento da assinatura.";

    private static string GuardianLine(ConsentSignature s) => $"Responsável legal: {s.SignerName} ({s.Relationship}).";

    private static string SignatureLines(ConsentSignature s, string? practiceCity)
    {
        var place = string.IsNullOrWhiteSpace(practiceCity) ? "" : $"{practiceCity.Trim()}, ";
        var role = s.Role == ConsentRules.RoleGuardian ? $"responsável legal ({s.Relationship})" : "paciente";
        return $"{place}{LocalDateTime(s.SignedAt)} (horário de Brasília).\nAssinado eletronicamente por {s.SignerName}, {role}.";
    }

    // The signed text from the text the signer was shown (the stored sent text): only the open lines are filled, so the
    // hash covers exactly what was read plus the signature. Null when the sent text does not fit the signature (no open
    // signature line, or a guardian line present for an adult signer or missing for a guardian).
    public static string? Complete(string sentText, ConsentSignature signature, string? practiceCity)
    {
        var lines = sentText.Split('\n').ToList();
        var signatureAt = lines.IndexOf(SignaturePending);
        var guardianAt = lines.IndexOf(GuardianPending);
        var guardian = signature.Role == ConsentRules.RoleGuardian;
        if (signatureAt < 0 || guardian != (guardianAt >= 0) || (guardian && guardianAt > signatureAt))
        {
            return null;
        }
        lines[signatureAt] = SignatureLines(signature, practiceCity);
        if (guardian)
        {
            lines[guardianAt] = GuardianLine(signature);
        }
        return string.Join('\n', lines);
    }

    private static void Clause(StringBuilder sb, string heading, string text)
    {
        sb.AppendLine(heading);
        sb.AppendLine(text);
        sb.AppendLine();
    }
}
