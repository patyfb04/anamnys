using Anamnys.Domain.Entities;
using Anamnys.Domain.Enums;

namespace Anamnys.Infrastructure.Services.Billing;

/// <summary>
/// Brazil billing engine — TUSS procedure codes + CID-10 diagnoses.
///
/// TUSS (Terminologia Unificada da Saúde Suplementar) is the standardized
/// procedure code table mandated by ANS (Agência Nacional de Saúde Suplementar)
/// for all private health insurers (operadoras) in Brazil.
///
/// Public health (SUS) uses SIGTAP codes — not yet implemented.
/// CBHPM (Classificação Brasileira Hierarquizada de Procedimentos Médicos)
/// is used by some private payers alongside TUSS.
///
/// Mental health TUSS codes:
///   10101039 — Psicoterapia individual por sessão (individual psychotherapy session)
///   10101047 — Psicoterapia em grupo (group psychotherapy)
///   10101020 — Avaliação psiquiátrica / psicológica (psychiatric/psychological evaluation)
///
/// Fisioterapia (Physical Therapy) TUSS codes:
///   20104011 — Avaliação fisioterapêutica (physiotherapy assessment)
///   20104038 — Cinesioterapia (kinesiotherapy / therapeutic exercise)
///   20104046 — Terapia manual (manual therapy / manipulative physiotherapy)
///   20104054 — Eletroterapia (electrotherapy — common in Brazilian PT practice)
///
/// Diagnoses use CID-10 (Classificação Internacional de Doenças, 10ª revisão),
/// the Brazilian adoption of ICD-10. Most codes are identical to ICD-10.
///
/// Key payer rules:
/// - ANS mandates pre-authorization (prévia autorização) for sessions above the carência period
/// - Most operadoras cover 12–24 psychotherapy sessions/year; beyond that requires médico regulador approval
/// - Guia de Serviço Profissional (TISS XML) must accompany the billing submission
/// </summary>
internal class BrazilBillingEngine : IBillingEngineStrategy
{
    public BillingSystem BillingSystem => BillingSystem.BrazilTuss;

    public List<BillingCode> DeriveCodes(StructuredNote note, Specialty specialty)
        => specialty == Specialty.MentalHealth
            ? DerivePsicoterapiaCodes(note)
            : DeriveFisioterapiaCodes(note);

    private static List<BillingCode> DerivePsicoterapiaCodes(StructuredNote note)
    {
        var content = string.Join(" ", note.Sections.Values).ToLowerInvariant();
        var missing = new List<string>();

        // Determine session type — group vs individual
        var isGroup = content.Contains("group") || content.Contains("grupo");
        var isEvaluation = content.Contains("avaliaç") || content.Contains("evaluation") || content.Contains("initial");

        string code;
        string description;
        if (isEvaluation)
        {
            code = "10101020"; description = "Avaliação psiquiátrica / psicológica";
        }
        else if (isGroup)
        {
            code = "10101047"; description = "Psicoterapia em grupo";
        }
        else
        {
            code = "10101039"; description = "Psicoterapia individual por sessão";
        }

        // ANS requires CID-10 diagnosis and documented therapeutic approach
        if (!content.Contains("diagnos") && !content.Contains("cid") && !content.Contains("transtorno"))
            missing.Add("CID-10 obrigatório para faturamento TUSS (diagnóstico principal e secundário)");
        if (!content.Contains("plano") && !content.Contains("plan") && !content.Contains("objetivo"))
            missing.Add("Plano terapêutico e objetivos do tratamento requeridos pela operadora");
        if (!content.Contains("técnica") && !content.Contains("abordagem") && !content.Contains("approach") && !isEvaluation)
            missing.Add("Registre a abordagem psicoterapêutica utilizada (ex: TCC, psicanálise, EMDR)");

        // Pre-authorization warning for extended treatment
        var authWarning = new List<string>();
        if (!isEvaluation)
            authWarning.Add("Verifique limite de sessões cobertas pela operadora — prévia autorização pode ser necessária após o limite");

        return
        [
            new BillingCode
            {
                CptCode      = code,
                Icd10Code    = "F33.1",   // CID-10: Transtorno depressivo recorrente, episódio atual moderado
                Description  = description,
                ConfidenceScore   = missing.Count == 0 ? 0.88 : 0.65,
                DenialRiskScore   = missing.Count switch { 0 => 18, 1 => 45, _ => 72 },
                MissingDocumentation = [.. missing, .. authWarning],
            }
        ];
    }

    private static List<BillingCode> DeriveFisioterapiaCodes(StructuredNote note)
    {
        var content = string.Join(" ", note.Sections.Values).ToLowerInvariant();
        var codes = new List<BillingCode>();

        // Assessment
        if (content.Contains("avaliaç") || content.Contains("assessment") || content.Contains("initial"))
        {
            codes.Add(new BillingCode
            {
                CptCode     = "20104011",
                Icd10Code   = "M54.5",   // CID-10: Dorsalgia
                Description = "Avaliação fisioterapêutica",
                ConfidenceScore  = 0.92,
                DenialRiskScore  = 10,
                MissingDocumentation = content.Contains("escala") || content.Contains("score")
                    ? []
                    : ["Inclua escalas funcionais validadas (ex: EVA, SF-36, DASH)"],
            });
        }

        // Cinesioterapia (therapeutic exercise)
        if (content.Contains("exerc") || content.Contains("cinesio") || content.Contains("fortalecimento")
            || content.Contains("strengthening") || content.Contains("exercise"))
        {
            codes.Add(new BillingCode
            {
                CptCode     = "20104038",
                Icd10Code   = "M54.5",
                Description = "Cinesioterapia",
                ConfidenceScore  = 0.87,
                DenialRiskScore  = 18,
                MissingDocumentation = content.Contains("sessão") || content.Contains("session") || content.Contains("min")
                    ? []
                    : ["Registre carga, séries e tempo de cada exercício (TUSS exige especificidade)"],
            });
        }

        // Terapia manual
        if (content.Contains("manual") || content.Contains("mobilizaç") || content.Contains("manipulaç")
            || content.Contains("mobilization") || content.Contains("manipulation"))
        {
            codes.Add(new BillingCode
            {
                CptCode     = "20104046",
                Icd10Code   = "M54.5",
                Description = "Terapia manual",
                ConfidenceScore  = 0.83,
                DenialRiskScore  = 22,
                MissingDocumentation = ["Especifique região anatômica e técnica aplicada (Maitland, Mulligan, etc.)"],
            });
        }

        // Eletroterapia (very common in Brazilian PT — often co-billed)
        if (content.Contains("eletro") || content.Contains("tens") || content.Contains("ultrassom")
            || content.Contains("ultrasound") || content.Contains("laser"))
        {
            codes.Add(new BillingCode
            {
                CptCode     = "20104054",
                Icd10Code   = "M54.5",
                Description = "Eletroterapia / agentes físicos",
                ConfidenceScore  = 0.80,
                DenialRiskScore  = 15,
                MissingDocumentation = ["Registre parâmetros do equipamento (frequência, intensidade, tempo)"],
            });
        }

        if (codes.Count == 0)
            codes.Add(new BillingCode
            {
                CptCode     = "20104038",
                Icd10Code   = "M54.5",
                Description = "Cinesioterapia (padrão)",
                ConfidenceScore  = 0.55,
                DenialRiskScore  = 42,
                MissingDocumentation = ["Especifique os procedimentos realizados na sessão", "CID-10 principal e secundário obrigatórios"],
            });

        return codes;
    }
}
