using Anamnys.Domain.Entities;
using Anamnys.Domain.Enums;

namespace Anamnys.Infrastructure.Services.Billing;

/// <summary>
/// Canada billing engine — provincial fee schedule codes + ICD-10-CA diagnoses.
///
/// Uses OHIP (Ontario Health Insurance Plan) as the baseline schedule.
/// Other provinces use equivalent codes under their own schedules:
///   BC:  MSP (Medical Services Plan)
///   AB:  Alberta Health Care
///   QC:  RAMQ (Régie de l'assurance maladie du Québec)
///
/// Mental health fee codes (OHIP Schedule of Benefits — K prefix = psychiatry/psychology):
///   K029 — Individual psychotherapy, ≤45 minutes
///   K030 — Individual psychotherapy, 46–75 minutes
///   K031 — Individual psychotherapy, 76+ minutes
///
/// Physiotherapy (PT) codes vary significantly by province.
/// Common OHIP extended-care and WSIB codes:
///   P001 — Initial physiotherapy assessment
///   P002 — Physiotherapy treatment session (standard)
///   P003 — Complex physiotherapy treatment (manual + exercise combined)
///
/// Diagnoses use ICD-10-CA (essentially ICD-10 with Canadian modifications).
/// Most ICD-10 codes are identical between US (ICD-10-CM) and Canada (ICD-10-CA).
/// </summary>
internal class CanadaBillingEngine : IBillingEngineStrategy
{
    public BillingSystem BillingSystem => BillingSystem.CanadaOhip;

    public List<BillingCode> DeriveCodes(StructuredNote note, Specialty specialty)
        => specialty == Specialty.MentalHealth
            ? DeriveMentalHealthCodes(note)
            : DerivePhysiotherapyCodes(note);

    private static List<BillingCode> DeriveMentalHealthCodes(StructuredNote note)
    {
        var content = string.Join(" ", note.Sections.Values).ToLowerInvariant();
        var missing = new List<string>();

        // Determine session duration to select the appropriate OHIP K-code
        string code;
        string description;
        if (content.Contains("76 min") || content.Contains("76-minute") || content.Contains("90 min"))
        {
            code = "K031"; description = "Individual psychotherapy, 76+ minutes";
        }
        else if (content.Contains("60 min") || content.Contains("60-minute") || content.Contains("one hour")
                 || content.Contains("46 min") || content.Contains("50 min"))
        {
            code = "K030"; description = "Individual psychotherapy, 46–75 minutes";
        }
        else
        {
            code = "K029"; description = "Individual psychotherapy, ≤45 minutes";
        }

        // OHIP requires a recorded mental health diagnosis and documented clinical rationale
        if (!content.Contains("diagnos"))
            missing.Add("Mental health diagnosis required for OHIP psychotherapy claims");
        if (!content.Contains("plan") && !content.Contains("treatment") && !content.Contains("goal"))
            missing.Add("Documented treatment goals required for provincial billing");
        if (!content.Contains("consent") && !content.Contains("agreed"))
            missing.Add("Informed consent to treatment should be noted (OHIP recommendation)");

        return
        [
            new BillingCode
            {
                CptCode      = code,           // field name is CptCode; used as generic procedure code
                Icd10Code    = "F33.1",         // ICD-10-CA: Recurrent depressive disorder, moderate
                Description  = description,
                ConfidenceScore   = missing.Count == 0 ? 0.90 : 0.68,
                DenialRiskScore   = missing.Count switch { 0 => 12, 1 => 40, _ => 70 },
                MissingDocumentation = missing,
            }
        ];
    }

    private static List<BillingCode> DerivePhysiotherapyCodes(StructuredNote note)
    {
        var content = string.Join(" ", note.Sections.Values).ToLowerInvariant();
        var codes = new List<BillingCode>();
        var missing = new List<string>();

        // Initial assessment vs treatment session
        var isAssessment = content.Contains("assessment") || content.Contains("initial") || content.Contains("evaluation");

        if (isAssessment)
        {
            codes.Add(new BillingCode
            {
                CptCode     = "P001",
                Icd10Code   = "M54.5",    // ICD-10-CA: Low back pain
                Description = "Physiotherapy initial assessment",
                ConfidenceScore  = 0.90,
                DenialRiskScore  = 15,
                MissingDocumentation = content.Contains("objective") ? [] :
                    ["Objective outcome measures required for physiotherapy assessment (provincial standard)"],
            });
        }
        else if (content.Contains("manual") || content.Contains("mobilization")
                 || content.Contains("exercise") || content.Contains("strengthening"))
        {
            // Combined manual + exercise = P003; exercise only = P002
            var isComplex = (content.Contains("manual") || content.Contains("mobilization"))
                            && (content.Contains("exercise") || content.Contains("strengthening"));

            if (!content.Contains("time") && !content.Contains("min"))
                missing.Add("Document treatment duration in minutes (provincial billing requirement)");

            codes.Add(new BillingCode
            {
                CptCode     = isComplex ? "P003" : "P002",
                Icd10Code   = "M54.5",
                Description = isComplex
                    ? "Complex physiotherapy treatment (manual therapy + therapeutic exercise)"
                    : "Physiotherapy treatment session",
                ConfidenceScore  = 0.85,
                DenialRiskScore  = isComplex ? 20 : 15,
                MissingDocumentation = missing,
            });
        }
        else
        {
            codes.Add(new BillingCode
            {
                CptCode     = "P002",
                Icd10Code   = "M54.5",
                Description = "Physiotherapy treatment session",
                ConfidenceScore  = 0.60,
                DenialRiskScore  = 35,
                MissingDocumentation = ["Specify interventions performed", "Document treatment time"],
            });
        }

        return codes;
    }
}
