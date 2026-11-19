using System.Net;
using System.Net.Mail;
using System.Text.Json;
using Delivery.BuildingBlocks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Delivery.Modules.Identity.Infrastructure;

public interface IOtpSender
{
    Task SendAsync(string email, string code, CancellationToken ct);
}
public sealed class SmtpOtpSender(IOptions<IdentityOptions> options) : IOtpSender
{
    public async Task SendAsync(string email, string code, CancellationToken ct)
    {
        var settings = options.Value;
        using var client = new SmtpClient(settings.SmtpHost, settings.SmtpPort) { EnableSsl = settings.SmtpTls };
        if (!string.IsNullOrEmpty(settings.SmtpUser))
            client.Credentials = new NetworkCredential(settings.SmtpUser, settings.SmtpPassword);
        using var message = new MailMessage(settings.Sender, email)
        {
            Subject = "Tu código de acceso",
            Body = $"Tu código de acceso es {code}. Vence en cinco minutos. Si no lo solicitaste, ignora este correo."
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await client.SendMailAsync(message, timeout.Token);
    }
}
public sealed class OtpRequestedHandler(IDataProtectionProvider protection, IOtpSender sender, TimeProvider clock) : IMessageHandler
{
    public string MessageType => "identity.otp-requested.v1";
    public string Consumer => "identity.smtp.v1";
    public async Task HandleAsync(IntegrationMessage message, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        var delivery = JsonSerializer.Deserialize<OtpDelivery>(message.Payload)!;
        if (delivery.ExpiresAt <= clock.GetUtcNow()) return;
        await using var check = new NpgsqlCommand("SELECT consumed_at IS NULL FROM identity.otp_challenges WHERE id=@id", connection, transaction);
        check.Parameters.AddWithValue("id", delivery.ChallengeId);
        if (await check.ExecuteScalarAsync(ct) is not true) return;
        var mail = JsonSerializer.Deserialize<OtpMail>(protection.CreateProtector("Delivery.Identity.Otp.v1").Unprotect(delivery.ProtectedContent))!;
        await sender.SendAsync(mail.Email, mail.Code, ct);
    }
}
public sealed class UserRegisteredHandler : IMessageHandler
{
    public string MessageType => "identity.user-registered.v1";
    public string Consumer => "identity.registration-audit.v1";
    public async Task HandleAsync(IntegrationMessage message, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        var user = JsonSerializer.Deserialize<UserRegistered>(message.Payload)!;
        await using var command = new NpgsqlCommand("""
            INSERT INTO identity.registration_audit(event_id,user_id) VALUES (@event,@user) ON CONFLICT DO NOTHING
            """, connection, transaction);
        command.Parameters.AddWithValue("event", message.Id);
        command.Parameters.AddWithValue("user", user.UserId);
        await command.ExecuteNonQueryAsync(ct);
    }
}
