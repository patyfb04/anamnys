using System.Globalization;
using System.Text.Encodings.Web;
using Anamnys.Server.Email;

namespace Anamnys.Server.Notifications;

public sealed record AppointmentEmailData(
    string ToEmail,
    string FirstName,
    string ProviderName,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Timezone,
    string Modality);

// A series notice (design/specs/2026-11-09-appointment-series-design.md §3 Notices). Summary: "toda terça às 14:00,
// a partir de 17/11, 10 sessões"; EndedFrom: the first date without sessions. Created: the occurrences that exist;
// Requested: the session count of a series that ends after N sessions; Missing: dates generation covered whose
// session could not be created (the slot was taken); Pending: whether later sessions are still to be generated.
public sealed record SeriesEmailData(
    string ToEmail,
    string FirstName,
    string ProviderName,
    string Summary,
    string Description,
    string Modality,
    DateOnly EndedFrom,
    int Created = 0,
    int? Requested = null,
    IReadOnlyList<DateOnly>? Missing = null,
    bool Pending = false);

public static class AppointmentEmails
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    // No clinical content: provider's name, date, time, modality and the link when there is one.
    public static EmailMessage Confirmation(AppointmentEmailData d, Uri link, DateTimeOffset? deadline)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(d.Timezone);
        var localStart = TimeZoneInfo.ConvertTime(d.StartsAt, tz);
        var localEnd = TimeZoneInfo.ConvertTime(d.EndsAt, tz);
        var dateStr = localStart.ToString("dddd, d 'de' MMMM", PtBr);
        var startTimeStr = localStart.ToString("HH:mm", PtBr);
        var endTimeStr = localEnd.ToString("HH:mm", PtBr);
        var modalityLabel = d.Modality.Equals("online", StringComparison.OrdinalIgnoreCase) ? "Online" : "Presencial";

        var subject = $"Confirme sua consulta com {d.ProviderName}";

        var text =
            $"Olá, {d.FirstName}.\n\n" +
            $"Sua consulta com {d.ProviderName} está marcada para {dateStr}, das {startTimeStr} às {endTimeStr} ({modalityLabel}).\n\n" +
            "Confirme sua presença pelo link abaixo.\n\n" +
            $"{link}\n";

        if (deadline.HasValue)
        {
            var localDeadline = TimeZoneInfo.ConvertTime(deadline.Value, tz);
            var deadlineStr = localDeadline.ToString("dd/MM/yyyy HH:mm", PtBr);
            text += $"\nSem confirmação até {deadlineStr}, a consulta será cancelada.\n";
        }

        var html = HtmlEncoder.Default;
        var htmlBody =
            $"<p>Olá, {html.Encode(d.FirstName)}.</p>" +
            $"<p>Sua consulta com {html.Encode(d.ProviderName)} está marcada para {dateStr}, das {startTimeStr} às {endTimeStr} ({modalityLabel}).</p>" +
            "<p>Confirme sua presença pelo link abaixo.</p>" +
            $"<p><a href=\"{html.Encode(link.ToString())}\">Confirmar presença</a></p>";

        if (deadline.HasValue)
        {
            var localDeadline = TimeZoneInfo.ConvertTime(deadline.Value, tz);
            var deadlineStr = localDeadline.ToString("dd/MM/yyyy HH:mm", PtBr);
            htmlBody += $"<p>Sem confirmação até {deadlineStr}, a consulta será cancelada.</p>";
        }

        return new EmailMessage(d.ToEmail, d.FirstName, subject, text, htmlBody);
    }

    public static EmailMessage Reminder(AppointmentEmailData d, Uri? link)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(d.Timezone);
        var localStart = TimeZoneInfo.ConvertTime(d.StartsAt, tz);
        var localEnd = TimeZoneInfo.ConvertTime(d.EndsAt, tz);
        var dateStr = localStart.ToString("dddd, d 'de' MMMM", PtBr);
        var startTimeStr = localStart.ToString("HH:mm", PtBr);
        var endTimeStr = localEnd.ToString("HH:mm", PtBr);
        var modalityLabel = d.Modality.Equals("online", StringComparison.OrdinalIgnoreCase) ? "Online" : "Presencial";

        var subject = $"Sua consulta com {d.ProviderName} começa em 1 hora";

        var text =
            $"Olá, {d.FirstName}.\n\n" +
            $"Sua consulta com {d.ProviderName} está marcada para {dateStr}, das {startTimeStr} às {endTimeStr} ({modalityLabel}).\n";

        if (link is not null)
        {
            text += $"\nAinda não recebemos sua confirmação.\n\n{link}\n";
        }

        var html = HtmlEncoder.Default;
        var htmlBody =
            $"<p>Olá, {html.Encode(d.FirstName)}.</p>" +
            $"<p>Sua consulta com {html.Encode(d.ProviderName)} está marcada para {dateStr}, das {startTimeStr} às {endTimeStr} ({modalityLabel}).</p>";

        if (link is not null)
        {
            htmlBody +=
                "<p>Ainda não recebemos sua confirmação.</p>" +
                $"<p><a href=\"{html.Encode(link.ToString())}\">Confirmar presença</a></p>";
        }

        return new EmailMessage(d.ToEmail, d.FirstName, subject, text, htmlBody);
    }

    // One summary when a series is created or split; no clinical content.
    public static EmailMessage SeriesScheduled(SeriesEmailData d)
    {
        var modalityLabel = d.Modality.Equals("online", StringComparison.OrdinalIgnoreCase) ? "Online" : "Presencial";
        var subject = $"Sessões marcadas com {d.ProviderName}";
        var created = CreatedSentence(d);
        var text =
            $"Olá, {d.FirstName}.\n\n" +
            $"Sessões marcadas com {d.ProviderName}: {d.Summary} ({modalityLabel}).\n\n" +
            $"{created}\n\n" +
            "Você receberá o pedido de confirmação de cada sessão alguns dias antes dela.\n";
        var html = HtmlEncoder.Default;
        var htmlBody =
            $"<p>Olá, {html.Encode(d.FirstName)}.</p>" +
            $"<p>Sessões marcadas com {html.Encode(d.ProviderName)}: {html.Encode(d.Summary)} ({modalityLabel}).</p>" +
            $"<p>{html.Encode(created)}</p>" +
            "<p>Você receberá o pedido de confirmação de cada sessão alguns dias antes dela.</p>";
        return new EmailMessage(d.ToEmail, d.FirstName, subject, text, htmlBody);
    }

    // The sessions actually created: "10 sessões já estão marcadas.", "Foram marcadas 9 das 10 sessões." and the dates
    // that could not be booked because the slot was taken.
    internal static string CreatedSentence(SeriesEmailData d)
    {
        var missing = d.Missing ?? [];
        var text = d.Requested is { } requested && missing.Count > 0 && !d.Pending
            ? $"Foram marcadas {d.Created} das {requested} sessões."
            : d.Created == 1 ? "1 sessão já está marcada." : $"{d.Created} sessões já estão marcadas.";
        if (d.Pending)
        {
            text += " As seguintes entram na agenda semana a semana.";
        }
        if (missing.Count > 0)
        {
            var dates = string.Join(", ", missing.Select(m => m.ToString("dd/MM/yyyy", PtBr)));
            text += missing.Count == 1
                ? $" Não foi possível marcar a sessão de {dates}, porque o horário já estava ocupado."
                : $" Não foi possível marcar as sessões de {dates}, porque o horário já estava ocupado.";
        }
        return text;
    }

    // One notice when a series is ended, with the first date without sessions.
    public static EmailMessage SeriesEnded(SeriesEmailData d)
    {
        var dateStr = d.EndedFrom.ToString("dd/MM/yyyy", PtBr);
        var subject = "Sessões encerradas";
        var text =
            $"Olá, {d.FirstName}.\n\n" +
            $"Suas sessões com {d.ProviderName} ({d.Description}) foram encerradas a partir de {dateStr}. " +
            "As sessões marcadas a partir dessa data foram canceladas.\n\n" +
            "Em caso de dúvida, fale com o profissional.\n";
        var html = HtmlEncoder.Default;
        var htmlBody =
            $"<p>Olá, {html.Encode(d.FirstName)}.</p>" +
            $"<p>Suas sessões com {html.Encode(d.ProviderName)} ({html.Encode(d.Description)}) foram encerradas a partir de {dateStr}. " +
            "As sessões marcadas a partir dessa data foram canceladas.</p>" +
            "<p>Em caso de dúvida, fale com o profissional.</p>";
        return new EmailMessage(d.ToEmail, d.FirstName, subject, text, htmlBody);
    }

    public static EmailMessage Cancellation(AppointmentEmailData d)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(d.Timezone);
        var localStart = TimeZoneInfo.ConvertTime(d.StartsAt, tz);
        var dateStr = localStart.ToString("dddd, d 'de' MMMM", PtBr);
        var startTimeStr = localStart.ToString("HH:mm", PtBr);

        var subject = "Consulta cancelada";

        var text =
            $"Olá, {d.FirstName}.\n\n" +
            $"Sua consulta com {d.ProviderName} de {dateStr}, às {startTimeStr}, foi cancelada.\n\n" +
            "Em caso de dúvida, fale com o profissional.\n";

        var html = HtmlEncoder.Default;
        var htmlBody =
            $"<p>Olá, {html.Encode(d.FirstName)}.</p>" +
            $"<p>Sua consulta com {html.Encode(d.ProviderName)} de {dateStr}, às {startTimeStr}, foi cancelada.</p>" +
            "<p>Em caso de dúvida, fale com o profissional.</p>";

        return new EmailMessage(d.ToEmail, d.FirstName, subject, text, htmlBody);
    }
}
