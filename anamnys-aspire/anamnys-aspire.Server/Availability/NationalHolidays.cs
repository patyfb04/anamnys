using Anamnys.Server.Data.Entities;

namespace Anamnys.Server.Availability;

public sealed record NationalHoliday(string Key, string Name, DateOnly Date);

// Brazilian national holidays (design/specs/2026-11-09-availability-design.md §3): the fixed dates and the ones moved by
// Easter, computed here with no external service. Keys are stable (they are stored in DisabledHolidays); names are the
// Portuguese ones shown to the provider.
public static class NationalHolidays
{
    public static readonly string[] Keys =
    [
        "confraternizacao_universal", "carnaval_segunda", "carnaval_terca", "sexta_feira_da_paixao", "tiradentes",
        "dia_do_trabalho", "corpus_christi", "independencia", "nossa_senhora_aparecida", "finados",
        "proclamacao_da_republica", "consciencia_negra", "natal",
    ];

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, IReadOnlyList<NationalHoliday>> ByYear = new();

    // The holidays of a year, in date order (computed once per year).
    public static IReadOnlyList<NationalHoliday> For(int year) => ByYear.GetOrAdd(year, Compute);

    private static IReadOnlyList<NationalHoliday> Compute(int year)
    {
        var easter = Easter(year);
        NationalHoliday[] all =
        [
            new("confraternizacao_universal", "Confraternização Universal", new DateOnly(year, 1, 1)),
            new("carnaval_segunda", "Carnaval (segunda-feira)", easter.AddDays(-48)),
            new("carnaval_terca", "Carnaval (terça-feira)", easter.AddDays(-47)),
            new("sexta_feira_da_paixao", "Sexta-feira da Paixão", easter.AddDays(-2)),
            new("tiradentes", "Tiradentes", new DateOnly(year, 4, 21)),
            new("dia_do_trabalho", "Dia do Trabalho", new DateOnly(year, 5, 1)),
            new("corpus_christi", "Corpus Christi", easter.AddDays(60)),
            new("independencia", "Independência", new DateOnly(year, 9, 7)),
            new("nossa_senhora_aparecida", "Nossa Senhora Aparecida", new DateOnly(year, 10, 12)),
            new("finados", "Finados", new DateOnly(year, 11, 2)),
            new("proclamacao_da_republica", "Proclamação da República", new DateOnly(year, 11, 15)),
            new("consciencia_negra", "Dia Nacional de Zumbi e da Consciência Negra", new DateOnly(year, 11, 20)),
            new("natal", "Natal", new DateOnly(year, 12, 25)),
        ];
        return [.. all.OrderBy(h => h.Date)];
    }

    // The holiday that applies to the provider on a date: none when they work on holidays or switched that one off.
    public static NationalHoliday? Applicable(DateOnly date, ProviderHolidaySettings? settings)
    {
        if (settings?.WorksOnHolidays == true)
        {
            return null;
        }
        var holiday = For(date.Year).FirstOrDefault(h => h.Date == date);
        return holiday is not null && settings?.DisabledHolidays.Contains(holiday.Key) == true ? null : holiday;
    }

    // Easter Sunday, anonymous Gregorian algorithm (Meeus/Jones/Butcher).
    public static DateOnly Easter(int year)
    {
        var a = year % 19;
        var b = year / 100;
        var c = year % 100;
        var d = b / 4;
        var e = b % 4;
        var f = (b + 8) / 25;
        var g = (b - f + 1) / 3;
        var h = (19 * a + b - d - g + 15) % 30;
        var i = c / 4;
        var k = c % 4;
        var l = (32 + 2 * e + 2 * i - h - k) % 7;
        var m = (a + 11 * h + 22 * l) / 451;
        var month = (h + l - 7 * m + 114) / 31;
        var day = (h + l - 7 * m + 114) % 31 + 1;
        return new DateOnly(year, month, day);
    }
}
