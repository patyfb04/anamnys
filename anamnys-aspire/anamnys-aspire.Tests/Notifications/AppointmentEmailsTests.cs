using Anamnys.Server.Notifications;
using FluentAssertions;

namespace Anamnys.Tests.Notifications;

public class AppointmentEmailsTests
{
    private static readonly AppointmentEmailData Data = new(
        "ana@example.com", "Ana", "Dra. Beatriz Costa",
        new DateTimeOffset(2026, 10, 8, 13, 0, 0, TimeSpan.Zero), // 10:00 in São Paulo
        new DateTimeOffset(2026, 10, 8, 13, 50, 0, TimeSpan.Zero),
        "America/Sao_Paulo", "online");
    private static readonly Uri Link = new("http://localhost:5274/patient/confirmar/abc");

    [Fact]
    public void Confirmation_ShowsLocalTime_LinkAndDeadline()
    {
        var mail = AppointmentEmails.Confirmation(Data, Link, Data.StartsAt.AddHours(-2));

        mail.ToEmail.Should().Be("ana@example.com");
        mail.Subject.Should().Be("Confirme sua consulta com Dra. Beatriz Costa");
        mail.Text.Should().Contain("10:00").And.Contain("10:50").And.Contain("Online").And.Contain(Link.ToString())
            .And.Contain("08:00"); // deadline in local time
        mail.Html.Should().Contain("Confirmar presença");
    }

    [Fact]
    public void Confirmation_WithoutDeadline_OmitsTheCancellationSentence() =>
        AppointmentEmails.Confirmation(Data, Link, null).Text.Should().NotContain("será cancelada");

    [Fact]
    public void Reminder_WithoutLink_HasNoConfirmCall()
    {
        var mail = AppointmentEmails.Reminder(Data, null);
        mail.Subject.Should().Be("Sua consulta com Dra. Beatriz Costa começa em 1 hora");
        mail.Html.Should().NotContain("Confirmar presença");
    }

    [Fact]
    public void Cancellation_NamesProviderAndTime()
    {
        var mail = AppointmentEmails.Cancellation(Data);
        mail.Subject.Should().Be("Consulta cancelada");
        mail.Text.Should().Contain("Dra. Beatriz Costa").And.Contain("10:00");
    }

    [Fact]
    public void Html_EncodesNames() =>
        AppointmentEmails.Cancellation(Data with { ProviderName = "<b>x</b>" }).Html.Should().NotContain("<b>x</b>");
}
