namespace Anamnys.Server.DocumentFacts;

// design/specs/2026-10-26-document-facts-design.md §1 and §2.
public static class FactKinds
{
    public const string Issuer = "issuer";
    public const string Registration = "registration";
    public const string IssueDate = "issue_date";
    public const string Icd = "icd";
    public const string Medication = "medication";
    public const string Conclusion = "conclusion";

    public static readonly string[] All = [Issuer, Registration, IssueDate, Icd, Medication, Conclusion];
}

public static class FactStatus
{
    public const string Pending = "pending";
    public const string Confirmed = "confirmed";
    public const string Corrected = "corrected";
    public const string Discarded = "discarded";
}

// One page of text given to the extractor (the read text of ExternalDocumentPages).
public sealed record FactPage(int PageNumber, string Text);

// What the model declared: not trusted. FactService validates the value (FactValidator) and locates the
// quote on that page (ExcerptLocator) before anything is stored.
public sealed record FactCandidate(string Kind, string Value, int Page, string Quote);
