using Delivery.BuildingBlocks;
using Npgsql;
namespace Delivery.Modules.Identity.Infrastructure;

public sealed class IdentityEventHandler(string type) : IMessageHandler
{
    public string MessageType => type;
    public string Consumer => "identity.event-audit.v1";
    public async Task HandleAsync(IntegrationMessage message, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO identity.event_receipts(event_id,event_type,payload) VALUES (@id,@type,@payload::jsonb) ON CONFLICT DO NOTHING
            """, connection, transaction);
        command.Parameters.AddWithValue("id", message.Id); command.Parameters.AddWithValue("type", message.Type);
        command.Parameters.AddWithValue("payload", message.Payload); await command.ExecuteNonQueryAsync(ct);
    }
}

