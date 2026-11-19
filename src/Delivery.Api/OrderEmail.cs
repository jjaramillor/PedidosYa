using System.Net;
using System.Net.Mail;
using System.Text.Json;
using Delivery.BuildingBlocks;
using Delivery.Contracts;
using Delivery.Modules.Identity;
using Delivery.Modules.Notifications;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Delivery.Api;
public sealed class SmtpOrderEmail(IIdentityDirectory identities,IOptions<IdentityOptions> options):IOrderEmailSender
{
    public async Task SendAsync(Guid user,string title,string body,CancellationToken ct)
    {
        var email=await identities.EmailAsync(user,ct);if(email is null)return;
        var settings=options.Value;
        using var client=new SmtpClient(settings.SmtpHost,settings.SmtpPort){EnableSsl=settings.SmtpTls};
        if(!string.IsNullOrEmpty(settings.SmtpUser))client.Credentials=new NetworkCredential(settings.SmtpUser,settings.SmtpPassword);
        using var message=new MailMessage(settings.Sender,email){Subject=title,Body=body};
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(30));
        await client.SendMailAsync(message,deadline.Token);
    }
}
public sealed class OrderEmailHandler(IOrderEmailSender sender):IMessageHandler
{
    public string MessageType=>"notifications.email-requested.v1";
    public string Consumer=>"notifications.smtp.v1";
    public async Task HandleAsync(IntegrationMessage message,NpgsqlConnection connection,NpgsqlTransaction transaction,CancellationToken ct)
    {
        using var document=JsonDocument.Parse(message.Payload);var payload=document.RootElement;
        await sender.SendAsync(payload.GetProperty("UserId").GetGuid(),payload.GetProperty("Title").GetString()!,payload.GetProperty("Body").GetString()!,ct);
    }
}
