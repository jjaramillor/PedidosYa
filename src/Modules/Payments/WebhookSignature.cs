using System.Security.Cryptography;
using System.Text;

namespace Delivery.Modules.Payments;

public static class WebhookSignature
{
    public static bool Valid(string secret, string dataId, string requestId, string signature, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(dataId) || string.IsNullOrWhiteSpace(requestId)
            || dataId.Length > 100 || requestId.Length > 200 || signature.Length > 500) return false;
        string? timestamp = null, digest = null;
        foreach (var part in signature.Split(','))
        {
            var pair = part.Trim().Split('=', 2);
            if (pair.Length != 2) return false;
            if (pair[0] == "ts") { if (timestamp is not null) return false; timestamp = pair[1]; }
            if (pair[0] == "v1") { if (digest is not null) return false; digest = pair[1]; }
        }
        if (!long.TryParse(timestamp, out var ticks) || digest?.Length != 64) return false;
        DateTimeOffset signedAt;
        try { signedAt = ticks > 100000000000 ? DateTimeOffset.FromUnixTimeMilliseconds(ticks) : DateTimeOffset.FromUnixTimeSeconds(ticks); }
        catch (ArgumentOutOfRangeException) { return false; }
        if ((now - signedAt).Duration() > TimeSpan.FromMinutes(10)) return false;
        var manifest = $"id:{dataId.ToLowerInvariant()};request-id:{requestId};ts:{timestamp};";
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(manifest));
        try { return CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(digest)); }
        catch (FormatException) { return false; }
    }
}
