using Anamnys.Server.Analysis;
using FluentAssertions;

namespace Anamnys.Tests.Analysis;

// design/specs/2026-10-29-theme-absence-design.md §6 "Pure".
public class ThemeAbsenceTests
{
    private static readonly DateOnly Day0 = new(2026, 3, 2);
    private static readonly DateOnly Today = new(2026, 10, 1);

    private static List<AbsenceSession> Sessions(int count) =>
        Enumerable.Range(0, count).Select(i => new AbsenceSession(Guid.NewGuid(), Day0.AddDays(7 * i))).ToList();

    private static AbsenceTerm Term(
        IReadOnlyList<AbsenceSession> sessions, string label, int[] at, Guid? parent = null, bool active = true) =>
        new(Guid.NewGuid(), $"code-{label}", label, parent, active,
            at.Select(i => new AbsenceRecord(sessions[i].NoteId, null, null, null)).ToList());

    [Fact]
    public void Detect_RecordedInThreeSessionsThenNone_IsAbsent_TwoIsNot()
    {
        // Arrange
        var s = Sessions(10);
        var three = Term(s, "Sono", [0, 1, 2]);
        var two = Term(s, "Luto", [0, 1]);

        // Act
        var result = ThemeAbsence.Detect(s, [three, two], 4, Today);

        // Assert
        result.Should().ContainSingle().Which.TermId.Should().Be(three.TermId);
    }

    [Fact]
    public void Detect_Boundary_AbsentInTheLastNButNotInTheLastNMinusOne()
    {
        // Arrange: 10 sessions, N = 4 -> the window is sessions 6..9; N = 3 -> 7..9.
        var s = Sessions(10);
        var outside = Term(s, "Fora", [0, 1, 5]);
        var inside = Term(s, "Dentro", [0, 1, 6]);

        // Act
        var four = ThemeAbsence.Detect(s, [outside, inside], 4, Today);
        var three = ThemeAbsence.Detect(s, [outside, inside], 3, Today);

        // Assert
        four.Select(i => i.TermId).Should().Equal(outside.TermId);
        three.Select(i => i.TermId).Should().BeEquivalentTo([outside.TermId, inside.TermId]);
    }

    [Fact]
    public void Detect_ParentWithARecentChild_IsNotFlagged_ChildAbsentAloneIs()
    {
        // Arrange
        var s = Sessions(10);
        var parent = Term(s, "Humor", [0, 1, 2]);
        var recentChild = Term(s, "Irritabilidade", [0, 1, 9], parent.TermId);
        var oldChild = Term(s, "Tristeza", [0, 1, 2], parent.TermId);
        var lonely = Term(s, "Pai", [0, 1, 2]);
        var lonelyChild = Term(s, "Filho", [], lonely.TermId);

        // Act
        var result = ThemeAbsence.Detect(s, [parent, recentChild, oldChild, lonely, lonelyChild], 4, Today);

        // Assert
        result.Select(i => i.TermId).Should().BeEquivalentTo([oldChild.TermId, lonely.TermId]);
    }

    [Fact]
    public void Detect_ParentIsAliveThroughADeepDescendant()
    {
        // Arrange
        var s = Sessions(10);
        var root = Term(s, "Raiz", [0, 1, 2]);
        var middle = Term(s, "Meio", [], root.TermId);
        var leaf = Term(s, "Folha", [8], middle.TermId);

        // Act
        var result = ThemeAbsence.Detect(s, [root, middle, leaf], 4, Today);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void Detect_SupersededOrRetiredTerms_AreNeverFlagged()
    {
        // Arrange
        var s = Sessions(10);
        var old = Term(s, "Antigo", [0, 1, 2], active: false);

        // Act
        var result = ThemeAbsence.Detect(s, [old], 4, Today);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void Detect_FewerThanSixSessions_FindsNothing()
    {
        // Arrange
        var s = Sessions(5);

        // Act
        var result = ThemeAbsence.Detect(s, [Term(s, "Sono", [0, 1, 2])], 2, Today);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void Detect_CountsDatesAndLastPassage()
    {
        // Arrange: recorded in sessions 1, 3 and 4 (three times in 4); N = 4 over 12 sessions.
        var s = Sessions(12);
        var excerpt = Guid.NewGuid();
        var term = new AbsenceTerm(Guid.NewGuid(), "sono", "Sono", null, true,
        [
            new AbsenceRecord(s[1].NoteId, null, null, null),
            new AbsenceRecord(s[3].NoteId, null, null, null),
            new AbsenceRecord(s[4].NoteId, null, null, null),
            new AbsenceRecord(s[4].NoteId, excerpt, "subjective", "Dormiu mal"),
            new AbsenceRecord(s[4].NoteId, null, null, null),
        ]);

        // Act
        var item = ThemeAbsence.Detect(s, [term], 4, Today).Should().ContainSingle().Subject;

        // Assert
        item.RecordedIn.Should().Be(3);
        item.SpanSessions.Should().Be(4);
        item.FirstOn.Should().Be(s[1].On);
        item.LastOn.Should().Be(s[4].On);
        item.SessionsSince.Should().Be(7);
        item.DaysSince.Should().Be(Today.DayNumber - s[4].On.DayNumber);
        item.LastOccurrence.Should().Be(new AbsenceLastOccurrence(s[4].NoteId, excerpt, "subjective", "Dormiu mal"));
    }

    [Fact]
    public void Detect_OrdersByLastRecordDate()
    {
        // Arrange
        var s = Sessions(12);
        var later = Term(s, "Aa", [0, 1, 5]);
        var earlier = Term(s, "Zz", [0, 1, 2]);

        // Act
        var result = ThemeAbsence.Detect(s, [later, earlier], 4, Today);

        // Assert
        result.Select(i => i.TermId).Should().Equal(earlier.TermId, later.TermId);
    }
}
