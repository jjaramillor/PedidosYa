using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Delivery.BuildingBlocks;
using Delivery.Modules.Identity.Infrastructure;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace Delivery.Modules.Identity;

public sealed record RefreshSession(string RefreshToken);
public sealed record SessionInfo(Guid Id, string DeviceName, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt);
public sealed record AccessContext(AuthenticatedUser User, string[] Roles, string[] Permissions);

public sealed class SessionService(NpgsqlDataSource source, IOptions<IdentityOptions> options, TimeProvider clock)
{
    internal async Task<AuthSession> CreateAsync(NpgsqlConnection connection, NpgsqlTransaction tx, AuthenticatedUser user,
        string device, string correlation, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var id = Guid.CreateVersion7();
        await using var insert = new NpgsqlCommand("""
            INSERT INTO identity.sessions(id,user_id,created_at,expires_at,device_name,last_used_at)
            VALUES (@id,@user,@now,@expires,@device,@now)
            """, connection, tx);
        insert.Parameters.AddWithValue("id", id); insert.Parameters.AddWithValue("user", user.Id);
        insert.Parameters.AddWithValue("now", now); insert.Parameters.AddWithValue("expires", now.AddDays(30));
        insert.Parameters.AddWithValue("device", ValidateDevice(device));
        await insert.ExecuteNonQueryAsync(ct);
        var refresh = await NewRefreshAsync(connection, tx, id, now.AddDays(30), ct);
        await IdentityStore.AuditAsync(connection, tx, user.Id, user.Id, "session.created", new { SessionId = id }, correlation, now, ct);
        return Issue(user, id, refresh, now);
    }

    public async Task<AuthSession> RefreshAsync(RefreshSession request, string correlation, CancellationToken ct)
    {
        var hash = HashRefresh(request.RefreshToken);
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        Guid sessionId, userId, tokenId;
        await using (var lookup = new NpgsqlCommand("""
            SELECT r.id,s.id,s.user_id FROM identity.refresh_tokens r JOIN identity.sessions s ON s.id=r.session_id WHERE r.token_hash=@hash
            """, connection, tx))
        {
            lookup.Parameters.AddWithValue("hash", hash);
            await using var reader = await lookup.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw InvalidRefresh();
            tokenId = reader.GetGuid(0); sessionId = reader.GetGuid(1); userId = reader.GetGuid(2);
        }
        var user = await IdentityStore.LockUserAsync(connection, tx, userId, ct);
        var now = clock.GetUtcNow();
        DateTimeOffset expiry; bool used;
        await using (var query = new NpgsqlCommand("""
            SELECT s.expires_at,s.revoked_at,r.used_at,r.expires_at
            FROM identity.sessions s JOIN identity.refresh_tokens r ON r.session_id=s.id
            WHERE s.id=@session AND r.id=@token FOR UPDATE OF s,r
            """, connection, tx))
        {
            query.Parameters.AddWithValue("session", sessionId); query.Parameters.AddWithValue("token", tokenId);
            await using var reader = await query.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            expiry = new(reader.GetDateTime(0));
            if (!reader.IsDBNull(1) || expiry <= now || new DateTimeOffset(reader.GetDateTime(3)) <= now) throw InvalidRefresh();
            used = !reader.IsDBNull(2);
        }
        if (used)
        {
            await RevokeAsync(connection, tx, sessionId, "refresh_reuse", now, ct);
            await IdentityStore.AuditAsync(connection, tx, userId, userId, "session.refresh-reuse", new { SessionId = sessionId }, correlation, now, ct);
            await tx.CommitAsync(ct);
            throw InvalidRefresh();
        }
        await using (var consume = new NpgsqlCommand("UPDATE identity.refresh_tokens SET used_at=@now WHERE id=@id", connection, tx))
        {
            consume.Parameters.AddWithValue("now", now); consume.Parameters.AddWithValue("id", tokenId);
            await consume.ExecuteNonQueryAsync(ct);
        }
        var refresh = await NewRefreshAsync(connection, tx, sessionId, expiry, ct);
        await using (var touch = new NpgsqlCommand("UPDATE identity.sessions SET last_used_at=@now WHERE id=@id", connection, tx))
        {
            touch.Parameters.AddWithValue("now", now); touch.Parameters.AddWithValue("id", sessionId); await touch.ExecuteNonQueryAsync(ct);
        }
        var result = Issue(user, sessionId, refresh, now);
        await IdentityStore.AuditAsync(connection, tx, userId, userId, "session.refreshed", new { SessionId = sessionId }, correlation, now, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<AccessContext?> ValidateAsync(Guid userId, Guid sessionId, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        AuthenticatedUser user;
        await using (var command = new NpgsqlCommand("""
            SELECT u.id,u.email,u.display_name FROM identity.users u JOIN identity.sessions s ON s.user_id=u.id
            WHERE u.id=@user AND s.id=@session AND u.status='active' AND s.revoked_at IS NULL AND s.expires_at>@now
            """, connection))
        {
            command.Parameters.AddWithValue("user", userId); command.Parameters.AddWithValue("session", sessionId);
            command.Parameters.AddWithValue("now", clock.GetUtcNow());
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            user = new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2));
        }
        var roles = new List<string>(); var permissions = new HashSet<string>();
        await using (var command = new NpgsqlCommand("""
            SELECT ur.role_id,rp.permission_id FROM identity.user_roles ur LEFT JOIN identity.role_permissions rp ON rp.role_id=ur.role_id
            WHERE ur.user_id=@user
            """, connection))
        {
            command.Parameters.AddWithValue("user", userId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) { roles.Add(reader.GetString(0)); if (!reader.IsDBNull(1)) permissions.Add(reader.GetString(1)); }
        }
        return new(user, roles.Distinct().ToArray(), permissions.ToArray());
    }
    public async Task<IReadOnlyList<SessionInfo>> ListAsync(Guid userId, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT id,device_name,created_at,expires_at,revoked_at FROM identity.sessions WHERE user_id=@user ORDER BY created_at DESC
            """, connection);
        command.Parameters.AddWithValue("user", userId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<SessionInfo>();
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetGuid(0), reader.GetString(1), new(reader.GetDateTime(2)),
            new(reader.GetDateTime(3)), reader.IsDBNull(4) ? null : new DateTimeOffset(reader.GetDateTime(4))));
        return result;
    }
    public async Task LogoutAsync(Guid userId, Guid? sessionId, string correlation, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await IdentityStore.LockUserAsync(connection, tx, userId, ct);
        await using var command = new NpgsqlCommand("""
            UPDATE identity.sessions SET revoked_at=@now,revoke_reason='logout'
            WHERE user_id=@user AND (@all OR id=@session) AND revoked_at IS NULL
            """, connection, tx);
        var now = clock.GetUtcNow();
        command.Parameters.AddWithValue("user", userId); command.Parameters.AddWithValue("all", sessionId is null);
        command.Parameters.AddWithValue("session", sessionId ?? Guid.Empty); command.Parameters.AddWithValue("now", now);
        if (await command.ExecuteNonQueryAsync(ct) > 0)
            await IdentityStore.AuditAsync(connection, tx, userId, userId, "session.revoked", new { SessionId = sessionId }, correlation, now, ct);
        await tx.CommitAsync(ct);
    }
    internal static async Task RevokeAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid session, string reason, DateTimeOffset now, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("UPDATE identity.sessions SET revoked_at=@now,revoke_reason=@reason WHERE id=@id AND revoked_at IS NULL", connection, tx);
        command.Parameters.AddWithValue("id", session); command.Parameters.AddWithValue("now", now); command.Parameters.AddWithValue("reason", reason);
        await command.ExecuteNonQueryAsync(ct);
    }
    private async Task<string> NewRefreshAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid session, DateTimeOffset expires, CancellationToken ct)
    {
        var token = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var now = clock.GetUtcNow();
        await using var command = new NpgsqlCommand("""
            INSERT INTO identity.refresh_tokens(id,session_id,token_hash,created_at,expires_at) VALUES (@id,@session,@hash,@now,@expires)
            """, connection, tx);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7()); command.Parameters.AddWithValue("session", session);
        command.Parameters.AddWithValue("hash", HashRefresh(token)); command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("expires", expires);
        await command.ExecuteNonQueryAsync(ct); return token;
    }
    private AuthSession Issue(AuthenticatedUser user, Guid session, string refresh, DateTimeOffset now)
    {
        var expiry = now.AddMinutes(15);
        var token = new JwtSecurityToken(options.Value.Issuer, options.Value.Audience,
            [new Claim("sub", user.Id.ToString()), new Claim("sid", session.ToString()), new Claim("jti", Guid.NewGuid().ToString())],
            now.UtcDateTime, expiry.UtcDateTime, new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Value.JwtKey)), SecurityAlgorithms.HmacSha256));
        return new(new JwtSecurityTokenHandler().WriteToken(token), expiry, user, refresh, session);
    }
    internal static byte[] HashRefresh(string? token)
    {
        if (token is null || token.Length != 43 || token.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) throw InvalidRefresh();
        return SHA256.HashData(Encoding.UTF8.GetBytes(token));
    }
    private static string ValidateDevice(string? device)
    {
        var name = string.IsNullOrWhiteSpace(device) ? "unknown" : device.Trim();
        if (name.Length > 100) throw new ApiException(400, "invalid_device", "El nombre del dispositivo supera 100 caracteres.");
        return name;
    }
    private static ApiException InvalidRefresh() => new(401, "invalid_refresh", "La sesión de recuperación no es válida.");
}

