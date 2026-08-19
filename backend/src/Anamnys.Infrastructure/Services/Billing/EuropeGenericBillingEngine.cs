using Anamnys.Domain.Entities;
using Anamnys.Domain.Enums;

namespace Anamnys.Infrastructure.Services.Billing;

/// <summary>
/// Europe (generic) billing engine — SNOMED CT procedure concepts + ICD-10 diagnoses.
///
/// European countries each have their own national procedure coding system:
///   Germany  — EBM (Einheitlicher Bewertungsmaßstab) for outpatient statutory care
///              + GOÄ (Gebührenordnung für Ärzte) for private billing
///   France   — CCAM (Classification Commune des Actes Médicaux)
///   UK       — OPCS-4 procedure codes + NHS Payment by Results tariffs
///   Spain    — CIE-10-ES (procedure and diagnosis classifications)
///   Italy    — ICD-9-CM-IT (gradually migrating to ICD-10)
///   Netherlands — DBC/DOT groupers
///
/// This engine uses SNOMED CT procedure concept identifiers as a common denominator.
/// SNOMED CT is the recommended international clinical terminology and is used
/// as a reference standard across most European health systems.
///
/// Diagnoses use ICD-10 (national modifications vary; codes are largely identical
/// for mental health and musculoskeletal conditions across EU member states).
///
/// Key considerations for European practitioners:
/// - GDPR (EU) / UK GDPR applies to all PHI — stricter than HIPAA in some areas
/// - Data residency: server must be hosted within the EEA for most jurisdictions
/// - NHS (UK): referral pathway required before most specialist services
/// - German statutory (GKV) vs private (PKV) insurers have different fee schedules
/// </summary>
internal class EuropeGenericBillingEngine : IBillingEngineStrategy
{
    public BillingSystem BillingSystem => BillingSystem.EuropeGeneric;

    public List<BillingCode> DeriveCodes(StructuredNote note, Specialty specialty)
        => specialty == Specialty.MentalHealth
            ? DerivePsychotherapyCodes(note)
            : DerivePhysiotherapyCodes(note);

    private static List<BillingCode> DerivePsychotherapyCodes(StructuredNote note)
    {
        var content = string.Join(" ", note.Sections.Values).ToLowerInvariant();
        var missing = new List<string>();

        // Identify therapeutic modality for SNOMED code selection
        string snomedCode;
        string description;

        if (content.Contains("cbt") || content.Contains("cognitive behavioral") || content.Contains("cognitive behavioural"))
        {
            snomedCode = "SNOMED-229070002"; description = "Cognitive behavioural therapy session";
        }
        else if (content.Contains("emdr"))
        {
            snomedCode = "SNOMED-444875005"; description = "Eye movement desensitization and reprocessing therapy";
        }
        else if (content.Contains("psychodynamic") || content.Contains("psychoanalytic"))
        {
            snomedCode = "SNOMED-229075007"; description = "Psychodynamic psychotherapy session";
        }
        else if (content.Contains("group") || content.Contains("groupe") || content.Contains("gruppe"))
        {
            snomedCode = "SNOMED-76168009";  description = "Group psychotherapy session";
        }
        else
        {
            snomedCode = "SNOMED-229070000"; description = "Individual psychotherapy session";
        }

        // Common European documentation requirements
        if (!content.Contains("diagnos") && !content.Contains("icd"))
            missing.Add("ICD-10 diagnosis required for reimbursement across all EU systems");
        if (!content.Contains("plan") && !content.Contains("treatment") && !content.Contains("goal"))
            missing.Add("Documented treatment plan with measurable goals required");
        if (!content.Contains("consent"))
            missing.Add("GDPR-compliant informed consent to data processing should be noted");

        // Duration matters in several European systems (e.g. German EBM has time thresholds)
        if (!content.Contains("min") && !content.Contains("hour") && !content.Contains("duration"))
            missing.Add("Session duration should be documented (required for time-based fee scales in DE, FR, UK)");

        return
        [
            new BillingCode
            {
                CptCode      = snomedCode,
                Icd10Code    = "F33.1",    // ICD-10: Recurrent depressive disorder, current episode moderate
                Description  = description,
                ConfidenceScore   = missing.Count == 0 ? 0.88 : 0.65,
                DenialRiskScore   = missing.Count switch { 0 => 14, 1 => 38, _ => 65 },
                Modifiers    = DetectCountryModifiers(content),
                MissingDocumentation = missing,
            }
        ];
    }

    private static List<BillingCode> DerivePhysiotherapyCodes(StructuredNote note)
    {
        var content = string.Join(" ", note.Sections.Values).ToLowerInvariant();
        var codes = new List<BillingCode>();

        // SNOMED CT physiotherapy procedure concepts
        if (content.Contains("exercise") || content.Contains("strengthening")
            || content.Contains("übung") || content.Contains("exercice"))
        {
            codes.Add(new BillingCode
            {
                CptCode     = "SNOMED-229070009",
                Icd10Code   = "M54.5",
                Description = "Exercise therapy",
                ConfidenceScore  = 0.86,
                DenialRiskScore  = 18,
                MissingDocumentation = content.Contains("duration") || content.Contains("min")
                    ? []
                    : ["Document session duration — required for time-based billing in DE and FR"],
            });
        }

        if (content.Contains("manual") || content.Contains("mobilization") || content.Contains("mobilisation")
            || content.Contains("manipulation") || content.Contains("massage"))
        {
            codes.Add(new BillingCode
            {
                CptCode     = "SNOMED-229070010",
                Icd10Code   = "M54.5",
                Description = "Manual therapy",
                ConfidenceScore  = 0.84,
                DenialRiskScore  = 20,
                MissingDocumentation = ["Specify anatomical region and technique (Maitland, Mulligan, Kaltenborn, etc.)"],
            });
        }

        if (content.Contains("electro") || content.Contains("ultrasound") || content.Contains("tens")
            || content.Contains("laser") || content.Contains("thermotherapy"))
        {
            codes.Add(new BillingCode
            {
                CptCode     = "SNOMED-229070011",
                Icd10Code   = "M54.5",
                Description = "Electrophysical agent therapy",
                ConfidenceScore  = 0.79,
                DenialRiskScore  = 16,
                MissingDocumentation = ["Document device parameters (frequency, intensity, duration)"],
            });
        }

        if (codes.Count == 0)
            codes.Add(new BillingCode
            {
                CptCode     = "SNOMED-229070009",
                Icd10Code   = "M54.5",
                Description = "Physiotherapy treatment session",
                ConfidenceScore  = 0.55,
                DenialRiskScore  = 40,
                MissingDocumentation = ["Specify interventions performed", "ICD-10 code required", "Document session duration"],
            });

        return codes;
    }

    /// <summary>
    /// Detects language/country indicators in the note to suggest country-specific modifiers
    /// or documentation notes. This is a lightweight heuristic — a production implementation
    /// would use the provider's registered country.
    /// </summary>
    private static List<string> DetectCountryModifiers(string content)
    {
        if (content.Contains("nhs") || content.Contains("gp referral") || content.Contains("iapt"))
            return ["NHS-UK: ensure referral pathway documented"];
        if (content.Contains("krankenkasse") || content.Contains("gkv") || content.Contains("pkv"))
            return ["DE: Antrag auf Psychotherapie (KZT/LZT) may be required for GKV reimbursement"];
        if (content.Contains("sécurité sociale") || content.Contains("cpam") || content.Contains("ameli"))
            return ["FR: ordonnance médicale and CPAM declaration required"];
        return [];
    }
}
