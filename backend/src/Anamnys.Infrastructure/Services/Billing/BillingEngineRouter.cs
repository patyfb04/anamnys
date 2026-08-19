using Anamnys.Application.Interfaces;
using Anamnys.Domain.Entities;
using Anamnys.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Anamnys.Infrastructure.Services.Billing;

/// <summary>
/// Implements IBillingEngine by routing to the correct country-specific engine
/// based on the provider's BillingSystem setting.
///
/// Adding a new country requires:
///   1. Add a value to the BillingSystem enum
///   2. Implement IBillingEngineStrategy for the new country
///   3. Register it in the _engines dictionary below — no other changes needed
/// </summary>
public class BillingEngineRouter : IBillingEngine
{
    private readonly IReadOnlyDictionary<BillingSystem, IBillingEngineStrategy> _engines;
    private readonly ILogger<BillingEngineRouter> _logger;

    public BillingEngineRouter(ILogger<BillingEngineRouter> logger)
    {
        _logger = logger;

        // Register all available billing engine strategies
        var strategies = new IBillingEngineStrategy[]
        {
            new UsBillingEngine(),
            new CanadaBillingEngine(),
            new BrazilBillingEngine(),
            new EuropeGenericBillingEngine(),
        };

        _engines = strategies.ToDictionary(s => s.BillingSystem);
    }

    /// <summary>
    /// Routes to the correct billing engine based on the provider's billing system.
    /// Falls back to the US CPT engine if the billing system is unrecognized.
    /// </summary>
    public Task<IReadOnlyList<BillingCode>> DeriveCodesAsync(
        StructuredNote note,
        Specialty specialty,
        BillingSystem billingSystem = BillingSystem.UsCpt,
        string? payerName = null,
        CancellationToken ct = default)
    {
        if (!_engines.TryGetValue(billingSystem, out var engine))
        {
            _logger.LogWarning(
                "No billing engine found for {BillingSystem} — falling back to US CPT engine",
                billingSystem);
            engine = _engines[BillingSystem.UsCpt];
        }

        var codes = engine.DeriveCodes(note, specialty);

        _logger.LogInformation(
            "Billing engine [{System}] derived {Count} code(s) for {Specialty}",
            billingSystem, codes.Count, specialty);

        return Task.FromResult<IReadOnlyList<BillingCode>>(codes);
    }
}
