using System.Collections.Concurrent;
using Delivery.BuildingBlocks;
using Delivery.Modules.Identity.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using Microsoft.AspNetCore.Builder;
using System.Net;
using Delivery.Modules.Identity;
using StackExchange.Redis;
using Delivery.Modules.Notifications;

namespace Delivery.Tests;

public sealed class TestClock : TimeProvider
{
    private long ticks = DateTimeOffset.UtcNow.Ticks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
    public void Advance(TimeSpan time) => Interlocked.Add(ref ticks, time.Ticks);
    public void Reset() => Interlocked.Exchange(ref ticks, DateTimeOffset.UtcNow.Ticks);
}
public sealed class CapturingSender : IOtpSender
{
    public ConcurrentDictionary<string, string> Codes { get; } = new();
    public int Sent;
    public bool Fail;
    public Task SendAsync(string email, string code, CancellationToken ct)
    {
        if (Fail) throw new InvalidOperationException("Simulated transport failure");
        Codes[email] = code;
        Interlocked.Increment(ref Sent);
        return Task.CompletedTask;
    }
}
public sealed class IntegrationFixture : IAsyncLifetime
{
    public PostgreSqlContainer Postgres { get; } = new PostgreSqlBuilder("postgis/postgis:17-3.5")
        .WithDatabase("delivery_tests").WithUsername("delivery").WithPassword("integration-password").Build();
    public RedisContainer Redis { get; } = new RedisBuilder("redis:7.4-alpine").Build();
    public TestClock Clock { get; } = new();
    public CapturingSender Sender { get; } = new();
    public FakeFederatedProvider Federated { get; } = new();
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public NpgsqlDataSource Source => Factory.Services.GetRequiredService<NpgsqlDataSource>();
    public OutboxDispatcher Dispatcher => Factory.Services.GetRequiredService<OutboxDispatcher>();
    private readonly string keyPath = Path.Combine(Path.GetTempPath(), "delivery-tests-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        await Task.WhenAll(Postgres.StartAsync(), Redis.StartAsync());
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = Postgres.GetConnectionString(),
                ["ConnectionStrings:Redis"] = Redis.GetConnectionString()+",allowAdmin=true",
                ["Identity:JwtKey"] = new string('j', 48),
                ["Identity:OtpKey"] = new string('o', 48),
                ["DataProtection:KeyPath"] = keyPath,
                ["Documents:Root"] = Path.Combine(keyPath, "uploads"),
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "",
                ["Messaging:RunWorker"] = "false",
                ["Payments:Provider"] = "sandbox",
                ["Payments:AccessToken"] = "",
                ["Payments:PublicKey"] = "",
                ["Payments:WebhookSecret"] = "",
                ["BootstrapAdminEmail"] = ""
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Clock);
                services.RemoveAll<IOtpSender>();
                services.AddSingleton<IOtpSender>(Sender);
                services.RemoveAll<IOrderEmailSender>();
                services.AddSingleton<IOrderEmailSender,TestOrderEmail>();
                services.RemoveAll<IFederatedProvider>();
                services.AddSingleton<IFederatedProvider>(Federated);
                services.AddSingleton<IStartupFilter, TestIpStartupFilter>();
            });
        });
        Client = Factory.CreateClient();
    }

    public async Task ResetAsync()
    {
        Sender.Fail = false; Sender.Sent = 0; Sender.Codes.Clear(); Clock.Reset();
        Federated.Email = "federated@example.test"; Federated.Subject = "external-user"; Federated.Exchanges = 0;
        Client.DefaultRequestHeaders.Authorization = null;
        Client.DefaultRequestHeaders.Remove("X-Test-IP");
        Client.DefaultRequestHeaders.Add("X-Test-IP", "127.0.0." + Interlocked.Increment(ref testIp));
        await Factory.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase().ExecuteAsync("FLUSHDB");
        await using var command = Source.CreateCommand("""
            TRUNCATE identity.registration_audit, identity.users, identity.otp_challenges, platform.outbox, platform.inbox,
                identity.user_roles,identity.sessions,identity.refresh_tokens,identity.consent_history,identity.security_audit,
                identity.federated_flows,identity.external_identities,identity.event_receipts RESTART IDENTITY CASCADE;
            DELETE FROM identity.role_permissions WHERE role_id IN (SELECT id FROM identity.roles WHERE NOT system_role);
            DELETE FROM identity.roles WHERE NOT system_role
            """);
        await command.ExecuteNonQueryAsync();
        await using var commerce = Source.CreateCommand("""
            TRUNCATE customers.addresses,customers.ratings,merchants.businesses,merchants.branches,merchants.members,merchants.products,merchants.stock_operations,
              pricing.carts,pricing.quotes,pricing.coupons,pricing.coupon_uses,orders.orders,orders.operations,orders.transitions,
              payments.payments,payments.refunds,payments.operations,payments.reconciliations,dispatch.couriers,dispatch.offers,
              deliveries.deliveries,deliveries.locations,deliveries.operations,ledger.vouchers,ledger.lines,ledger.deliveries,ledger.settlements,ledger.settlement_lines,
              support.cases,support.notes,support.evidence,notifications.messages,configuration.zones,platform.documents,platform.audit CASCADE;
            UPDATE configuration.parameters SET value='true'::jsonb WHERE name='orders.enabled';
            UPDATE configuration.parameters SET value='false'::jsonb WHERE name='cash.enabled';
            """);
        await commerce.ExecuteNonQueryAsync();
    }
    private int testIp;
    public async Task<long> CountAsync(string sql)
    {
        await using var command = Source.CreateCommand(sql);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
    public async Task DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
        await Task.WhenAll(Postgres.DisposeAsync().AsTask(), Redis.DisposeAsync().AsTask());
        if (Directory.Exists(keyPath)) Directory.Delete(keyPath, true);
    }
}

public sealed class TestOrderEmail:IOrderEmailSender
{
    public Task SendAsync(Guid user,string title,string body,CancellationToken ct)=>Task.CompletedTask;
}

public sealed class TestIpStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, following) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.TryParse(context.Request.Headers["X-Test-IP"].ToString(), out var ip) ? ip : IPAddress.Loopback;
            await following(context);
        });
        next(app);
    };
}
public sealed class FakeFederatedProvider : IFederatedProvider
{
    public string Email = "federated@example.test";
    public string Subject = "external-user";
    public int Exchanges;
    public Task<string> AuthorizationUrlAsync(string state, string nonce, string challenge, CancellationToken ct)
        => Task.FromResult("https://issuer.example/authorize?state=" + state + "&nonce=" + nonce + "&code_challenge=" + challenge);
    public Task<FederatedIdentity> ExchangeAsync(string code, string verifier, string nonce, CancellationToken ct)
    {
        Interlocked.Increment(ref Exchanges);
        if (code != "valid-code") throw new Delivery.BuildingBlocks.ApiException(401, "invalid_federation", "Invalid provider code.");
        return Task.FromResult(new FederatedIdentity("https://issuer.example", Subject, Email));
    }
}
