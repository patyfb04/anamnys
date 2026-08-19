using Anamnys.Domain.Entities;
using Anamnys.Domain.Enums;

namespace Anamnys.Infrastructure.Services.Billing;

/// <summary>
/// United States billing engine — CPT procedure codes + ICD-10-CM diagnoses.
///
/// Mental health CPT codes:
///   90837 — Individual psychotherapy, 60 min (most common for therapy sessions)
///   90834 — Individual psychotherapy, 45 min
///
/// Physical therapy CPT codes:
///   97110 — Therapeutic exercises (strength, endurance, ROM)
///   97140 — Manual therapy (mobilization, manipulation, soft tissue)
///   97530 — Therapeutic activities (functional tasks, ADL training)
///
/// Denial risk is computed based on CMS and commercial payer documentation rules.
/// Missing diagnosis or treatment plan are the most common denial triggers.
/// </summary>
internal class UsBillingEngine : IBillingEngineStrategy
{
    public BillingSystem BillingSystem => BillingSystem.UsCpt;

    public List<BillingCode> DeriveCodes(StructuredNote note, Specialty specialty)
        => specialty == Specialty.MentalHealth
            ? DeriveMentalHealthCodes(note)
            : DerivePhysicalTherapyCodes(note);

    private static List<BillingCode> DeriveMentalHealthCodes(StructuredNote note)
    {
        var content = string.Join(" ", note.Sections.Values).ToLowerInvariant();
        var is60Min = content.Contains("60 min") || content.Contains("60-minute") || content.Contains("one hour");
        var missing = new List<string>();

        if (!content.Contains("diagnos"))
            missing.Add("Diagnosis statement required for medical necessity");
        if (!content.Contains("plan") && !content.Contains("treatment"))
            missing.Add("Treatment plan or goal update required");

        return
        [
            new BillingCode
            {
                CptCode      = is60Min ? "90837" : "90834",
                Icd10Code    = "F32.1",
                Description  = is60Min ? "Psychotherapy, 60 minutes" : "Psychotherapy, 45 minutes",
                ConfidenceScore   = missing.Count == 0 ? 0.92 : 0.70,
                DenialRiskScore   = missing.Count switch { 0 => 15, 1 => 45, _ => 75 },
                MissingDocumentation = missing,
            }
        ];
    }

    private static List<BillingCode> DerivePhysicalTherapyCodes(StructuredNote note)
    {
        var content = string.Join(" ", note.Sections.Values).ToLowerInvariant();
        var codes = new List<BillingCode>();

        if (content.Contains("exercise") || content.Contains("strengthening") || content.Contains("stretching"))
            codes.Add(new BillingCode
            {
                CptCode     = "97110",
                Icd10Code   = "M54.5",
                Description = "Therapeutic exercises",
                ConfidenceScore  = 0.88,
                DenialRiskScore  = 20,
                Modifiers   = ["GP"],
                MissingDocumentation = content.Contains("unit") ? [] : ["Document time units for 97110"],
            });

        if (content.Contains("manual") || content.Contains("mobilization") || content.Contains("manipulation"))
            codes.Add(new BillingCode
            {
                CptCode     = "97140",
                Icd10Code   = "M54.5",
                Description = "Manual therapy techniques",
                ConfidenceScore  = 0.85,
                DenialRiskScore  = 25,
                Modifiers   = ["GP"],
                MissingDocumentation = ["Ensure GP modifier appended for Medicare"],
            });

        if (codes.Count == 0)
            codes.Add(new BillingCode
            {
                CptCode     = "97530",
                Icd10Code   = "M54.5",
                Description = "Therapeutic activities",
                ConfidenceScore  = 0.60,
                DenialRiskScore  = 40,
                MissingDocumentation = ["Specify functional activities performed", "Document time in units"],
            });

        return codes;
    }
}
