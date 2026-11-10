using Anamnys.Server.Availability;
using Anamnys.Server.Data.Entities;
using FluentAssertions;

namespace Anamnys.Tests.Availability;

// design/specs/2026-11-09-availability-design.md §3, §6: national holidays for 2024–2030 against known dates, keys and
// names, and when one applies to the provider.
public class NationalHolidaysTests
{
    [Theory]
    [InlineData(2024, "2024-03-31", "2024-02-12", "2024-02-13", "2024-03-29", "2024-05-30")]
    [InlineData(2025, "2025-04-20", "2025-03-03", "2025-03-04", "2025-04-18", "2025-06-19")]
    [InlineData(2026, "2026-04-05", "2026-02-16", "2026-02-17", "2026-04-03", "2026-06-04")]
    [InlineData(2027, "2027-03-28", "2027-02-08", "2027-02-09", "2027-03-26", "2027-05-27")]
    [InlineData(2028, "2028-04-16", "2028-02-28", "2028-02-29", "2028-04-14", "2028-06-15")]
    [InlineData(2029, "2029-04-01", "2029-02-12", "2029-02-13", "2029-03-30", "2029-05-31")]
    [InlineData(2030, "2030-04-21", "2030-03-04", "2030-03-05", "2030-04-19", "2030-06-20")]
    public void For_MovingHolidays_MatchKnownDates(int year, string easter, string carnavalMonday, string carnavalTuesday, string goodFriday, string corpusChristi)
    {
        // Act
        var holidays = NationalHolidays.For(year).ToDictionary(h => h.Key, h => h.Date);

        // Assert
        NationalHolidays.Easter(year).Should().Be(DateOnly.Parse(easter));
        holidays["carnaval_segunda"].Should().Be(DateOnly.Parse(carnavalMonday));
        holidays["carnaval_terca"].Should().Be(DateOnly.Parse(carnavalTuesday));
        holidays["sexta_feira_da_paixao"].Should().Be(DateOnly.Parse(goodFriday));
        holidays["corpus_christi"].Should().Be(DateOnly.Parse(corpusChristi));
    }

    [Fact]
    public void For_FixedHolidays_HaveStableKeysPortugueseNamesAndDates()
    {
        // Act
        var holidays = NationalHolidays.For(2026);

        // Assert
        holidays.Should().HaveCount(13);
        holidays.Select(h => h.Key).Should().BeEquivalentTo(NationalHolidays.Keys);
        holidays.Select(h => h.Date).Should().BeInAscendingOrder();
        holidays.Select(h => (h.Key, h.Name, h.Date.ToString("MM-dd"))).Should().Contain(
        [
            ("confraternizacao_universal", "Confraternização Universal", "01-01"),
            ("tiradentes", "Tiradentes", "04-21"),
            ("dia_do_trabalho", "Dia do Trabalho", "05-01"),
            ("independencia", "Independência", "09-07"),
            ("nossa_senhora_aparecida", "Nossa Senhora Aparecida", "10-12"),
            ("finados", "Finados", "11-02"),
            ("proclamacao_da_republica", "Proclamação da República", "11-15"),
            ("consciencia_negra", "Dia Nacional de Zumbi e da Consciência Negra", "11-20"),
            ("natal", "Natal", "12-25"),
        ]);
        holidays.Single(h => h.Key == "sexta_feira_da_paixao").Name.Should().Be("Sexta-feira da Paixão");
        holidays.Single(h => h.Key == "corpus_christi").Name.Should().Be("Corpus Christi");
    }

    [Fact]
    public void Applicable_DependsOnWorksOnHolidaysAndTheDisabledKeys()
    {
        // Arrange
        var tiradentes = new DateOnly(2027, 4, 21);
        var ordinary = new DateOnly(2027, 4, 22);

        // Act
        var byDefault = NationalHolidays.Applicable(tiradentes, null);
        var noRowDefaults = NationalHolidays.Applicable(tiradentes, new ProviderHolidaySettings());
        var works = NationalHolidays.Applicable(tiradentes, new ProviderHolidaySettings { WorksOnHolidays = true });
        var disabled = NationalHolidays.Applicable(tiradentes, new ProviderHolidaySettings { DisabledHolidays = ["tiradentes"] });
        var otherDisabled = NationalHolidays.Applicable(tiradentes, new ProviderHolidaySettings { DisabledHolidays = ["natal"] });

        // Assert
        byDefault!.Name.Should().Be("Tiradentes");
        noRowDefaults!.Key.Should().Be("tiradentes");
        works.Should().BeNull();
        disabled.Should().BeNull();
        otherDisabled!.Key.Should().Be("tiradentes");
        NationalHolidays.Applicable(ordinary, null).Should().BeNull();
    }
}
