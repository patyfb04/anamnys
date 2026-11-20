namespace Anamnys.Server.Pseudonymization;

// Kinds of third-party mentions (design/specs/2026-11-17-pseudonymization-design.md §3).
public static class EntityKinds
{
    public const string Person = "person";
    public const string Organization = "organization";
    public const string School = "school";
    public const string Address = "address";

    public static readonly string[] All = [Person, Organization, School, Address];

    public static bool IsKnown(string? kind) => kind is Person or Organization or School or Address;
}

// A mention in the text: [Start, Start + Length) in UTF-16 code units.
public sealed record DetectedSpan(int Start, int Length, string Kind)
{
    public int End => Start + Length;
}

// Finds third-party mentions in a text. Exclusions are names that must never be reported (the patient's and the
// provider's). Implementations never log, store or put the text in an exception.
public interface IThirdPartyDetector
{
    Task<IReadOnlyList<DetectedSpan>> DetectAsync(string text, IReadOnlyCollection<string> exclusions, CancellationToken ct);
}
