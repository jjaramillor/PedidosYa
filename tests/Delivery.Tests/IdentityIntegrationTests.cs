using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Delivery.BuildingBlocks;
using Delivery.Modules.Identity;
using Microsoft.Extensions.DependencyInjection;
namespace Delivery.Tests;

public sealed class IdentityIntegrationTests(IntegrationFixture fixture) : IClassFixture<IntegrationFixture>
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private async Task<OtpChallenge> Request(string email = "client@example.test")
    {
        using var scope = fixture.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IdentityService>().RequestAsync(new(email, "Cliente", true, AcceptPrivacy: true), "test", Ct);
    }
    private async Task<AuthSession> Verify(Guid id, string code)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IdentityService>().VerifyAsync(new(id, code), "test", Ct);
    }
    private async Task<string> Deliver(string email = "client@example.test")
    {
        Assert.True(await fixture.Dispatcher.DispatchOneAsync(Ct));
        return fixture.Sender.Codes[email];
    }

    [Fact]
    public async Task Http_registration_token_and_me_work_and_replay_is_rejected()
    {
        await fixture.ResetAsync();
        fixture.Client.DefaultRequestHeaders.Remove("Authorization");
        fixture.Client.DefaultRequestHeaders.Remove("X-Correlation-ID");
        fixture.Client.DefaultRequestHeaders.Add("X-Correlation-ID", "registration-test");
        var response = await fixture.Client.PostAsJsonAsync("/api/v1/identity/otp/request", new RequestOtp(" User@Example.Test ", "Ana", true, AcceptPrivacy: true));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("registration-test", response.Headers.GetValues("X-Correlation-ID").Single());
        var challenge = (await response.Content.ReadFromJsonAsync<OtpChallenge>())!;
        Assert.DoesNotContain("code", (await response.Content.ReadAsStringAsync()).ToLowerInvariant());
        var code = await Deliver("user@example.test");
        var verified = await fixture.Client.PostAsJsonAsync("/api/v1/identity/otp/verify", new VerifyOtp(challenge.ChallengeId, code));
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
        var session = (await verified.Content.ReadFromJsonAsync<AuthSession>())!;
        fixture.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        var me = await fixture.Client.GetFromJsonAsync<AuthenticatedUser>("/api/v1/identity/me");
        Assert.Equal(session.User.Id, me!.Id);
        Assert.Equal("Ana", me.DisplayName);
        var replay = await fixture.Client.PostAsJsonAsync("/api/v1/identity/otp/verify", new VerifyOtp(challenge.ChallengeId, code));
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal("application/problem+json", replay.Content.Headers.ContentType!.MediaType);
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM identity.users"));
        Assert.True(await fixture.Dispatcher.DispatchOneAsync(Ct));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM identity.registration_audit"));
    }

    [Fact]
    public async Task Wrong_codes_lock_challenge_after_five_attempts()
    {
        await fixture.ResetAsync();
        var challenge = await Request();
        var code = await Deliver();
        var wrong = code == "000000" ? "111111" : "000000";
        for (var i = 0; i < 5; i++) Assert.Equal(401, (await Assert.ThrowsAsync<ApiException>(() => Verify(challenge.ChallengeId, wrong))).StatusCode);
        await Assert.ThrowsAsync<ApiException>(() => Verify(challenge.ChallengeId, code));
        Assert.Equal(5, await fixture.CountAsync("SELECT attempts FROM identity.otp_challenges"));
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM identity.users"));
    }

    [Fact]
    public async Task Expired_code_cannot_register_user()
    {
        await fixture.ResetAsync();
        var challenge = await Request();
        var code = await Deliver();
        fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        await Assert.ThrowsAsync<ApiException>(() => Verify(challenge.ChallengeId, code));
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM identity.users"));
    }

    [Fact]
    public async Task Concurrent_requests_enforce_cooldown()
    {
        await fixture.ResetAsync();
        var tasks = Enumerable.Range(0, 4).Select(async _ =>
        {
            try { await Request(); return 202; } catch (ApiException ex) { return ex.StatusCode; }
        });
        var results = await Task.WhenAll(tasks);
        Assert.Equal(1, results.Count(r => r == 202));
        Assert.Equal(3, results.Count(r => r == 429));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM platform.outbox"));
    }

    [Fact]
    public async Task Concurrent_verifications_create_one_user_and_one_event()
    {
        await fixture.ResetAsync();
        var challenge = await Request();
        var code = await Deliver();
        var tasks = Enumerable.Range(0, 4).Select(async _ =>
        {
            try { await Verify(challenge.ChallengeId, code); return 200; } catch (ApiException ex) { return ex.StatusCode; }
        });
        var results = await Task.WhenAll(tasks);
        Assert.Equal(1, results.Count(r => r == 200));
        Assert.Equal(3, results.Count(r => r == 401));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM identity.users"));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM platform.outbox WHERE type='identity.user-registered.v1'"));
    }

    [Fact]
    public async Task Resend_invalidates_old_code_and_login_reuses_user()
    {
        await fixture.ResetAsync();
        var first = await Request();
        var oldCode = await Deliver();
        fixture.Clock.Advance(TimeSpan.FromSeconds(61));
        var second = await Request();
        var newCode = await Deliver();
        await Assert.ThrowsAsync<ApiException>(() => Verify(first.ChallengeId, oldCode));
        var registration = await Verify(second.ChallengeId, newCode);
        fixture.Clock.Advance(TimeSpan.FromSeconds(61));
        var login = await Request();
        // Dispatch registration event before the new OTP.
        Assert.True(await fixture.Dispatcher.DispatchOneAsync(Ct));
        var loginCode = await Deliver();
        var session = await Verify(login.ChallengeId, loginCode);
        Assert.Equal(registration.User.Id, session.User.Id);
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM identity.users"));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM platform.outbox WHERE type='identity.user-registered.v1'"));
    }

    [Fact]
    public async Task Failed_transport_rolls_back_inbox_and_retries_without_losing_message()
    {
        await fixture.ResetAsync();
        await Request();
        fixture.Sender.Fail = true;
        Assert.True(await fixture.Dispatcher.DispatchOneAsync(Ct));
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM platform.inbox"));
        Assert.Equal(1, await fixture.CountAsync("SELECT attempts FROM platform.outbox"));
        fixture.Sender.Fail = false;
        await using (var command = fixture.Source.CreateCommand("UPDATE platform.outbox SET next_attempt_at=now()"))
            await command.ExecuteNonQueryAsync();
        Assert.True(await fixture.Dispatcher.DispatchOneAsync(Ct));
        Assert.Equal(1, fixture.Sender.Sent);
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM platform.inbox"));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM platform.outbox WHERE processed_at IS NOT NULL AND payload='{}'::jsonb"));
        // Replay the same envelope: inbox suppresses the handler even if outbox is requeued.
        await using (var command = fixture.Source.CreateCommand("UPDATE platform.outbox SET processed_at=NULL,next_attempt_at=now()"))
            await command.ExecuteNonQueryAsync();
        Assert.True(await fixture.Dispatcher.DispatchOneAsync(Ct));
        Assert.Equal(1, fixture.Sender.Sent);
    }

    [Fact]
    public async Task Outbox_and_challenge_are_atomic_if_message_insert_fails()
    {
        await fixture.ResetAsync();
        await using (var command = fixture.Source.CreateCommand("ALTER TABLE platform.outbox ADD CONSTRAINT reject_test CHECK (type <> 'identity.otp-requested.v1')"))
            await command.ExecuteNonQueryAsync();
        try
        {
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => Request());
            Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM identity.otp_challenges"));
        }
        finally
        {
            await using var cleanup = fixture.Source.CreateCommand("ALTER TABLE platform.outbox DROP CONSTRAINT reject_test");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Http_errors_are_problem_details_and_anonymous_me_is_rejected()
    {
        await fixture.ResetAsync();
        fixture.Client.DefaultRequestHeaders.Authorization = null;
        var result = await fixture.Client.PostAsJsonAsync("/api/v1/identity/otp/request", new RequestOtp("bad", "Ana", true));
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal("application/problem+json", result.Content.Headers.ContentType!.MediaType);
        var json = await result.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("correlationId", out _));
        Assert.True(json.TryGetProperty("traceId", out _));
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Client.GetAsync("/api/v1/identity/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fixture.Client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM pg_extension WHERE extname='postgis'"));
    }

    [Fact]
    public async Task Consent_is_required_and_payload_never_contains_plaintext_code()
    {
        await fixture.ResetAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IdentityService>();
        await Assert.ThrowsAsync<ApiException>(() => service.RequestAsync(new("client@example.test", "Ana", false), "test", Ct));
        await Request();
        await using (var pending = fixture.Source.CreateCommand("SELECT payload::text FROM platform.outbox"))
        {
            var content = (string)(await pending.ExecuteScalarAsync())!;
            Assert.DoesNotContain("client@example.test", content);
            Assert.DoesNotContain("\"Code\"", content);
        }
        var code = await Deliver();
        await using var command = fixture.Source.CreateCommand("SELECT encode(code_hash,'hex') FROM identity.otp_challenges");
        Assert.NotEqual(code, await command.ExecuteScalarAsync());
    }
}
