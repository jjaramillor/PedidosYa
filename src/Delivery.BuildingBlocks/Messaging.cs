using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Delivery.BuildingBlocks;

public sealed record IntegrationMessage(Guid Id, string Type, string Payload, string CorrelationId);
public interface IMessageHandler
{
    string MessageType { get; }
    string Consumer { get; }
    Task HandleAsync(IntegrationMessage message, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct);
}

public static class Outbox
{
    public static async Task EnqueueAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string type, object payload, string correlationId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO platform.outbox (id, type, payload, correlation_id)
            VALUES (@id, @type, @payload::jsonb, @correlation)
            """, connection, transaction);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("type", type);
        command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(payload));
        command.Parameters.AddWithValue("correlation", correlationId);
        await command.ExecuteNonQueryAsync(ct);
    }
}

// A row lock keeps dispatch exclusive across instances. Inbox and handler writes share this transaction.
// External transports (SMTP) are at-least-once: a crash after send can cause a repeated delivery.
public sealed class OutboxDispatcher(NpgsqlDataSource source, IEnumerable<IMessageHandler> handlers,
    ILogger<OutboxDispatcher> logger,IConfiguration configuration) : BackgroundService
{
    public async Task<bool> DispatchOneAsync(CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        IntegrationMessage message;
        await using (var command = new NpgsqlCommand("""
            SELECT id, type, payload::text, correlation_id FROM platform.outbox
            WHERE processed_at IS NULL AND attempts < 10 AND next_attempt_at <= now()
            ORDER BY created_at, id LIMIT 1 FOR UPDATE SKIP LOCKED
            """, connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return false;
            message = new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
        }
        using var activity = Telemetry.Activities.StartActivity("outbox.dispatch");
        activity?.SetTag("messaging.message.id", message.Id);
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = message.CorrelationId, ["MessageId"] = message.Id });
        await transaction.SaveAsync("handler", ct);
        try
        {
            var targets = handlers.Where(h => h.MessageType == message.Type).ToArray();
            if (targets.Length == 0) throw new InvalidOperationException("No handler registered for message type.");
            foreach (var handler in targets)
            {
                await using var inbox = new NpgsqlCommand("""
                    INSERT INTO platform.inbox (message_id, consumer)
                    VALUES (@id, @consumer) ON CONFLICT DO NOTHING
                    """, connection, transaction);
                inbox.Parameters.AddWithValue("id", message.Id);
                inbox.Parameters.AddWithValue("consumer", handler.Consumer);
                if (await inbox.ExecuteNonQueryAsync(ct) == 1)
                    await handler.HandleAsync(message, connection, transaction, ct);
            }
            await using var done = new NpgsqlCommand("""
                UPDATE platform.outbox SET processed_at = now(), payload = '{}'::jsonb, last_error = NULL WHERE id = @id
                """, connection, transaction);
            done.Parameters.AddWithValue("id", message.Id);
            await done.ExecuteNonQueryAsync(ct);
            Telemetry.MessagesProcessed.Add(1);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await transaction.RollbackAsync("handler", ct);
            // Store only the exception type: transport errors may contain addresses or secrets.
            await using var retry = new NpgsqlCommand("""
                UPDATE platform.outbox SET attempts = attempts + 1, last_error = @error,
                next_attempt_at = now() + make_interval(secs => LEAST(300, power(2, attempts + 1)::integer))
                WHERE id = @id
                """, connection, transaction);
            retry.Parameters.AddWithValue("id", message.Id);
            retry.Parameters.AddWithValue("error", ex.GetType().Name);
            await retry.ExecuteNonQueryAsync(ct);
            logger.LogWarning("Message processing failed ({ErrorType}); retry scheduled", ex.GetType().Name);
            Telemetry.MessagesFailed.Add(1);
        }
        await transaction.CommitAsync(ct);
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Test-host configuration can be supplied after service registration.
        if(!configuration.GetValue("Messaging:RunWorker",true))return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await DispatchOneAsync(stoppingToken))
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning("Outbox storage unavailable ({ErrorType})", ex.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}

