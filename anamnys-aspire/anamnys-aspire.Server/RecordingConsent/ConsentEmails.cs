using System.Globalization;
using System.Text.Encodings.Web;
using Anamnys.Server.Email;

namespace Anamnys.Server.RecordingConsents;

// The two consent e-mails (design/specs/2026-11-15-recording-consent-design.md §4). No clinical content: the patient's
// first name, the provider's name, the link and its deadline or the signing time.
public static class ConsentEmails
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly TimeZoneInfo Practice = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    public const string RequestSubject = "Termo de consentimento para gravação das sessões";
    public const string SignedSubject = "Consentimento para gravação registrado";

    // Minor: the contact e-mail reaches the guardian, who signs for the patient.
    public static EmailMessage Request(string toEmail, string firstName, string providerName, bool minor, Uri link, DateTimeOffset expiresAt)
    {
        var until = TimeZoneInfo.ConvertTime(expiresAt, Practice).ToString("dd/MM/yyyy", PtBr);
        var greeting = minor ? "Olá." : $"Olá, {firstName}.";
        var ask = minor
            ? $"{providerName} pede o consentimento do(a) responsável legal por {firstName} para gravar o áudio das sessões. "
              + "O termo explica para que serve a gravação e o que acontece com ela."
            : $"{providerName} pede o seu consentimento para gravar o áudio das sessões. "
              + "O termo explica para que serve a gravação e o que acontece com ela.";
        const string how = "Leia o termo e, se concordar, assine pelo link abaixo. Para assinar, informe a data de nascimento do(a) paciente.";
        const string voluntary = "O consentimento é voluntário: sem ele, as sessões continuam normalmente, sem gravação.";

        var text = $"{greeting}\n\n{ask}\n\n{how}\n\n{link}\n\nO link vale até {until}.\n\n{voluntary}\n";

        var html = HtmlEncoder.Default;
        var htmlBody =
            $"<p>{html.Encode(greeting)}</p>" +
            $"<p>{html.Encode(ask)}</p>" +
            $"<p>{html.Encode(how)}</p>" +
            $"<p><a href=\"{html.Encode(link.ToString())}\">Ler e assinar o termo</a></p>" +
            $"<p>O link vale até {until}.</p>" +
            $"<p>{html.Encode(voluntary)}</p>";

        return new EmailMessage(toEmail, firstName, RequestSubject, text, htmlBody);
    }

    // The signed copy: the revocation page shows the signed term and takes the revocation.
    public static EmailMessage Signed(string toEmail, string firstName, string providerName, Uri revokeLink, DateTimeOffset signedAt)
    {
        var at = TimeZoneInfo.ConvertTime(signedAt, Practice).ToString("dd/MM/yyyy 'às' HH:mm", PtBr);
        var greeting = $"Olá, {firstName}.";
        var done = $"O consentimento para gravação do áudio das sessões com {providerName} foi assinado em {at}.";
        const string copy = "Pelo link abaixo você vê a cópia do termo assinado e pode revogar o consentimento a qualquer momento. "
            + "A revogação interrompe as gravações futuras e não apaga os registros clínicos já assinados.";
        const string keep = "Guarde este e-mail: o link é pessoal.";

        var text = $"{greeting}\n\n{done}\n\n{copy}\n\n{revokeLink}\n\n{keep}\n";

        var html = HtmlEncoder.Default;
        var htmlBody =
            $"<p>{html.Encode(greeting)}</p>" +
            $"<p>{html.Encode(done)}</p>" +
            $"<p>{html.Encode(copy)}</p>" +
            $"<p><a href=\"{html.Encode(revokeLink.ToString())}\">Ver o termo assinado ou revogar</a></p>" +
            $"<p>{html.Encode(keep)}</p>";

        return new EmailMessage(toEmail, firstName, SignedSubject, text, htmlBody);
    }
}
