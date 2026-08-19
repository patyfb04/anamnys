using Anamnys.Domain.Entities;
using Anamnys.Domain.Enums;

namespace Anamnys.Application.Interfaces;

/// <summary>
/// Derives billing codes from a structured clinical note.
/// Routes to the correct country/regional billing engine based on the provider's BillingSystem.
/// Supported systems: US CPT, Canada OHIP, Brazil TUSS, Europe Generic (SNOMED CT).
/// </summary>
public interface IBillingEngine
{
    /// <param name="note">The AI-structured note to derive codes from.</param>
    /// <param name="specialty">Provider specialty — determines which code family to use.</param>
    /// <param name="billingSystem">Country/regional billing system for this provider.</param>
    /// <param name="payerName">Optional payer name for payer-specific rule overrides (Phase 2).</param>
    Task<IReadOnlyList<BillingCode>> DeriveCodesAsync(
        StructuredNote note,
        Specialty specialty,
        BillingSystem billingSystem = BillingSystem.UsCpt,
        string? payerName = null,
        CancellationToken ct = default);
}
