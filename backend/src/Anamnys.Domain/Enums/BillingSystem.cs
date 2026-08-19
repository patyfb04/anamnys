namespace Anamnys.Domain.Enums;

/// <summary>
/// Identifies the billing/coding system used by a provider's country or region.
/// Controls which billing engine strategy is applied during the note pipeline.
/// The field CptCode on BillingCode is used as a generic procedure code field
/// regardless of billing system — the actual code format differs per system.
/// </summary>
public enum BillingSystem
{
    /// <summary>
    /// United States — CPT procedure codes (AMA) + ICD-10-CM diagnoses.
    /// Governed by CMS, Medicare, Medicaid, and commercial payer rules.
    /// </summary>
    UsCpt = 0,

    /// <summary>
    /// Canada — Provincial fee schedule codes + ICD-10-CA diagnoses.
    /// Baseline uses OHIP (Ontario); other provinces use equivalent schedules.
    /// Mental health: K029/K030 psychotherapy codes.
    /// Physiotherapy: provincial schedule codes vary by region.
    /// </summary>
    CanadaOhip = 1,

    /// <summary>
    /// Brazil — TUSS procedure codes (ANS) + CID-10 diagnoses.
    /// TUSS (Terminologia Unificada da Saúde Suplementar) governs private health.
    /// SUS (public) uses SIGTAP codes — not yet implemented.
    /// </summary>
    BrazilTuss = 2,

    /// <summary>
    /// Europe (generic) — ICD-10 national modifications + SNOMED CT procedure codes.
    /// Actual systems vary by country (EBM in Germany, CCAM in France, OPCS-4 in UK).
    /// This engine applies common European clinical coding conventions as a baseline.
    /// </summary>
    EuropeGeneric = 3,
}
