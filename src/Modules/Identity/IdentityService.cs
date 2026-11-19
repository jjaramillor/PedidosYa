using System.IdentityModel.Tokens.Jwt;
using System.Net.Mail;
using System.Security.Claims;
using System.Text;
using Delivery.BuildingBlocks;
using Delivery.Modules.Identity.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Delivery.Modules.Identity.Infrastructure;

namespace Delivery.Modules.Identity;

public sealed record RequestOtp(string Email, string DisplayName, bool AcceptTerms, string Purpose = "register", bool AcceptPrivacy = false, string DeviceName = "unknown", string? TermsVersion = null, string? PrivacyVersion = null);
public sealed record RequestLogin(string Email, string DeviceName = "unknown");
public sealed record VerifyOtp(Guid ChallengeId, string Code);
public sealed record OtpChallenge(Guid ChallengeId, DateTimeOffset ExpiresAt);
public sealed record AuthenticatedUser(Guid Id, string Email, string DisplayName);
public sealed record AuthSession(string AccessToken, DateTimeOffset ExpiresAt, AuthenticatedUser User, string RefreshToken, Guid SessionId);
public sealed record OtpDelivery(Guid ChallengeId, string ProtectedContent, DateTimeOffset ExpiresAt);
public sealed record OtpMail(string Email, string Code);
public sealed record UserRegistered(Guid UserId);

public sealed class IdentityService(NpgsqlDataSource source, OtpCrypto crypto, IDataProtectionProvider protection,
    IOptions<IdentityOptions> options, TimeProvider clock, SessionService sessions)
{
    private readonly IDataProtector protector = protection.CreateProtector("Delivery.Identity.Otp.v1");

    public async Task<OtpChallenge> RequestAsync(RequestOtp request, string correlation, CancellationToken ct)
    {
        var email = NormalizeEmail(request.Email);
        var name = request.DisplayName?.Trim();
        if (request.Purpose is not ("register" or "login" or "recovery"))
            throw new ApiException(400, "invalid_purpose", "El propósito OTP no es válido.");
        if (request.Purpose == "register")
        {
            if ((request.TermsVersion is not null && request.TermsVersion != options.Value.TermsVersion)
                || (request.PrivacyVersion is not null && request.PrivacyVersion != options.Value.PrivacyVersion))
                throw new ApiException(409, "policies_changed", "Las políticas cambiaron. Consulta las versiones y vuelve a aceptar.");
            var errors = new Dictionary<string, string[]>();
            if (string.IsNullOrWhiteSpace(name) || name.Length > 100) errors["displayName"] = ["El nombre es obligatorio y admite hasta 100 caracteres."];
            if (!request.AcceptTerms) errors["acceptTerms"] = ["Debes aceptar los términos vigentes."];
            if (!request.AcceptPrivacy) errors["acceptPrivacy"] = ["Debes aceptar la política de privacidad vigente."];
            if (errors.Count > 0) throw new ApiException(400, "invalid_registration", "Datos de registro inválidos.", errors);
        }
        if (request.DeviceName is null || request.DeviceName.Length > 100)
            throw new ApiException(400, "invalid_device", "El nombre del dispositivo no es válido.");
        name = request.Purpose == "register" ? name : request.DeviceName;
        var now = clock.GetUtcNow();
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await LockEmail(connection, transaction, email, ct);
        bool eligible;
        await using (var account = new NpgsqlCommand("SELECT status FROM identity.users WHERE email=@email", connection, transaction))
        {
            account.Parameters.AddWithValue("email", email);
            var status = await account.ExecuteScalarAsync(ct) as string;
            eligible = status == "active" || (status is null && request.Purpose == "register");
        }
        await using (var cooldown = new NpgsqlCommand("""
            SELECT created_at FROM identity.otp_challenges WHERE email = @email ORDER BY created_at DESC LIMIT 1
            """, connection, transaction))
        {
            cooldown.Parameters.AddWithValue("email", email);
            var recent = await cooldown.ExecuteScalarAsync(ct);
            if (recent is DateTime previous && now - new DateTimeOffset(previous) < OtpPolicy.Cooldown)
                throw new ApiException(429, "otp_cooldown", "Espera 60 segundos antes de solicitar otro código.");
        }
        await using (var invalidate = new NpgsqlCommand("""
            UPDATE identity.otp_challenges SET consumed_at = @now WHERE email = @email AND consumed_at IS NULL
            """, connection, transaction))
        {
            invalidate.Parameters.AddWithValue("email", email);
            invalidate.Parameters.AddWithValue("now", now);
            await invalidate.ExecuteNonQueryAsync(ct);
        }
        var id = Guid.CreateVersion7();
        var code = crypto.Generate();
        var expires = now + OtpPolicy.Lifetime;
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO identity.otp_challenges
            (id,email,display_name,terms_version,code_hash,created_at,expires_at,purpose,eligible,device_name,privacy_version)
            VALUES (@id,@email,@name,@terms,@hash,@now,@expires,@purpose,@eligible,@device,@privacy)
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("email", email);
            insert.Parameters.AddWithValue("name", name ?? "");
            insert.Parameters.AddWithValue("purpose", request.Purpose);
            insert.Parameters.AddWithValue("eligible", eligible);
            insert.Parameters.AddWithValue("device", request.DeviceName);
            insert.Parameters.AddWithValue("privacy", options.Value.PrivacyVersion);
            insert.Parameters.AddWithValue("terms", options.Value.TermsVersion);
            insert.Parameters.AddWithValue("hash", crypto.Hash(id, code));
            insert.Parameters.AddWithValue("now", now);
            insert.Parameters.AddWithValue("expires", expires);
            await insert.ExecuteNonQueryAsync(ct);
        }
        var content = protector.Protect(System.Text.Json.JsonSerializer.Serialize(new OtpMail(email, code)));
        if (eligible)
            await Outbox.EnqueueAsync(connection, transaction, "identity.otp-requested.v1",
                new OtpDelivery(id, content, expires), correlation, ct);
        await IdentityStore.AuditAsync(connection, transaction, null, null, "otp.requested", new { ChallengeId = id, Purpose = request.Purpose }, correlation, now, ct);
        await transaction.CommitAsync(ct);
        Telemetry.OtpRequests.Add(1);
        return new(id, expires);
    }

    public async Task<AuthSession> VerifyAsync(VerifyOtp request, string correlation, CancellationToken ct)
    {
        if (request.Code is null || request.Code.Length != 6 || request.Code.Any(c => c < '0' || c > '9'))
            throw new ApiException(400, "invalid_code_format", "El código debe tener seis dígitos.", new Dictionary<string, string[]> { ["code"] = ["Introduce seis dígitos."] });
        var now = clock.GetUtcNow();
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        // Obtain the email before taking locks, then follow the same email -> challenge lock order as requests.
        string email;
        await using (var lookup = new NpgsqlCommand("SELECT email FROM identity.otp_challenges WHERE id=@id", connection, transaction))
        {
            lookup.Parameters.AddWithValue("id", request.ChallengeId);
            email = await lookup.ExecuteScalarAsync(ct) as string ?? throw InvalidCode();
        }
        await LockEmail(connection, transaction, email, ct);
        string name, terms, purpose, device, privacy;
        byte[] hash;
        DateTimeOffset expires;
        int attempts;
        bool consumed;
        await using (var query = new NpgsqlCommand("""
            SELECT display_name, terms_version, code_hash, expires_at, attempts, consumed_at, purpose, eligible, device_name, privacy_version
            FROM identity.otp_challenges WHERE id=@id FOR UPDATE
            """, connection, transaction))
        {
            query.Parameters.AddWithValue("id", request.ChallengeId);
            await using var reader = await query.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw InvalidCode();
            name = reader.GetString(0); terms = reader.GetString(1); hash = reader.GetFieldValue<byte[]>(2);
            expires = new DateTimeOffset(reader.GetDateTime(3)); attempts = reader.GetInt32(4); consumed = !reader.IsDBNull(5);
            purpose = reader.GetString(6);
            if (!reader.GetBoolean(7)) throw InvalidCode();
            device = reader.GetString(8);
            privacy = reader.GetString(9);
        }
        if (!OtpPolicy.CanVerify(expires, attempts, consumed, now)) throw InvalidCode();
        if (!crypto.Matches(request.ChallengeId, request.Code, hash))
        {
            await using var fail = new NpgsqlCommand("UPDATE identity.otp_challenges SET attempts=attempts+1 WHERE id=@id", connection, transaction);
            fail.Parameters.AddWithValue("id", request.ChallengeId);
            await fail.ExecuteNonQueryAsync(ct);
            await IdentityStore.AuditAsync(connection, transaction, null, null, "otp.invalid", new { ChallengeId = request.ChallengeId }, correlation, now, ct);
            await transaction.CommitAsync(ct);
            Telemetry.OtpVerifications.Add(1, new KeyValuePair<string, object?>("outcome", "invalid"));
            throw InvalidCode();
        }
        AuthenticatedUser user;
        var newId = Guid.CreateVersion7();
        bool created;
        await using (var create = new NpgsqlCommand("""
            INSERT INTO identity.users (id,email,display_name,terms_version,terms_accepted_at)
            SELECT @id,@email,@name,@terms,@now WHERE @register ON CONFLICT(email) DO NOTHING
            """, connection, transaction))
        {
            create.Parameters.AddWithValue("id", newId);
            create.Parameters.AddWithValue("email", email);
            create.Parameters.AddWithValue("name", name);
            create.Parameters.AddWithValue("terms", terms);
            create.Parameters.AddWithValue("now", now);
            create.Parameters.AddWithValue("register", purpose == "register");
            created = await create.ExecuteNonQueryAsync(ct) == 1;
        }
        await using (var query = new NpgsqlCommand("SELECT id,email,display_name FROM identity.users WHERE email=@email", connection, transaction))
        {
            query.Parameters.AddWithValue("email", email);
            await using var reader = await query.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw InvalidCode();
            user = new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2));
        }
        user = await IdentityStore.LockUserAsync(connection, transaction, user.Id, ct);
        await using (var consume = new NpgsqlCommand("UPDATE identity.otp_challenges SET consumed_at=@now WHERE id=@id", connection, transaction))
        {
            consume.Parameters.AddWithValue("now", now);
            consume.Parameters.AddWithValue("id", request.ChallengeId);
            await consume.ExecuteNonQueryAsync(ct);
        }
        if (created)
        {
            await using var role = new NpgsqlCommand("INSERT INTO identity.user_roles(user_id,role_id) VALUES (@id,'customer')", connection, transaction);
            role.Parameters.AddWithValue("id", user.Id);
            await role.ExecuteNonQueryAsync(ct);
            await IdentityStore.ConsentAsync(connection, transaction, user.Id, "terms", terms, true, now, ct);
            await IdentityStore.ConsentAsync(connection, transaction, user.Id, "privacy", privacy, true, now, ct);
            await Outbox.EnqueueAsync(connection, transaction, "identity.user-registered.v1", new UserRegistered(user.Id), correlation, ct);
        }
        if (purpose == "recovery")
        {
            await using var revoke = new NpgsqlCommand("UPDATE identity.sessions SET revoked_at=@now,revoke_reason='recovery' WHERE user_id=@id AND revoked_at IS NULL", connection, transaction);
            revoke.Parameters.AddWithValue("now", now); revoke.Parameters.AddWithValue("id", user.Id);
            await revoke.ExecuteNonQueryAsync(ct);
        }
        // Create the token before committing so failures do not consume the challenge.
        var session = await sessions.CreateAsync(connection, transaction, user, device, correlation, ct);
        await IdentityStore.AuditAsync(connection, transaction, user.Id, user.Id, created ? "user.registered" : "user.authenticated", new { ChallengeId = request.ChallengeId, Purpose = purpose }, correlation, now, ct);
        await transaction.CommitAsync(ct);
        Telemetry.OtpVerifications.Add(1, new KeyValuePair<string, object?>("outcome", "success"));
        return session;
    }

    public async Task<AuthenticatedUser?> GetUserAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("SELECT id,email,display_name FROM identity.users WHERE id=@id AND status='active'", connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2)) : null;
    }

    private static async Task LockEmail(NpgsqlConnection connection, NpgsqlTransaction transaction, string email, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@email,0))", connection, transaction);
        command.Parameters.AddWithValue("email", email);
        await command.ExecuteNonQueryAsync(ct);
    }

    public static string NormalizeEmail(string? value)
    {
        var email = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(email) || email.Length > 254 || !MailAddress.TryCreate(email, out var parsed) || parsed.Address != email)
            throw new ApiException(400, "invalid_email", "Correo electrónico inválido.", new Dictionary<string, string[]> { ["email"] = ["Introduce un correo válido."] });
        return email;
    }
    private static ApiException InvalidCode() => new(401, "invalid_otp", "Código inválido, vencido o ya utilizado.");
}
