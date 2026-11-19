using Delivery.Modules.Identity;
using Delivery.Modules.Identity.Domain;
using Microsoft.Extensions.Options;
namespace Delivery.Tests;

public sealed class OtpPolicyTests
{
    [Theory]
    [InlineData(0, false, 1, true)]
    [InlineData(5, false, 1, false)]
    [InlineData(0, true, 1, false)]
    [InlineData(0, false, 0, false)]
    [InlineData(0, false, -1, false)]
    public void Verification_requires_unused_unexpired_challenge_and_remaining_attempts(int attempts, bool consumed, int minutes, bool valid)
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(valid, OtpPolicy.CanVerify(now.AddMinutes(minutes), attempts, consumed, now));
    }

    [Fact]
    public void Hash_is_bound_to_the_challenge_and_code()
    {
        var crypto = new OtpCrypto(Options.Create(new IdentityOptions { OtpKey = new string('x', 32) }));
        var id = Guid.NewGuid();
        var hash = crypto.Hash(id, "123456");
        Assert.True(crypto.Matches(id, "123456", hash));
        Assert.False(crypto.Matches(id, "654321", hash));
        Assert.False(crypto.Matches(Guid.NewGuid(), "123456", hash));
    }
}

