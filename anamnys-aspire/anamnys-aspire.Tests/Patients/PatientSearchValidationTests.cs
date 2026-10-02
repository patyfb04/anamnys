using Anamnys.Server.Patients;
using FluentAssertions;

namespace Anamnys.Tests.Patients;

public class PatientSearchValidationTests
{
    [Fact]
    public void Validate_DefaultRequest_HasNoErrors()
    {
        new PatientSearchRequest().Validate().Should().BeEmpty();
    }

    [Theory]
    [InlineData("lastVisit")]
    [InlineData("nextVisit")]
    public void Validate_ReversedRange_ReportsTheToField(string range)
    {
        // Arrange
        var from = new DateOnly(2026, 10, 2);
        var to = new DateOnly(2026, 10, 1);
        var request = range == "lastVisit"
            ? new PatientSearchRequest { LastVisitFrom = from, LastVisitTo = to }
            : new PatientSearchRequest { NextVisitFrom = from, NextVisitTo = to };

        // Act
        var errors = request.Validate();

        // Assert
        errors.Should().ContainKey($"{range}To");
    }

    [Fact]
    public void Validate_SameDayRange_IsAllowed()
    {
        var day = new DateOnly(2026, 10, 1);

        new PatientSearchRequest { LastVisitFrom = day, LastVisitTo = day }.Validate().Should().BeEmpty();
    }

    [Fact]
    public void Validate_UnknownEnumValues_ReportsEachField()
    {
        var errors = new PatientSearchRequest { NoteStatus = ["pending", "draft"], SortBy = "age", SortDir = "up" }.Validate();

        errors.Keys.Should().BeEquivalentTo(["noteStatus", "sortBy", "sortDir"]);
    }

    [Fact]
    public void Validate_TextOver200Chars_ReportsEachField()
    {
        var longText = new string('a', 201);

        var errors = new PatientSearchRequest { Search = longText, Name = longText, Email = longText }.Validate();

        errors.Keys.Should().BeEquivalentTo(["search", "name", "email"]);
    }

    [Theory]
    [InlineData(0, 25, "page")]
    [InlineData(1, 0, "pageSize")]
    [InlineData(1, 101, "pageSize")]
    public void Validate_PagingOutOfRange_ReportsTheField(int page, int pageSize, string key)
    {
        new PatientSearchRequest { Page = page, PageSize = pageSize }.Validate().Should().ContainKey(key);
    }
}
