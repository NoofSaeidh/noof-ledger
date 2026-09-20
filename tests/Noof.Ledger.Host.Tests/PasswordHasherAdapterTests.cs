using AwesomeAssertions;
using Noof.Ledger.Application.Auth;
using Noof.Ledger.Domain;
using Noof.Ledger.Host.Auth;

namespace Noof.Ledger.Host.Tests;

public class PasswordHasherAdapterTests
{
    static readonly AppUser User = new()
    {
        Id = Guid.NewGuid(),
        Username = "noof",
        PasswordHash = string.Empty,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void A_hash_verifies_against_its_own_password()
    {
        var hasher = new PasswordHasherAdapter();
        var hash = hasher.Hash(User, "correct horse battery staple");

        hasher.Verify(User, hash, "correct horse battery staple").Should().Be(PasswordVerifyResult.Success);
    }

    [Fact]
    public void A_wrong_password_fails()
    {
        var hasher = new PasswordHasherAdapter();
        var hash = hasher.Hash(User, "correct horse battery staple");

        hasher.Verify(User, hash, "wrong").Should().Be(PasswordVerifyResult.Failed);
    }

    [Fact]
    public void The_same_password_hashes_differently_every_time()
    {
        var hasher = new PasswordHasherAdapter();

        hasher.Hash(User, "same").Should().NotBe(hasher.Hash(User, "same"));
    }

    [Fact]
    public void A_malformed_hash_fails_rather_than_throwing()
    {
        var hasher = new PasswordHasherAdapter();

        hasher.Verify(User, "not-a-hash", "whatever").Should().Be(PasswordVerifyResult.Failed);
    }
}
