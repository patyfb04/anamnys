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
