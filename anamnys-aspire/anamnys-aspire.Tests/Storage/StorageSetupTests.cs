using Anamnys.Server.Storage;
using FluentAssertions;

namespace Anamnys.Tests.Storage;

public class StorageSetupTests
{
    [Theory]
    [InlineData("")]
    [InlineData("http://storage:8333")]
    [InlineData("http://files.example.com")]
    [InlineData("files.example.com")]
    public void PublicUrlProblem_OutsideDevelopmentWithoutHttps_ReportsProblem(string publicUrl)
    {
        // Act
        var problem = StorageSetup.PublicUrlProblem(publicUrl, isDevelopment: false);

        // Assert
        problem.Should().Contain("storage-public-url");
    }

    [Fact]
    public void PublicUrlProblem_OutsideDevelopmentWithHttps_NoProblem()
    {
        // Act
        var problem = StorageSetup.PublicUrlProblem("https://files.example.com", isDevelopment: false);

        // Assert
        problem.Should().BeNull();
    }

    [Fact]
    public void PublicUrlProblem_InDevelopment_AcceptsLocalHttp()
    {
        // Act
        var problem = StorageSetup.PublicUrlProblem("http://localhost:54321", isDevelopment: true);

        // Assert
        problem.Should().BeNull();
    }
}
