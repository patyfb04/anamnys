using Anamnys.Domain.Entities;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Anamnys.Api.Pdf;

/// <summary>
/// Generates a clinical note PDF using QuestPDF.
/// Supports DAP, SOAP, and PT Functional formats.
/// </summary>
public class NotePdfDocument : IDocument
{
    private readonly Note _note;
    private readonly Patient _patient;
    private readonly Provider _provider;

    // ── palette ────────────────────────────────────────────────────────────────
    private static readonly string HeaderColor   = "#1e40af"; // blue-800
    private static readonly string AccentColor   = "#3b82f6"; // blue-500
    private static readonly string LightGray     = "#f8fafc";
    private static readonly string BorderGray    = "#e2e8f0";
    private static readonly string TextMuted     = "#64748b";
    private static readonly string DangerColor   = "#dc2626";
    private static readonly string WarningColor  = "#d97706";
    private static readonly string SafeColor     = "#16a34a";

    public NotePdfDocument(Note note, Patient patient, Provider provider)
    {
        _note     = note;
        _patient  = patient;
        _provider = provider;
    }

    public DocumentMetadata GetMetadata() => new()
    {
        Title       = $"Clinical Note — {_patient.FirstName} {_patient.LastName}",
        Author      = _provider.Name,
        Creator     = "Anamnys AI",
        Producer    = "Anamnys AI",
        CreationDate = _note.SignedAt?.UtcDateTime ?? DateTime.UtcNow,
    };

    public DocumentSettings GetSettings() => DocumentSettings.Default;

    public void Compose(IDocumentContainer container)
    {
        container.Page(page =>
        {
            page.Size(PageSizes.Letter);
            page.Margin(40);
            page.DefaultTextStyle(x => x.FontSize(10).FontColor(Colors.Grey.Darken4));

            page.Header().Element(ComposeHeader);
            page.Content().PaddingTop(12).Element(ComposeContent);
            page.Footer().Element(ComposeFooter);
        });
    }

    // ── Header ─────────────────────────────────────────────────────────────────
    private void ComposeHeader(IContainer c)
    {
        c.Column(col =>
        {
            // top bar
            col.Item().Background(HeaderColor).Padding(14).Row(row =>
            {
                row.RelativeItem().Column(inner =>
                {
                    inner.Item().Text("Anamnys AI")
                        .FontSize(16).Bold().FontColor(Colors.White);
                    inner.Item().Text("Clinical Documentation")
                        .FontSize(9).FontColor("#93c5fd");
                });

                row.ConstantItem(160).AlignRight().Column(inner =>
                {
                    inner.Item().Text(_note.StructuredContent?.Format.ToString() ?? "Note")
                        .FontSize(13).Bold().FontColor(Colors.White);
                    inner.Item().Text($"Signed: {_note.SignedAt:MMM dd, yyyy HH:mm} UTC")
                        .FontSize(8).FontColor("#bfdbfe");
                });
            });

            // patient + provider info strip
            col.Item().Background(LightGray).BorderBottom(1).BorderColor(BorderGray)
                .Padding(10).Row(row =>
                {
                    // Patient
                    row.RelativeItem().Column(inner =>
                    {
                        inner.Item().Text("PATIENT").FontSize(7).Bold().FontColor(TextMuted);
                        inner.Item().Text($"{_patient.FirstName} {_patient.LastName}")
                            .FontSize(11).Bold();
                        inner.Item().Text($"DOB: {_patient.DateOfBirth:MM/dd/yyyy}  |  " +
                                          $"Age: {Age(_patient.DateOfBirth)}")
                            .FontSize(8).FontColor(TextMuted);
                    });

                    // Provider
                    row.RelativeItem().Column(inner =>
                    {
                        inner.Item().Text("PROVIDER").FontSize(7).Bold().FontColor(TextMuted);
                        inner.Item().Text(_provider.Name).FontSize(11).Bold();
                        inner.Item().Text(_provider.Email).FontSize(8).FontColor(TextMuted);
                    });

                    // Note metadata
                    row.ConstantItem(140).Column(inner =>
                    {
                        inner.Item().Text("NOTE ID").FontSize(7).Bold().FontColor(TextMuted);
                        inner.Item().Text(_note.Id.ToString()[..8] + "…")
                            .FontSize(8).FontColor(TextMuted);
                        inner.Item().PaddingTop(4).Text("STATUS").FontSize(7).Bold().FontColor(TextMuted);
                        inner.Item().Text(_note.Status.ToString().ToUpperInvariant())
                            .FontSize(9).Bold().FontColor(AccentColor);
                    });
                });
        });
    }

    // ── Content ────────────────────────────────────────────────────────────────
    private void ComposeContent(IContainer c)
    {
        c.Column(col =>
        {
            col.Spacing(14);

            // ── Active diagnoses ─────────────────────────────────────────────
            if (_patient.Diagnoses.Count > 0)
            {
                col.Item().Element(SectionHeader("Active Diagnoses", AccentColor));
                col.Item().Table(t =>
                {
                    t.ColumnsDefinition(cd => cd.RelativeColumn());
                    foreach (var dx in _patient.Diagnoses)
                        t.Cell().BorderBottom(1).BorderColor(BorderGray)
                            .PaddingVertical(4).Text($"• {dx}").FontSize(9);
                });
            }

            // ── Note sections (DAP / SOAP / PT) ──────────────────────────────
            if (_note.StructuredContent?.Sections is { } sections)
            {
                col.Item().Element(SectionHeader("Session Note", AccentColor));
                foreach (var (label, body) in sections)
                {
                    col.Item().Column(inner =>
                    {
                        inner.Item().Background(LightGray).Border(1).BorderColor(BorderGray)
                            .Padding(8).Column(block =>
                            {
                                block.Item().Text(label.ToUpperInvariant())
                                    .FontSize(8).Bold().FontColor(AccentColor);
                                block.Item().PaddingTop(4).Text(body).FontSize(10).LineHeight(1.4f);
                            });
                    });
                }
            }

            // ── Billing codes ─────────────────────────────────────────────────
            if (_note.BillingCodes.Count > 0)
            {
                col.Item().Element(SectionHeader("Billing Codes", AccentColor));
                col.Item().Table(t =>
                {
                    t.ColumnsDefinition(cd =>
                    {
                        cd.ConstantColumn(60);  // CPT
                        cd.ConstantColumn(70);  // ICD-10
                        cd.RelativeColumn();    // Description
                        cd.ConstantColumn(55);  // Confidence
                        cd.ConstantColumn(65);  // Denial Risk
                        cd.ConstantColumn(70);  // Modifiers
                    });

                    // header row
                    foreach (var h in new[] { "CPT", "ICD-10", "Description", "Confidence", "Denial Risk", "Modifiers" })
                        t.Cell().Background(HeaderColor).Padding(5)
                            .Text(h).FontSize(8).Bold().FontColor(Colors.White);

                    // data rows
                    foreach (var bc in _note.BillingCodes)
                    {
                        var riskColor = bc.DenialRiskScore >= 50 ? DangerColor
                                      : bc.DenialRiskScore >= 25 ? WarningColor
                                      : SafeColor;

                        t.Cell().BorderBottom(1).BorderColor(BorderGray).Padding(5)
                            .Text(bc.CptCode).FontSize(9).Bold();
                        t.Cell().BorderBottom(1).BorderColor(BorderGray).Padding(5)
                            .Text(bc.Icd10Code).FontSize(9);
                        t.Cell().BorderBottom(1).BorderColor(BorderGray).Padding(5)
                            .Text(bc.Description).FontSize(9);
                        t.Cell().BorderBottom(1).BorderColor(BorderGray).Padding(5)
                            .Text($"{bc.ConfidenceScore:P0}").FontSize(9);
                        t.Cell().BorderBottom(1).BorderColor(BorderGray).Padding(5)
                            .Text($"{bc.DenialRiskScore}%").FontSize(9).FontColor(riskColor);
                        t.Cell().BorderBottom(1).BorderColor(BorderGray).Padding(5)
                            .Text(bc.Modifiers.Count > 0 ? string.Join(", ", bc.Modifiers) : "—").FontSize(9);
                    }
                });

                // missing documentation warnings
                var missing = _note.BillingCodes
                    .Where(b => b.MissingDocumentation.Count > 0)
                    .SelectMany(b => b.MissingDocumentation)
                    .Distinct()
                    .ToList();

                if (missing.Count > 0)
                {
                    col.Item().Background("#fef2f2").Border(1).BorderColor("#fecaca")
                        .Padding(8).Column(warn =>
                        {
                            warn.Item().Text("⚠ Missing Documentation")
                                .FontSize(9).Bold().FontColor(DangerColor);
                            foreach (var m in missing)
                                warn.Item().Text($"• {m}").FontSize(9).FontColor(DangerColor);
                        });
                }
            }

            // ── Prior auth letter (if present) ────────────────────────────────
            if (!string.IsNullOrWhiteSpace(_note.PriorAuthLetter))
            {
                col.Item().Element(SectionHeader("Prior Authorization Letter", AccentColor));
                col.Item().Background(LightGray).Border(1).BorderColor(BorderGray)
                    .Padding(10).Text(_note.PriorAuthLetter).FontSize(9).LineHeight(1.5f);
            }

            // ── Audit trail ───────────────────────────────────────────────────
            if (_note.AuditTrail.Count > 0)
            {
                col.Item().Element(SectionHeader("Audit Trail", TextMuted));
                col.Item().Table(t =>
                {
                    t.ColumnsDefinition(cd =>
                    {
                        cd.ConstantColumn(130); // timestamp
                        cd.ConstantColumn(100); // action
                        cd.RelativeColumn();    // actor / detail
                    });

                    foreach (var h in new[] { "Timestamp (UTC)", "Action", "Actor" })
                        t.Cell().Background(BorderGray).Padding(4)
                            .Text(h).FontSize(7).Bold().FontColor(TextMuted);

                    foreach (var entry in _note.AuditTrail.OrderBy(a => a.Timestamp))
                    {
                        t.Cell().BorderBottom(1).BorderColor(BorderGray).Padding(4)
                            .Text(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss")).FontSize(8);
                        t.Cell().BorderBottom(1).BorderColor(BorderGray).Padding(4)
                            .Text(entry.Action).FontSize(8);
                        t.Cell().BorderBottom(1).BorderColor(BorderGray).Padding(4)
                            .Text(entry.ActorId == Guid.Empty ? "AI System" : entry.ActorId.ToString()[..8] + "…")
                            .FontSize(8).FontColor(TextMuted);
                    }
                });
            }

            // ── Signature block ───────────────────────────────────────────────
            col.Item().PaddingTop(10).BorderTop(2).BorderColor(AccentColor).PaddingTop(10)
                .Row(row =>
                {
                    row.RelativeItem().Column(sig =>
                    {
                        sig.Item().Text("Electronically signed by:").FontSize(8).FontColor(TextMuted);
                        sig.Item().Text(_provider.Name).FontSize(12).Bold();
                        sig.Item().Text(_provider.Email).FontSize(8).FontColor(TextMuted);
                    });
                    row.ConstantItem(160).AlignRight().Column(sig =>
                    {
                        sig.Item().Text("Date & Time (UTC)").FontSize(8).FontColor(TextMuted);
                        sig.Item().Text(_note.SignedAt?.ToString("MMM dd, yyyy HH:mm") ?? "—")
                            .FontSize(10).Bold();
                    });
                });
        });
    }

    // ── Footer ─────────────────────────────────────────────────────────────────
    private void ComposeFooter(IContainer c)
    {
        c.BorderTop(1).BorderColor(BorderGray).PaddingTop(6).Row(row =>
        {
            row.RelativeItem().Text("Anamnys AI — Confidential Clinical Document")
                .FontSize(7).FontColor(TextMuted);
            row.ConstantItem(80).AlignRight()
                .Text(t =>
                {
                    t.Span("Page ").FontSize(7).FontColor(TextMuted);
                    t.CurrentPageNumber().FontSize(7).FontColor(TextMuted);
                    t.Span(" of ").FontSize(7).FontColor(TextMuted);
                    t.TotalPages().FontSize(7).FontColor(TextMuted);
                });
        });
    }

    // ── Helpers ────────────────────────────────────────────────────────────────
    private static Action<IContainer> SectionHeader(string title, string color) =>
        c => c.BorderBottom(2).BorderColor(color).PaddingBottom(4)
              .Text(title).FontSize(11).Bold().FontColor(color);

    private static int Age(DateOnly dob)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var age   = today.Year - dob.Year;
        if (dob > today.AddYears(-age)) age--;
        return age;
    }
}
