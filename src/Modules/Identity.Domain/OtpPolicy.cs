namespace Delivery.Modules.Identity.Domain;

public static class OtpPolicy
{
    public const int MaxAttempts = 5;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(60);
    public static bool CanVerify(DateTimeOffset expiresAt, int attempts, bool consumed, DateTimeOffset now)
        => !consumed && attempts < MaxAttempts && expiresAt > now;
}

