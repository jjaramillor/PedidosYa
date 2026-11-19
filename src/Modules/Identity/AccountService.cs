using Delivery.BuildingBlocks;
using Delivery.Modules.Identity.Infrastructure;
using Microsoft.Extensions.Options;
using Npgsql;
namespace Delivery.Modules.Identity;

public sealed record ConsentChange(string Kind, string Version, bool Accepted);
public sealed record ConsentRecord(string Kind, string Version, bool Accepted, DateTimeOffset RecordedAt);
public sealed record AccountStatusChange(string Status);
public sealed record AccountNameChange(string DisplayName);
public sealed record PolicyVersions(string TermsVersion, string PrivacyVersion, string MarketingVersion);

public sealed class AccountService(NpgsqlDataSource source, IOptions<IdentityOptions> options, TimeProvider clock)
{
    public PolicyVersions Policies() => new(options.Value.TermsVersion, options.Value.PrivacyVersion, options.Value.PrivacyVersion);
    public async Task<object[]> UsersAsync(string? search, CancellationToken ct)
    {
        if ((search?.Length ?? 0) > 100) throw new ApiException(400, "invalid_search", "Búsqueda demasiado larga.");
        await using var db = await source.OpenConnectionAsync(ct);
        await using var query = new NpgsqlCommand("SELECT id,email,display_name,status FROM identity.users WHERE email ILIKE @search OR display_name ILIKE @search ORDER BY display_name LIMIT 200", db);
        query.Parameters.AddWithValue("search", "%" + (search ?? "") + "%");
        await using var reader = await query.ExecuteReaderAsync(ct);
        var users = new List<object>();
        while (await reader.ReadAsync(ct)) users.Add(new { Id=reader.GetGuid(0), Email=reader.GetString(1), DisplayName=reader.GetString(2), Status=reader.GetString(3) });
        return users.ToArray();
    }
    public async Task<IReadOnlyList<ConsentRecord>> ConsentsAsync(Guid user, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT kind,version,accepted,recorded_at FROM identity.consent_history WHERE user_id=@id ORDER BY sequence DESC LIMIT 100
            """, connection);
        command.Parameters.AddWithValue("id", user); await using var reader = await command.ExecuteReaderAsync(ct); var result = new List<ConsentRecord>();
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), new(reader.GetDateTime(3))));
        return result;
    }
    public async Task SetConsentAsync(Guid user, ConsentChange request, string correlation, CancellationToken ct)
    {
        var expected = request.Kind switch { "terms" => options.Value.TermsVersion, "privacy" or "marketing" => options.Value.PrivacyVersion, _ => null };
        if (expected is null || request.Version != expected) throw new ApiException(400, "invalid_consent", "Tipo o versión de consentimiento inválidos.");
        await using var connection = await source.OpenConnectionAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        await IdentityStore.AdminGateAsync(connection, tx, ct);
        await EmailGateAsync(connection, tx, user, ct);
        await IdentityStore.LockUserAsync(connection, tx, user, ct);
        var unchanged = false;
        await using (var query = new NpgsqlCommand("SELECT version,accepted FROM identity.consent_history WHERE user_id=@id AND kind=@kind ORDER BY sequence DESC LIMIT 1", connection, tx))
        {
            query.Parameters.AddWithValue("id", user); query.Parameters.AddWithValue("kind", request.Kind);
            await using var reader = await query.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct)) unchanged = reader.GetString(0) == request.Version && reader.GetBoolean(1) == request.Accepted;
        }
        if (!unchanged)
        {
            var now = clock.GetUtcNow();
            await IdentityStore.ConsentAsync(connection, tx, user, request.Kind, request.Version, request.Accepted, now, ct);
            await IdentityStore.AuditAsync(connection, tx, user, user, "consent.changed", request, correlation, now, ct);
            await Outbox.EnqueueAsync(connection, tx, "identity.consent-changed.v1", new { UserId = user, request.Kind, request.Version, request.Accepted }, correlation, ct);
            if (!request.Accepted && request.Kind is "terms" or "privacy")
            {
                await RoleService.ProtectAdminAsync(connection, tx, user, ct);
                await SetStatus(connection, tx, user, "deactivated", now, ct);
                await IdentityStore.AuditAsync(connection, tx, user, user, "account.status-changed", new { Status = "deactivated" }, correlation, now, ct);
                await Outbox.EnqueueAsync(connection, tx, "identity.account-status-changed.v1", new { UserId = user, Status = "deactivated" }, correlation, ct);
            }
        }
        await tx.CommitAsync(ct);
    }
    public async Task SetStatusAsync(Guid actor, Guid user, string status, bool administrative, string correlation, CancellationToken ct)
    {
        if (status is not ("active" or "blocked" or "deactivated") || (!administrative && status != "deactivated"))
            throw new ApiException(400, "invalid_status", "Estado de cuenta inválido.");
        await using var connection = await source.OpenConnectionAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        await IdentityStore.AdminGateAsync(connection, tx, ct);
        if (administrative) await RoleService.RequireAsync(connection, tx, actor, "identity.users.manage", ct);
        else if (actor != user) throw new ApiException(403, "permission_denied", "No puedes cambiar otra cuenta.");
        await EmailGateAsync(connection, tx, user, ct);
        if (!administrative) await IdentityStore.LockUserAsync(connection, tx, user, ct);
        string previous;
        await using (var query = new NpgsqlCommand("SELECT status FROM identity.users WHERE id=@id FOR UPDATE", connection, tx))
        { query.Parameters.AddWithValue("id", user); previous = await query.ExecuteScalarAsync(ct) as string ?? throw new ApiException(404, "user_not_found", "La cuenta no existe."); }
        if (previous == "closed") throw new ApiException(409, "account_closed", "Una cuenta cerrada no puede reactivarse.");
        if (previous != status)
        {
            if (status != "active") await RoleService.ProtectAdminAsync(connection, tx, user, ct);
            var now = clock.GetUtcNow(); await SetStatus(connection, tx, user, status, now, ct);
            await IdentityStore.AuditAsync(connection, tx, actor, user, "account.status-changed", new { Status = status }, correlation, now, ct);
            await Outbox.EnqueueAsync(connection, tx, "identity.account-status-changed.v1", new { UserId = user, Status = status }, correlation, ct);
        }
        await tx.CommitAsync(ct);
    }
    public async Task ChangeNameAsync(Guid user, AccountNameChange request, string correlation, CancellationToken ct)
    {
        var name = request.DisplayName?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100) throw new ApiException(400, "invalid_name", "Nombre inválido.");
        await using var connection = await source.OpenConnectionAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        var account = await IdentityStore.LockUserAsync(connection, tx, user, ct);
        if (account.DisplayName != name)
        {
            await using var command = new NpgsqlCommand("UPDATE identity.users SET display_name=@name,updated_at=@now WHERE id=@id", connection, tx);
            command.Parameters.AddWithValue("name", name); command.Parameters.AddWithValue("now", clock.GetUtcNow()); command.Parameters.AddWithValue("id", user);
            await command.ExecuteNonQueryAsync(ct);
            await IdentityStore.AuditAsync(connection, tx, user, user, "account.name-changed", new { }, correlation, clock.GetUtcNow(), ct);
        }
        await tx.CommitAsync(ct);
    }
    public async Task CloseAsync(Guid user, string correlation, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        await IdentityStore.AdminGateAsync(connection, tx, ct);
        await EmailGateAsync(connection, tx, user, ct);
        var account = await IdentityStore.LockUserAsync(connection, tx, user, ct);
        await RoleService.ProtectAdminAsync(connection, tx, user, ct);
        var now = clock.GetUtcNow();
        await using var command = new NpgsqlCommand("""
            UPDATE platform.outbox SET payload='{}',processed_at=@now
              WHERE type='identity.otp-requested.v1' AND payload->>'ChallengeId' IN (SELECT id::text FROM identity.otp_challenges WHERE email=@email);
            DELETE FROM identity.otp_challenges WHERE email=@email;
            DELETE FROM identity.external_identities WHERE user_id=@id;
            DELETE FROM identity.user_roles WHERE user_id=@id;
            UPDATE identity.users SET status='closed',email=@anonymous,display_name='Cuenta eliminada',updated_at=@now WHERE id=@id;
            UPDATE identity.sessions SET revoked_at=@now,revoke_reason='account_closed' WHERE user_id=@id AND revoked_at IS NULL
            """, connection, tx);
        command.Parameters.AddWithValue("email", account.Email); command.Parameters.AddWithValue("id", user);
        command.Parameters.AddWithValue("anonymous", user.ToString("N") + "@deleted.invalid"); command.Parameters.AddWithValue("now", now);
        await command.ExecuteNonQueryAsync(ct);
        await IdentityStore.AuditAsync(connection, tx, user, user, "account.closed", new { }, correlation, now, ct);
        await Outbox.EnqueueAsync(connection, tx, "identity.account-closed.v1", new { UserId = user }, correlation, ct);
        await tx.CommitAsync(ct);
    }
    private static async Task SetStatus(NpgsqlConnection connection, NpgsqlTransaction tx, Guid user, string status, DateTimeOffset now, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE identity.users SET status=@status,updated_at=@now WHERE id=@id;
            UPDATE identity.sessions SET revoked_at=@now,revoke_reason=@status WHERE user_id=@id AND revoked_at IS NULL AND @status<>'active';
            UPDATE identity.otp_challenges SET consumed_at=@now
              WHERE email=(SELECT email FROM identity.users WHERE id=@id) AND consumed_at IS NULL AND @status<>'active'
            """, connection, tx);
        command.Parameters.AddWithValue("status", status); command.Parameters.AddWithValue("now", now); command.Parameters.AddWithValue("id", user);
        await command.ExecuteNonQueryAsync(ct);
    }
    private static async Task EmailGateAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid user, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(email,0)) FROM identity.users WHERE id=@id", connection, tx);
        command.Parameters.AddWithValue("id", user);
        await command.ExecuteNonQueryAsync(ct);
    }
}
