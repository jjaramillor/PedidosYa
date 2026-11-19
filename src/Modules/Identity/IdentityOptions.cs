namespace Delivery.Modules.Identity;

public sealed class IdentityOptions
{
    public string JwtKey { get; set; } = "";
    public string OtpKey { get; set; } = "";
    public string Issuer { get; set; } = "delivery";
    public string Audience { get; set; } = "delivery-client";
    public string TermsVersion { get; set; } = "2026-10";
    public string PrivacyVersion { get; set; } = "2026-10";
    public string SmtpHost { get; set; } = "localhost";
    public int SmtpPort { get; set; } = 1025;
    public bool SmtpTls { get; set; } = true;
    public string SmtpUser { get; set; } = "";
    public string SmtpPassword { get; set; } = "";
    public string Sender { get; set; } = "no-reply@delivery.local";
}
