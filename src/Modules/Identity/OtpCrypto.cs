using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Delivery.Modules.Identity;

public sealed class OtpCrypto(IOptions<IdentityOptions> options)
{
    public string Generate() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
    public byte[] Hash(Guid challengeId, string code)
        => HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.Value.OtpKey),
            Encoding.UTF8.GetBytes($"{challengeId:N}:{code}"));
    public bool Matches(Guid id, string code, byte[] expected)
        => CryptographicOperations.FixedTimeEquals(Hash(id, code), expected);
}

