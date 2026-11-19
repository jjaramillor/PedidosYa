using System.Diagnostics;
using System.Text;
using System.Threading.RateLimiting;
using Delivery.Api;
using Delivery.BuildingBlocks;
using Delivery.Modules.Identity;
using Delivery.Modules.Identity.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
builder.Logging.AddJsonConsole(o => o.IncludeScopes = true);
builder.Services.AddOptions<IdentityOptions>().BindConfiguration("Identity")
    .Validate(o => Encoding.UTF8.GetByteCount(o.JwtKey) >= 32, "JWT key must have at least 32 bytes.")
    .Validate(o => Encoding.UTF8.GetByteCount(o.OtpKey) >= 32, "OTP key must have at least 32 bytes.")
    .Validate(o => !string.IsNullOrWhiteSpace(o.Issuer) && !string.IsNullOrWhiteSpace(o.Audience), "Issuer/audience required.")
    .Validate(o => !string.IsNullOrWhiteSpace(o.TermsVersion) && !string.IsNullOrWhiteSpace(o.PrivacyVersion), "Policy versions required.")
    .Validate(o => builder.Environment.IsDevelopment() || o.SmtpTls, "SMTP TLS required outside Development.")
    .ValidateOnStart();
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres required.")));
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(
    builder.Configuration.GetConnectionString("Redis") ?? throw new InvalidOperationException("ConnectionStrings:Redis required.")));
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("Delivery");
var keyPath = builder.Configuration["DataProtection:KeyPath"] ?? Path.Combine(builder.Environment.ContentRootPath, "keys");
dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keyPath));
builder.Services.AddIdentityModule();
builder.Services.AddMvp();
builder.Services.AddSingleton<OutboxDispatcher>();
if (builder.Configuration.GetValue("Messaging:RunWorker", true))
    builder.Services.AddHostedService(sp => sp.GetRequiredService<OutboxDispatcher>());
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = context =>
{
    context.ProblemDetails.Extensions["traceId"] = Activity.Current?.TraceId.ToString();
    context.ProblemDetails.Extensions["correlationId"] = context.HttpContext.TraceIdentifier;
});
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<IdentityOptions>>((jwt, identity) =>
    {
        jwt.MapInboundClaims = false;
        jwt.TokenValidationParameters = new()
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = identity.Value.Issuer,
            ValidAudience = identity.Value.Audience,
            ClockSkew = TimeSpan.FromSeconds(5),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(identity.Value.JwtKey))
        };
        jwt.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (context.Request.Path.StartsWithSegments("/hubs/orders")) context.Token = context.Request.Query["access_token"];
                if (string.IsNullOrEmpty(context.Token) && !context.Request.Headers.ContainsKey("Authorization"))
                    context.Token = context.Request.Cookies[BrowserSessions.AccessCookie];
                return Task.CompletedTask;
            },
            OnTokenValidated = async context =>
            {
                var principal = context.Principal!;
                if (!Guid.TryParse(principal.FindFirst("sub")?.Value, out var user)
                    || !Guid.TryParse(principal.FindFirst("sid")?.Value, out var session))
                { context.Fail("Invalid session."); return; }
                var service = context.HttpContext.RequestServices.GetRequiredService<SessionService>();
                var access = await service.ValidateAsync(user, session, context.HttpContext.RequestAborted);
                if (access is null) { context.Fail("Session revoked or inactive."); return; }
                var claims = (System.Security.Claims.ClaimsIdentity)principal.Identity!;
                foreach (var old in claims.FindAll("permission").Concat(claims.FindAll(System.Security.Claims.ClaimTypes.Role)).ToArray())
                    claims.RemoveClaim(old);
                foreach (var role in access.Roles) claims.AddClaim(new(System.Security.Claims.ClaimTypes.Role, role));
                foreach (var permission in access.Permissions) claims.AddClaim(new("permission", permission));
            }
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("otp", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.StatusCode = 429;
        await Results.Problem(statusCode: 429, title: "Demasiadas solicitudes.",
            extensions: new Dictionary<string, object?> { ["correlationId"] = context.HttpContext.TraceIdentifier })
            .ExecuteAsync(context.HttpContext);
    };
});
builder.Services.AddHealthChecks().AddCheck<DependencyHealthCheck>("dependencies", tags: ["ready"]);
var telemetry = builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("Delivery.Api"));
telemetry.WithTracing(t =>
{
    t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddSource(Telemetry.Name);
    if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"])) t.AddOtlpExporter();
});
telemetry.WithMetrics(m =>
{
    m.AddAspNetCoreInstrumentation().AddMeter(Telemetry.Name);
    if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"])) m.AddOtlpExporter();
});
builder.Logging.AddOpenTelemetry(o =>
{
    o.IncludeScopes = true;
    if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"])) o.AddOtlpExporter();
});
var app = builder.Build();
_ = app.Services.GetRequiredService<IOptions<IdentityOptions>>().Value;
await SchemaMigrator.ApplyAsync(app.Services.GetRequiredService<NpgsqlDataSource>());
await MvpComposition.ApplyMigrationsAsync(app.Services,CancellationToken.None);
if (!string.IsNullOrWhiteSpace(builder.Configuration["BootstrapAdminEmail"]))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<RoleService>().BootstrapAdminAsync(builder.Configuration["BootstrapAdminEmail"]!, CancellationToken.None);
    return;
}
app.Use(async (context, next) =>
{
    var supplied = context.Request.Headers["X-Correlation-ID"].ToString();
    var correlation = supplied.Length is > 0 and <= 64 && supplied.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
        ? supplied : Guid.NewGuid().ToString("N");
    context.TraceIdentifier = correlation;
    context.Response.Headers["X-Correlation-ID"] = correlation;
    using var scope = app.Logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlation });
    await next(context);
});
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<RedisIdentityRateLimit>();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseWebSockets();
app.Use(BrowserSessions.ValidateBrowserMutation);
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = r => r.Tags.Contains("ready") });
app.MapIdentityEndpoints();
app.MapBrowserSessions();
app.MapMvpEndpoints();
app.MapHub<OrderHub>("/hubs/orders",o=>o.CloseOnAuthenticationExpiration=true);
if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.Run();
public partial class Program;
