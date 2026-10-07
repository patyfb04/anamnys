using Anamnys.Server.Notifications;
using FluentAssertions;

namespace Anamnys.Tests.Notifications;

public class ConfirmationRulesTests
{
    private static readonly DateTimeOffset Sent = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Off_HasNoDeadline() =>
        ConfirmationRules.DeadlineAt("off", 1, Sent, Sent.AddDays(3)).Should().BeNull();

    [Fact]
    public void AfterEmail_AddsHoursToSendTime() =>
        ConfirmationRules.DeadlineAt("after_email", 2, Sent, Sent.AddDays(3)).Should().Be(Sent.AddHours(2));

    [Fact]
    public void BeforeSession_SubtractsHoursFromStart() =>
        ConfirmationRules.DeadlineAt("before_session", 24, Sent, Sent.AddDays(3)).Should().Be(Sent.AddDays(2));

    [Fact]
    public void Deadline_IsCappedAtStart() =>
        ConfirmationRules.DeadlineAt("after_email", 5, Sent, Sent.AddHours(3)).Should().Be(Sent.AddHours(3));

    [Theory]
    [InlineData(24, 3)] // "24 h before" for a session 3 h away: already past
    [InlineData(3, 3)]  // exactly at send time: not after it
    public void BeforeSession_AlreadyPast_HasNoDeadline(int hours, int startsInHours) =>
        ConfirmationRules.DeadlineAt("before_session", hours, Sent, Sent.AddHours(startsInHours)).Should().BeNull();

    [Fact]
    public void Reminder_IsOneHourBeforeStart() =>
        ConfirmationRules.ReminderAt(Sent.AddDays(1), Sent).Should().Be(Sent.AddDays(1).AddHours(-1));

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    public void Reminder_WithinTheLastHour_IsSkipped(int startsInMinutes) =>
        ConfirmationRules.ReminderAt(Sent.AddMinutes(startsInMinutes), Sent).Should().BeNull();
}
