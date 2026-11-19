using System.Text.Json;
using Delivery.BuildingBlocks;
using Npgsql;

namespace Delivery.Modules.Identity.Infrastructure;

internal static class IdentityStore
{
    public static async Task<AuthenticatedUser> LockUserAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid id, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT id,email,display_name,status FROM identity.users WHERE id=@id FOR UPDATE", connection, tx);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new ApiException(404, "user_not_found", "La cuenta no existe.");
        if (reader.GetString(3) != "active") throw new ApiException(403, "account_inactive", "La cuenta no está activa.");
        return new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2));
    }
    public static async Task AdminGateAsync(NpgsqlConnection connection, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(74910323)", connection, tx);
        await command.ExecuteNonQueryAsync(ct);
    }
    public static async Task AuditAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid? actor, Guid? subject,
        string action, object data, string correlation, DateTimeOffset now, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO identity.security_audit(id,actor_id,subject_id,action,data,correlation_id,occurred_at)
            VALUES (@id,@actor,@subject,@action,@data::jsonb,@correlation,@now)
            """, connection, tx);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.Add(new NpgsqlParameter("actor", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)actor ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("subject", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)subject ?? DBNull.Value });
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("data", JsonSerializer.Serialize(data));
        command.Parameters.AddWithValue("correlation", correlation);
        command.Parameters.AddWithValue("now", now);
        await command.ExecuteNonQueryAsync(ct);
    }
    public static async Task ConsentAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid user, string kind, string version,
        bool accepted, DateTimeOffset now, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO identity.consent_history(id,user_id,kind,version,accepted,recorded_at)
            VALUES (@id,@user,@kind,@version,@accepted,@now)
            """, connection, tx);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("user", user);
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.AddWithValue("version", version);
        command.Parameters.AddWithValue("accepted", accepted);
        command.Parameters.AddWithValue("now", now);
        await command.ExecuteNonQueryAsync(ct);
    }
}

