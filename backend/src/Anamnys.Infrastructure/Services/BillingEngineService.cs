using Anamnys.Application.Interfaces;
using Anamnys.Domain.Entities;
using Anamnys.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Anamnys.Infrastructure.Services;

/// <summary>
/// Deterministic billing engine.
/// Maps note format + specialty + session indicators to CPT/ICD-10 codes.
/// Computes denial risk based on documented payer rules.
/// </summary>
public class BillingEngineService : IBillingEngine
{
    private readonly ILogger<BillingEngineService> _logger;

    public BillingEngineService(ILogger<BillingEngineService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Entry point called by PipelineJob (Step 4) after note structuring.
    /// Routes to specialty-specific rule sets — mental health vs physical therapy
    /// have completely different CPT code families and payer rules.
    /// </summary>
    public Task<IReadOnlyList<BillingCode>> DeriveCodesAsync(
        StructuredNote note,
        Specialty specialty,
        BillingSystem billingSystem = BillingSystem.UsCpt,
        string? payerName = null,
        CancellationToken ct = default)
    {
        var codes = specialty == Specialty.MentalHealth
            ? DeriveMentalHealthCodes(note)
            : DerivePhysicalTherapyCodes(note);

        _logger.LogInformation("Billing engine derived {Count} code(s) for {Specialty}", codes.Count, specialty);
        return Task.FromResult<IReadOnlyList<BillingCode>>(codes);
    }

    /// <summary>
    /// Derives mental health CPT codes from note content using keyword heuristics.
    /// Distinguishes 45-min (90834) vs 60-min (90837) psychotherapy sessions.
    /// Computes a denial risk score based on missing required documentation fields —
    /// providers see this score color-coded in the mobile app before signing.
    /// </summary>
    private static List<BillingCode> DeriveMentalHealthCodes(StructuredNote note)
    {
        var codes = new List<BillingCode>();
        var content = string.Join(" ", note.Sections.Values).ToLowerInvariant();

        // Psychotherapy: 90834 (45 min), 90837 (60 min) — detect duration indicators
        var is60Min = content.Contains("60 min") || content.Contains("60-minute") || content.Contains("one hour");
        var cptCode = is60Min ? "90837" : "90834";
        var missingDocs = new List<string>();

        if (!content.Contains("diagnosis") && !content.Contains("diagnos"))
            missingDocs.Add("Diagnosis statement required for medical necessity");
        if (!content.Contains("plan") && !content.Contains("treatment"))
            missingDocs.Add("Treatment plan or goal update required");

        // Denial risk: elevated proportionally with missing documentation
        var denialRisk = missingDocs.Count switch
        {
            0 => 15,
            1 => 45,
            _ => 75
        };

        codes.Add(new BillingCode
        {
            CptCode = cptCode,
            Icd10Code = "F32.1", // Major depressive disorder, single episode, moderate (example)
            Description = is60Min ? "Psychotherapy, 60 minutes" : "Psychotherapy, 45 minutes",
            ConfidenceScore = missingDocs.Count == 0 ? 0.92 : 0.70,
            DenialRiskScore = denialRisk,
            MissingDocumentation = missingDocs
        });

        return codes;
    }

    /// <summary>
    /// Derives physical therapy CPT codes from note content.
    /// Detects therapeutic exercise (97110), manual therapy (97140), and falls back
    /// to therapeutic activities (97530). Flags Medicare GP modifier requirements,
    /// which are a common denial trigger for PT claims.
    /// </summary>
    private static List<BillingCode> DerivePhysicalTherapyCodes(StructuredNote note)
    {
        var codes = new List<BillingCode>();
        var content = string.Join(" ", note.Sections.Values).ToLowerInvariant();

        // Therapeutic exercises
        if (content.Contains("exercise") || content.Contains("strengthening") || content.Contains("stretching"))
        {
            codes.Add(new BillingCode
            {
                CptCode = "97110",
                Icd10Code = "M54.5", // Low back pain (example)
                Description = "Therapeutic exercises",
                ConfidenceScore = 0.88,
                DenialRiskScore = 20,
                MissingDocumentation = content.Contains("unit") ? [] : ["Document time units for 97110"]
            });
        }

        // Manual therapy
        if (content.Contains("manual") || content.Contains("mobilization") || content.Contains("manipulation"))
        {
            codes.Add(new BillingCode
            {
                CptCode = "97140",
                Icd10Code = "M54.5",
                Description = "Manual therapy techniques",
                ConfidenceScore = 0.85,
                DenialRiskScore = 25,
                Modifiers = content.Contains("gp") ? [] : ["GP"],
                MissingDocumentation = ["Ensure GP modifier appended for Medicare"]
            });
        }

        if (codes.Count == 0)
        {
            // Fallback: therapeutic procedure
            codes.Add(new BillingCode
            {
                CptCode = "97530",
                Icd10Code = "M54.5",
                Description = "Therapeutic activities",
                ConfidenceScore = 0.60,
                DenialRiskScore = 40,
                MissingDocumentation = ["Specify functional activities performed", "Document time in units"]
            });
        }

        return codes;
    }
}
