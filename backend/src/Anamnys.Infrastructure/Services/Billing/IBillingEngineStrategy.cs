using Anamnys.Domain.Entities;
using Anamnys.Domain.Enums;

namespace Anamnys.Infrastructure.Services.Billing;

/// <summary>
/// Internal strategy interface — one implementation per billing system.
/// Registered and resolved by BillingEngineRouter; not exposed to Application layer.
/// </summary>
internal interface IBillingEngineStrategy
{
    BillingSystem BillingSystem { get; }
    List<BillingCode> DeriveCodes(StructuredNote note, Specialty specialty);
}
