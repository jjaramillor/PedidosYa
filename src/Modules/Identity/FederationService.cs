using System.Security.Cryptography;
using System.Text;
using Delivery.BuildingBlocks;
using Delivery.Modules.Identity.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
namespace Delivery.Modules.Identity;

public sealed record StartFederation(string DisplayName, bool AcceptTerms, bool AcceptPrivacy, string? TermsVersion = null, string? PrivacyVersion = null);
public sealed record FederationStart(string AuthorizationUrl, DateTimeOffset ExpiresAt);
public sealed record CompleteFederation(string State, string Code);

public sealed class FederationService(NpgsqlDataSource source, IFederatedProvider provider, IDataProtectionProvider protection,
    IOptions<IdentityOptions> options, SessionService sessions, TimeProvider clock)
{
    private readonly IDataProtector protector = protection.CreateProtector("Delivery.Identity.Pkce.v1");
    public async Task<FederationStart> StartAsync(StartFederation request, string correlation, CancellationToken ct)
    {
        var name = request.DisplayName?.Trim();
        if ((request.TermsVersion is not null && request.TermsVersion != options.Value.TermsVersion)
            || (request.PrivacyVersion is not null && request.PrivacyVersion != options.Value.PrivacyVersion))
            throw new ApiException(409, "policies_changed", "Las políticas cambiaron. Consulta las versiones y vuelve a aceptar.");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || !request.AcceptTerms || !request.AcceptPrivacy)
            throw new ApiException(400, "invalid_federation_request", "Nombre y aceptación de políticas son obligatorios.");
        var state = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var nonce = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var verifier = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var url = await provider.AuthorizationUrlAsync(state, nonce, challenge, ct);
        var now = clock.GetUtcNow(); var expires = now.AddMinutes(5);
        await using var connection = await source.OpenConnectionAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO identity.federated_flows(id,state_hash,nonce,protected_verifier,display_name,terms_version,privacy_version,expires_at)
            VALUES (@id,@hash,@nonce,@verifier,@name,@terms,@privacy,@expires)
            """, connection, tx);
        insert.Parameters.AddWithValue("id", Guid.CreateVersion7()); insert.Parameters.AddWithValue("hash", SessionService.HashRefresh(state));
        insert.Parameters.AddWithValue("nonce", nonce); insert.Parameters.AddWithValue("verifier", protector.Protect(verifier));
        insert.Parameters.AddWithValue("name", name); insert.Parameters.AddWithValue("terms", options.Value.TermsVersion); insert.Parameters.AddWithValue("expires", expires);
        insert.Parameters.AddWithValue("privacy", options.Value.PrivacyVersion);
        await insert.ExecuteNonQueryAsync(ct);
        await IdentityStore.AuditAsync(connection, tx, null, null, "federation.started", new { }, correlation, now, ct); await tx.CommitAsync(ct);
        return new(url, expires);
    }
    public async Task<AuthSession> CompleteAsync(CompleteFederation request, string correlation, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Code) || request.Code.Length > 4096) throw new ApiException(400, "invalid_code", "Código de autorización inválido.");
        byte[] hash;
        try { hash = SessionService.HashRefresh(request.State); } catch (ApiException) { throw Invalid(); }
        string verifier, nonce, name, terms, privacy;
        // Burn state in its own transaction before contacting the provider: replay never re-exchanges a code.
        await using (var connection = await source.OpenConnectionAsync(ct))
        await using (var tx = await connection.BeginTransactionAsync(ct))
        {
            await using (var query = new NpgsqlCommand("""
                SELECT nonce,protected_verifier,display_name,terms_version,expires_at,consumed_at,privacy_version
                FROM identity.federated_flows WHERE state_hash=@hash FOR UPDATE
                """, connection, tx))
            {
                query.Parameters.AddWithValue("hash", hash); await using var reader = await query.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct) || !reader.IsDBNull(5) || new DateTimeOffset(reader.GetDateTime(4)) <= clock.GetUtcNow()) throw Invalid();
                nonce = reader.GetString(0); verifier = protector.Unprotect(reader.GetString(1)); name = reader.GetString(2); terms = reader.GetString(3);
                privacy = reader.GetString(6);
            }
            await using var consume = new NpgsqlCommand("UPDATE identity.federated_flows SET consumed_at=@now,protected_verifier='' WHERE state_hash=@hash", connection, tx);
            consume.Parameters.AddWithValue("hash", hash); consume.Parameters.AddWithValue("now", clock.GetUtcNow()); await consume.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
        }
        var identity = await provider.ExchangeAsync(request.Code, verifier, nonce, ct);
        await using var db = await source.OpenConnectionAsync(ct); await using var transaction = await db.BeginTransactionAsync(ct);
        await using (var externalGate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@external,1))", db, transaction))
        {
            externalGate.Parameters.AddWithValue("external", System.Text.Json.JsonSerializer.Serialize(new[] { identity.Issuer, identity.Subject }));
            await externalGate.ExecuteNonQueryAsync(ct);
        }
        // Email lock order matches OTP registration. Never link an existing local account using email alone.
        await using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@email,0))", db, transaction))
        { gate.Parameters.AddWithValue("email", identity.Email); await gate.ExecuteNonQueryAsync(ct); }
        Guid? existing;
        await using (var lookup = new NpgsqlCommand("SELECT user_id FROM identity.external_identities WHERE issuer=@issuer AND subject=@subject", db, transaction))
        {
            lookup.Parameters.AddWithValue("issuer", identity.Issuer); lookup.Parameters.AddWithValue("subject", identity.Subject);
            existing = await lookup.ExecuteScalarAsync(ct) is Guid id ? id : null;
        }
        AuthenticatedUser user;
        var now = clock.GetUtcNow();
        if (existing is Guid userId) user = await IdentityStore.LockUserAsync(db, transaction, userId, ct);
        else
        {
            await using (var duplicate = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM identity.users WHERE email=@email)", db, transaction))
            {
                duplicate.Parameters.AddWithValue("email", identity.Email);
                if (await duplicate.ExecuteScalarAsync(ct) is true) throw new ApiException(409, "explicit_link_required", "Este correo ya tiene cuenta. Inicia sesión con OTP.");
            }
            user = new(Guid.CreateVersion7(), identity.Email, name);
            await using var create = new NpgsqlCommand("""
                INSERT INTO identity.users(id,email,display_name,terms_version,terms_accepted_at) VALUES (@id,@email,@name,@terms,@now);
                INSERT INTO identity.user_roles(user_id,role_id) VALUES (@id,'customer');
                INSERT INTO identity.external_identities(issuer,subject,user_id) VALUES (@issuer,@subject,@id)
                """, db, transaction);
            create.Parameters.AddWithValue("id", user.Id); create.Parameters.AddWithValue("email", user.Email); create.Parameters.AddWithValue("name", name);
            create.Parameters.AddWithValue("terms", terms); create.Parameters.AddWithValue("now", now);
            create.Parameters.AddWithValue("issuer", identity.Issuer); create.Parameters.AddWithValue("subject", identity.Subject); await create.ExecuteNonQueryAsync(ct);
            await IdentityStore.ConsentAsync(db, transaction, user.Id, "terms", terms, true, now, ct);
            await IdentityStore.ConsentAsync(db, transaction, user.Id, "privacy", privacy, true, now, ct);
            await Outbox.EnqueueAsync(db, transaction, "identity.user-registered.v1", new UserRegistered(user.Id), correlation, ct);
        }
        var session = await sessions.CreateAsync(db, transaction, user, "federated", correlation, ct);
        await IdentityStore.AuditAsync(db, transaction, user.Id, user.Id, "federation.authenticated", new { identity.Issuer }, correlation, now, ct);
        await transaction.CommitAsync(ct); return session;
    }
    private static ApiException Invalid() => new(401, "invalid_federation_state", "El flujo federado venció o ya se utilizó.");
}
