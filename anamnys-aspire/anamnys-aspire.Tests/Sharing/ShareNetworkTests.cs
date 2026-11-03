using System.Net;
using Anamnys.Server.ExternalDocuments;
using Anamnys.Server.Sharing;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace Anamnys.Tests.Sharing;

public class ShareNetworkTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void PartitionKey_TwoAddressesOfOneIpv6Slash64_ShareABucket()
    {
        // Act
        var first = ShareNetwork.PartitionKey(IPAddress.Parse("2001:db8:1:2:aaaa::1"));
        var second = ShareNetwork.PartitionKey(IPAddress.Parse("2001:db8:1:2:ffff:ffff:ffff:ffff"));
        var other = ShareNetwork.PartitionKey(IPAddress.Parse("2001:db8:1:3::1"));

        // Assert
        first.Should().Be(second).And.Be("20010db800010002/64");
        other.Should().NotBe(first);
    }

    [Fact]
    public void PartitionKey_Ipv4AndItsMappedForm_ShareABucket_AndNoAddressIsUnknown()
    {
        // Act
        var plain = ShareNetwork.PartitionKey(IPAddress.Parse("203.0.113.9"));
        var mapped = ShareNetwork.PartitionKey(IPAddress.Parse("::ffff:203.0.113.9"));
        var none = ShareNetwork.PartitionKey(null);

        // Assert
        plain.Should().Be("203.0.113.9").And.Be(mapped);
        none.Should().Be("unknown");
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    [Fact]
    public void KnownProxies_NotConfigured_IsEmpty_SoForwardedHeadersAreNotTrusted()
    {
        // Act
        var proxies = ShareNetwork.KnownProxies(Config());

        // Assert
        proxies.Should().BeEmpty();
    }

    [Fact]
    public void KnownProxies_ParsesTheListAndRefusesAnythingElse()
    {
        // Arrange
        var good = Config(("ForwardedHeaders:KnownProxies:0", "10.0.0.5"), ("ForwardedHeaders:KnownProxies:1", "fd00::1"));
        var bad = Config(("ForwardedHeaders:KnownProxies:0", "proxy.internal"));

        // Act
        var proxies = ShareNetwork.KnownProxies(good);
        var act = () => ShareNetwork.KnownProxies(bad);

        // Assert
        proxies.Should().Equal(IPAddress.Parse("10.0.0.5"), IPAddress.Parse("fd00::1"));
        act.Should().Throw<InvalidOperationException>().WithMessage("*KnownProxies*");
    }

    [Theory]
    [InlineData(true, null, true)]
    [InlineData(false, "https://app.example.test/patient", true)]
    [InlineData(false, null, false)]
    [InlineData(false, "", false)]
    [InlineData(false, "http://app.example.test/patient", false)]
    [InlineData(false, "/patient", false)]
    public void RequirePortalUrl_OutsideDevelopment_WantsAnAbsoluteHttpsUrl(bool development, string? url, bool starts)
    {
        // Act
        var act = () => ShareNetwork.RequirePortalUrl(development, url);

        // Assert
        if (starts)
        {
            act.Should().NotThrow();
        }
        else
        {
            act.Should().Throw<InvalidOperationException>().WithMessage("*PatientPortal:BaseUrl*");
        }
    }

    [Fact]
    public async Task WarmAsync_PreparesTheDummyHash()
    {
        // Act
        await ShareSecrets.WarmAsync();

        // Assert
        ShareSecrets.IsDummyReady.Should().BeTrue();
    }

    [Fact]
    public async Task ShareAttemptLimiter_DefaultBound_RefusesPastPermitsPlusQueue()
    {
        // Arrange
        using var limiter = new ShareAttemptLimiter();
        var taken = Enumerable.Range(0, ShareAttemptLimiter.DefaultPermits + ShareAttemptLimiter.DefaultQueue)
            .Select(_ => limiter.AcquireAsync(Ct).AsTask()).ToList();

        // Act
        using var refused = await limiter.AcquireAsync(Ct);

        // Assert
        refused.IsAcquired.Should().BeFalse();
        foreach (var lease in taken.Where(t => t.IsCompletedSuccessfully))
        {
            lease.Result.Dispose();
        }
        await Task.WhenAll(taken);
    }

    [Theory]
    [InlineData("%PDF-1.7", "application/pdf")]
    [InlineData("texto", null)]
    public void MimeTypeOf_ReadsTheFirstBytes(string content, string? expected)
    {
        // Act
        var mime = FileTypeSniffer.MimeTypeOf(System.Text.Encoding.ASCII.GetBytes(content));

        // Assert
        mime.Should().Be(expected);
    }
}
