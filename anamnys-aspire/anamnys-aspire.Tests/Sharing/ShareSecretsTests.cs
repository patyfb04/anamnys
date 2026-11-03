using System.Buffers.Text;
using Anamnys.Server.Sharing;
using FluentAssertions;

namespace Anamnys.Tests.Sharing;

public class ShareSecretsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void NewToken_Is256BitsUrlSafeAndNeverRepeats()
    {
        // Act
        var tokens = Enumerable.Range(0, 100).Select(_ => ShareSecrets.NewToken()).ToList();

        // Assert
        tokens.Should().OnlyHaveUniqueItems();
        tokens.Should().OnlyContain(t => Base64Url.DecodeFromChars(t).Length * 8 >= 256);
        tokens.Should().OnlyContain(t => t.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_'));
    }

    [Fact]
    public void HashToken_IsTheHexSha256_AndNotTheToken()
    {
        // Act
        var hash = ShareSecrets.HashToken("abc");

        // Assert
        hash.Should().Be("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    }

    [Fact]
    public void NewPassword_HasAtLeast12CharactersOfTheUnambiguousAlphabet()
    {
        // Act
        var passwords = Enumerable.Range(0, 200).Select(_ => ShareSecrets.NewPassword()).ToList();

        // Assert
        passwords.Should().OnlyHaveUniqueItems();
        passwords.Should().OnlyContain(p => p.Length >= 12 && p.All(ShareSecrets.PasswordAlphabet.Contains));
        ShareSecrets.PasswordAlphabet.Should().NotContainAny("0", "O", "o", "1", "l", "I", "i");
    }

    [Fact]
    public async Task HashPasswordAsync_IsArgon2idAtTheOwaspCost_AndVerifiesOnlyTheRightPassword()
    {
        // Act
        var stored = await ShareSecrets.HashPasswordAsync("senha-certa", Ct);
        var other = await ShareSecrets.HashPasswordAsync("senha-certa", Ct);

        // Assert
        stored.Should().StartWith("$argon2id$v=19$m=19456,t=2,p=1$").And.NotContain("senha-certa").And.NotBe(other);
        (await ShareSecrets.VerifyPasswordAsync("senha-certa", stored, Ct)).Should().BeTrue();
        (await ShareSecrets.VerifyPasswordAsync("senha-errada", stored, Ct)).Should().BeFalse();
        (await ShareSecrets.VerifyPasswordAsync(new string('x', 100_000), stored, Ct)).Should().BeFalse();
        (await ShareSecrets.VerifyPasswordAsync("", stored, Ct)).Should().BeFalse();
    }
}
