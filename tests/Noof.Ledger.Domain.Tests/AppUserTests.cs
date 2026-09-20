using AwesomeAssertions;

namespace Noof.Ledger.Domain.Tests;

public class AppUserTests
{
    [Fact]
    public void Round_trips_its_properties()
    {
        var created = DateTimeOffset.UtcNow;
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            Username = "noof",
            PasswordHash = "hash",
            CreatedAt = created,
        };

        user.Username.Should().Be("noof");
        user.PasswordHash.Should().Be("hash");
        user.CreatedAt.Should().Be(created);
    }

    [Fact]
    public void Password_hash_can_be_replaced_without_rebuilding_the_user()
    {
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            Username = "noof",
            PasswordHash = "old",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        user.PasswordHash = "new";

        user.PasswordHash.Should().Be("new");
    }
}
