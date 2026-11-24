using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using Delivery.BuildingBlocks;
using Delivery.Modules.Identity;
using Delivery.Modules.Identity.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
namespace Delivery.Tests;

public sealed class EpicOneIntegrationTests(IntegrationFixture fixture) : IClassFixture<IntegrationFixture>
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private async Task<T> Use<TService, T>(Func<TService, Task<T>> action) where TService : notnull
    { using var scope = fixture.Factory.Services.CreateScope(); return await action(scope.ServiceProvider.GetRequiredService<TService>()); }
    private async Task Use<TService>(Func<TService, Task> action) where TService : notnull
    { using var scope = fixture.Factory.Services.CreateScope(); await action(scope.ServiceProvider.GetRequiredService<TService>()); }
    private async Task Drain()
    { while (await fixture.Dispatcher.DispatchOneAsync(Ct)) { } }
    private async Task<AuthSession> Register(string email = "client@example.test")
    {
        var challenge = await Use<IdentityService, OtpChallenge>(s => s.RequestAsync(new(email, "Cliente", true, AcceptPrivacy: true), "test", Ct));
        await Drain();
        return await Use<IdentityService, AuthSession>(s => s.VerifyAsync(new(challenge.ChallengeId, fixture.Sender.Codes[email]), "test", Ct));
    }
    private async Task<AuthSession> Login(string email = "client@example.test", bool recovery = false)
    {
        fixture.Clock.Advance(TimeSpan.FromSeconds(61));
        var challenge = await Use<IdentityService, OtpChallenge>(s => s.RequestAsync(new(email, "", false, recovery ? "recovery" : "login", false, "Tablet"), "test", Ct));
        await Drain();
        return await Use<IdentityService, AuthSession>(s => s.VerifyAsync(new(challenge.ChallengeId, fixture.Sender.Codes[email]), "test", Ct));
    }
    private void Bearer(AuthSession session) => fixture.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
    private async Task<AuthSession> Admin()
    {
        var session = await Register("admin@example.test");
        await Use<RoleService>(s => s.BootstrapAdminAsync(session.User.Email, Ct));
        return session;
    }

    [Fact]
    public async Task Registration_persists_customer_role_consents_and_hashed_refresh()
    {
        await fixture.ResetAsync(); var session = await Register();
        Assert.Equal(43, session.RefreshToken.Length); Assert.NotEqual(Guid.Empty, session.SessionId);
        Assert.Equal(2, await fixture.CountAsync("SELECT count(*) FROM identity.consent_history WHERE accepted"));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM identity.user_roles WHERE role_id='customer'"));
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM identity.user_roles WHERE role_id='admin'"));
        await using var hash = fixture.Source.CreateCommand("SELECT encode(token_hash,'hex') FROM identity.refresh_tokens");
        Assert.NotEqual(session.RefreshToken, (string)(await hash.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Login_does_not_register_unknown_accounts_or_send_their_codes()
    {
        await fixture.ResetAsync();
        var response = await fixture.Client.PostAsJsonAsync("/api/v1/identity/login", new RequestLogin("unknown@example.test"));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM identity.users"));
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM platform.outbox"));
        await Drain(); Assert.Equal(0, fixture.Sender.Sent);
    }

    [Fact]
    public async Task Recovery_revokes_old_sessions_and_creates_one_new_session()
    {
        await fixture.ResetAsync(); var first = await Register(); var recovered = await Login(recovery: true);
        Assert.Equal(first.User.Id, recovered.User.Id); Assert.NotEqual(first.SessionId, recovered.SessionId);
        Assert.Null(await Use<SessionService, AccessContext?>(s => s.ValidateAsync(first.User.Id, first.SessionId, Ct)));
        Assert.NotNull(await Use<SessionService, AccessContext?>(s => s.ValidateAsync(recovered.User.Id, recovered.SessionId, Ct)));
        var error = await Assert.ThrowsAsync<ApiException>(() => Use<SessionService, AuthSession>(s => s.RefreshAsync(new(first.RefreshToken), "test", Ct)));
        Assert.Equal(401, error.StatusCode);
    }

    [Fact]
    public async Task Refresh_rotates_and_reuse_revokes_the_session_family()
    {
        await fixture.ResetAsync(); var session = await Register();
        var refreshed = await Use<SessionService, AuthSession>(s => s.RefreshAsync(new(session.RefreshToken), "test", Ct));
        Assert.NotEqual(session.RefreshToken, refreshed.RefreshToken); Assert.Equal(session.SessionId, refreshed.SessionId);
        await Assert.ThrowsAsync<ApiException>(() => Use<SessionService, AuthSession>(s => s.RefreshAsync(new(session.RefreshToken), "test", Ct)));
        Assert.Null(await Use<SessionService, AccessContext?>(s => s.ValidateAsync(refreshed.User.Id, refreshed.SessionId, Ct)));
        await Assert.ThrowsAsync<ApiException>(() => Use<SessionService, AuthSession>(s => s.RefreshAsync(new(refreshed.RefreshToken), "test", Ct)));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM identity.security_audit WHERE action='session.refresh-reuse'"));
    }

    [Fact]
    public async Task Lost_refresh_response_requires_new_otp_without_revoking_other_devices()
    {
        await fixture.ResetAsync();
        var lost = await Register();
        var otherDevice = await Login();
        // The server commits rotation, but the client never consumes the response body.
        using (var discarded = await fixture.Client.PostAsJsonAsync("/api/v1/identity/sessions/refresh", new RefreshSession(lost.RefreshToken)))
            Assert.Equal(HttpStatusCode.OK, discarded.StatusCode);
        using var retry = await fixture.Client.PostAsJsonAsync("/api/v1/identity/sessions/refresh", new RefreshSession(lost.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, retry.StatusCode);
        Assert.Null(await Use<SessionService, AccessContext?>(s => s.ValidateAsync(lost.User.Id, lost.SessionId, Ct)));
        Assert.NotNull(await Use<SessionService, AccessContext?>(s => s.ValidateAsync(otherDevice.User.Id, otherDevice.SessionId, Ct)));
        var renewed = await Login();
        Assert.NotEqual(lost.SessionId, renewed.SessionId);
        Assert.NotNull(await Use<SessionService, AccessContext?>(s => s.ValidateAsync(renewed.User.Id, renewed.SessionId, Ct)));
        using var usable = await fixture.Client.PostAsJsonAsync("/api/v1/identity/sessions/refresh", new RefreshSession(renewed.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, usable.StatusCode);
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM identity.security_audit WHERE action='session.refresh-reuse'"));
    }

    [Fact]
    public async Task Concurrent_refresh_detects_reuse_without_minting_multiple_active_families()
    {
        await fixture.ResetAsync(); var session = await Register();
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
        {
            try { await Use<SessionService, AuthSession>(s => s.RefreshAsync(new(session.RefreshToken), "test", Ct)); return 200; }
            catch (ApiException ex) { return ex.StatusCode; }
        }));
        Assert.Equal(1, results.Count(r => r == 200)); Assert.Equal(2, results.Count(r => r == 401));
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM identity.sessions WHERE revoked_at IS NULL"));
    }

    [Fact]
    public async Task Expired_session_cannot_be_refreshed_or_validated()
    {
        await fixture.ResetAsync(); var session = await Register(); fixture.Clock.Advance(TimeSpan.FromDays(31));
        await Assert.ThrowsAsync<ApiException>(() => Use<SessionService, AuthSession>(s => s.RefreshAsync(new(session.RefreshToken), "test", Ct)));
        Assert.Null(await Use<SessionService, AccessContext?>(s => s.ValidateAsync(session.User.Id, session.SessionId, Ct)));
    }

    [Fact]
    public async Task Revocation_is_immediate_and_cannot_revoke_another_users_session()
    {
        await fixture.ResetAsync(); var first = await Register(); var other = await Register("other@example.test");
        await Use<SessionService>(s => s.LogoutAsync(first.User.Id, other.SessionId, "test", Ct));
        Assert.NotNull(await Use<SessionService, AccessContext?>(s => s.ValidateAsync(other.User.Id, other.SessionId, Ct)));
        Bearer(first);
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.Client.PostAsync("/api/v1/identity/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Client.GetAsync("/api/v1/identity/me")).StatusCode);
    }

    [Fact]
    public async Task Customer_cannot_escalate_and_admin_permissions_are_loaded_from_database()
    {
        await fixture.ResetAsync(); var customer = await Register(); var admin = await Admin();
        Bearer(customer);
        var denied = await fixture.Client.PutAsJsonAsync("/api/v1/identity/admin/users/" + customer.User.Id + "/roles", new AssignRoles(["admin"]));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Bearer(admin);
        Assert.Equal(HttpStatusCode.OK, (await fixture.Client.GetAsync("/api/v1/identity/admin/roles")).StatusCode);
        await Use<RoleService>(s => s.AssignAsync(admin.User.Id, customer.User.Id, new(["customer", "admin"]), "test", Ct));
        Bearer(customer);
        Assert.Equal(HttpStatusCode.OK, (await fixture.Client.GetAsync("/api/v1/identity/admin/roles")).StatusCode);
        await Use<RoleService>(s => s.AssignAsync(admin.User.Id, customer.User.Id, new(["customer"]), "test", Ct));
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Client.GetAsync("/api/v1/identity/admin/roles")).StatusCode);
    }

    [Fact]
    public async Task Last_admin_is_protected_from_role_removal_blocking_and_closure()
    {
        await fixture.ResetAsync(); var admin = await Admin();
        foreach (var operation in new Func<Task>[]
        {
            ()=>Use<RoleService>(s=>s.AssignAsync(admin.User.Id,admin.User.Id,new(["customer"]),"test",Ct)),
            ()=>Use<AccountService>(s=>s.SetStatusAsync(admin.User.Id,admin.User.Id,"blocked",true,"test",Ct)),
            ()=>Use<AccountService>(s=>s.CloseAsync(admin.User.Id,"test",Ct))
        })
            Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(operation)).StatusCode);
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM identity.users WHERE status='active'"));
    }

    [Fact]
    public async Task Custom_role_crud_is_idempotent_and_assigned_roles_cannot_be_deleted()
    {
        await fixture.ResetAsync(); var admin = await Admin(); var customer = await Register();
        var request = new SaveRole("Auditor", ["identity.audit.read"]);
        await Use<RoleService>(s => s.SaveAsync(admin.User.Id, "auditor", request, "test", Ct));
        await Use<RoleService>(s => s.SaveAsync(admin.User.Id, "auditor", request, "test", Ct));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM identity.security_audit WHERE action='role.saved'"));
        await Use<RoleService>(s => s.AssignAsync(admin.User.Id, customer.User.Id, new(["customer", "auditor"]), "test", Ct));
        Bearer(customer); Assert.Equal(HttpStatusCode.OK, (await fixture.Client.GetAsync("/api/v1/identity/admin/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Client.GetAsync("/api/v1/identity/admin/roles")).StatusCode);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => Use<RoleService>(s => s.DeleteAsync(admin.User.Id, "auditor", "test", Ct)))).StatusCode);
        await Use<RoleService>(s => s.AssignAsync(admin.User.Id, customer.User.Id, new(["customer"]), "test", Ct));
        await Use<RoleService>(s => s.DeleteAsync(admin.User.Id, "auditor", "test", Ct));
        await Use<RoleService>(s => s.DeleteAsync(admin.User.Id, "auditor", "test", Ct));
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM identity.roles WHERE id='auditor'"));
    }

    [Fact]
    public async Task Unknown_permissions_and_roles_fail_without_mutation()
    {
        await fixture.ResetAsync(); var admin = await Admin(); var user = await Register();
        await Assert.ThrowsAsync<ApiException>(() => Use<RoleService>(s => s.SaveAsync(admin.User.Id, "unknown", new("Unknown", ["not-a-permission"]), "test", Ct)));
        await Assert.ThrowsAsync<ApiException>(() => Use<RoleService>(s => s.AssignAsync(admin.User.Id, user.User.Id, new(["missing"]), "test", Ct)));
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM identity.roles WHERE id='unknown'"));
        Assert.Equal(["customer"], await Use<RoleService, string[]>(s => s.UserRolesAsync(user.User.Id, Ct)));
    }

    [Fact]
    public async Task Blocking_revokes_access_and_reactivation_requires_new_login()
    {
        await fixture.ResetAsync(); var admin = await Admin(); var user = await Register();
        fixture.Clock.Advance(TimeSpan.FromSeconds(61));
        var pending = await Use<IdentityService, OtpChallenge>(s => s.RequestAsync(new(user.User.Email, "", false, "login"), "test", Ct));
        await Drain();
        var oldCode = fixture.Sender.Codes[user.User.Email];
        await Use<AccountService>(s => s.SetStatusAsync(admin.User.Id, user.User.Id, "blocked", true, "test", Ct));
        Bearer(user); Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Client.GetAsync("/api/v1/identity/me")).StatusCode);
        await Assert.ThrowsAsync<ApiException>(() => Use<SessionService, AuthSession>(s => s.RefreshAsync(new(user.RefreshToken), "test", Ct)));
        await Use<AccountService>(s => s.SetStatusAsync(admin.User.Id, user.User.Id, "active", true, "test", Ct));
        await Assert.ThrowsAsync<ApiException>(() => Use<IdentityService, AuthSession>(s => s.VerifyAsync(new(pending.ChallengeId, oldCode), "test", Ct)));
        Assert.Null(await Use<SessionService, AccessContext?>(s => s.ValidateAsync(user.User.Id, user.SessionId, Ct)));
        var login = await Login(); Assert.Equal(user.User.Id, login.User.Id);
    }

    [Fact]
    public async Task Consent_is_versioned_idempotent_and_marketing_can_be_withdrawn()
    {
        await fixture.ResetAsync(); var session = await Register();
        await Use<AccountService>(s => s.SetConsentAsync(session.User.Id, new("marketing", "2026-10", true), "test", Ct));
        await Use<AccountService>(s => s.SetConsentAsync(session.User.Id, new("marketing", "2026-10", true), "test", Ct));
        await Use<AccountService>(s => s.SetConsentAsync(session.User.Id, new("marketing", "2026-10", false), "test", Ct));
        Assert.Equal(2, await fixture.CountAsync("SELECT count(*) FROM identity.consent_history WHERE kind='marketing'"));
        Assert.NotNull(await Use<SessionService, AccessContext?>(s => s.ValidateAsync(session.User.Id, session.SessionId, Ct)));
        await Assert.ThrowsAsync<ApiException>(() => Use<AccountService>(s => s.SetConsentAsync(session.User.Id, new("terms", "obsolete", true), "test", Ct)));
    }

    [Fact]
    public async Task Withdrawing_required_consent_deactivates_and_revokes_sessions()
    {
        await fixture.ResetAsync(); var session = await Register();
        await Use<AccountService>(s => s.SetConsentAsync(session.User.Id, new("privacy", "2026-10", false), "test", Ct));
        Assert.Null(await Use<SessionService, AccessContext?>(s => s.ValidateAsync(session.User.Id, session.SessionId, Ct)));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM identity.users WHERE status='deactivated'"));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM platform.outbox WHERE type='identity.consent-changed.v1'"));
    }

    [Fact]
    public async Task Account_name_can_change_without_changing_commercial_profile()
    {
        await fixture.ResetAsync(); var session = await Register();
        await Assert.ThrowsAsync<ApiException>(() => Use<AccountService>(s => s.ChangeNameAsync(session.User.Id, new(""), "test", Ct)));
        await Use<AccountService>(s => s.ChangeNameAsync(session.User.Id, new("Nuevo nombre"), "test", Ct));
        var user = await Use<IdentityService, AuthenticatedUser?>(s => s.GetUserAsync(session.User.Id, Ct));
        Assert.Equal("Nuevo nombre", user!.DisplayName);
    }

    [Fact]
    public async Task Closing_account_anonymizes_identity_clears_challenges_and_revokes_access()
    {
        await fixture.ResetAsync(); var session = await Register();
        await Use<AccountService>(s => s.CloseAsync(session.User.Id, "test", Ct));
        Assert.Null(await Use<SessionService, AccessContext?>(s => s.ValidateAsync(session.User.Id, session.SessionId, Ct)));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM identity.users WHERE status='closed' AND email LIKE '%@deleted.invalid'"));
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM identity.otp_challenges"));
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM identity.user_roles"));
        await Drain();
        var replacement = await Register(); Assert.NotEqual(session.User.Id, replacement.User.Id);
    }

    private async Task<(FederationStart Start, string State)> Federation()
    {
        var start = await Use<FederationService, FederationStart>(s => s.StartAsync(new("Federado", true, true), "test", Ct));
        return (start, QueryHelpers.ParseQuery(new Uri(start.AuthorizationUrl).Query)["state"].ToString());
    }

    [Fact]
    public async Task Federation_creates_a_customer_reuses_subject_and_rejects_replay()
    {
        await fixture.ResetAsync(); var flow = await Federation();
        var session = await Use<FederationService, AuthSession>(s => s.CompleteAsync(new(flow.State, "valid-code"), "test", Ct));
        await Assert.ThrowsAsync<ApiException>(() => Use<FederationService, AuthSession>(s => s.CompleteAsync(new(flow.State, "valid-code"), "test", Ct)));
        Assert.Equal(1, fixture.Federated.Exchanges);
        var next = await Federation();
        var login = await Use<FederationService, AuthSession>(s => s.CompleteAsync(new(next.State, "valid-code"), "test", Ct));
        Assert.Equal(session.User.Id, login.User.Id); Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM identity.users"));
    }

    [Fact]
    public async Task Federation_does_not_silently_link_an_existing_local_email()
    {
        await fixture.ResetAsync(); await Register("federated@example.test"); var flow = await Federation();
        var error = await Assert.ThrowsAsync<ApiException>(() => Use<FederationService, AuthSession>(s => s.CompleteAsync(new(flow.State, "valid-code"), "test", Ct)));
        Assert.Equal(409, error.StatusCode); Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM identity.external_identities"));
    }

    [Fact]
    public async Task Expired_federation_state_never_exchanges_a_code()
    {
        await fixture.ResetAsync(); var flow = await Federation(); fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        await Assert.ThrowsAsync<ApiException>(() => Use<FederationService, AuthSession>(s => s.CompleteAsync(new(flow.State, "valid-code"), "test", Ct)));
        Assert.Equal(0, fixture.Federated.Exchanges);
    }

    [Fact]
    public async Task Federation_http_callback_is_bound_to_the_initiating_browser()
    {
        await fixture.ResetAsync();
        var response = await fixture.Client.PostAsJsonAsync("/api/v1/identity/federation/start", new StartFederation("Federado", true, true));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var flow = (await response.Content.ReadFromJsonAsync<FederationStart>())!;
        var state = QueryHelpers.ParseQuery(new Uri(flow.AuthorizationUrl).Query)["state"].ToString();
        using var stranger = fixture.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await stranger.PostAsJsonAsync("/api/v1/identity/federation/complete", new CompleteFederation(state, "valid-code"))).StatusCode);
        var completed = await fixture.Client.PostAsJsonAsync("/api/v1/identity/federation/complete", new CompleteFederation(state, "valid-code"));
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode); Assert.Equal("no-store", completed.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task Authentication_rate_limit_is_shared_by_redis_and_rejects_the_twenty_first_request()
    {
        await fixture.ResetAsync();
        for (var i = 0; i < 20; i++)
            Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Client.PostAsJsonAsync("/api/v1/identity/login", new RequestLogin("invalid"))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await fixture.Client.PostAsJsonAsync("/api/v1/identity/login", new RequestLogin("invalid"))).StatusCode);
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM identity.users"));
    }

    [Fact]
    public async Task Migration_upgrades_iteration_zero_and_is_idempotent()
    {
        await fixture.ResetAsync();
        await using (var create = fixture.Source.CreateCommand("CREATE DATABASE delivery_upgrade")) await create.ExecuteNonQueryAsync();
        var settings = new NpgsqlConnectionStringBuilder(fixture.Postgres.GetConnectionString()) { Database = "delivery_upgrade" };
        await using var source = NpgsqlDataSource.Create(settings.ConnectionString);
        using (var stream = typeof(SchemaMigrator).Assembly.GetManifestResourceStream("Delivery.Modules.Identity.Infrastructure.001-initial.sql")!)
        using (var reader = new StreamReader(stream))
        {
            await using var baseline = source.CreateCommand(await reader.ReadToEndAsync()); await baseline.ExecuteNonQueryAsync();
        }
        await using (var user = source.CreateCommand("""
            INSERT INTO identity.users(id,email,display_name,terms_version,terms_accepted_at)
            VALUES ('00000000-0000-0000-0000-000000000001','legacy@example.test','Legacy','2026-10',now())
            """)) await user.ExecuteNonQueryAsync();
        await SchemaMigrator.ApplyAsync(source); await SchemaMigrator.ApplyAsync(source);
        await using var query = source.CreateCommand("SELECT count(*) FROM identity.user_roles WHERE role_id='customer'");
        Assert.Equal(1L, await query.ExecuteScalarAsync());
        await using var versions = source.CreateCommand("SELECT count(*) FROM platform.schema_versions");
        Assert.Equal(3L, await versions.ExecuteScalarAsync());
    }
}
