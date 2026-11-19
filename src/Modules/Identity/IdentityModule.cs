using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Delivery.BuildingBlocks;
using Delivery.Modules.Identity.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
namespace Delivery.Modules.Identity;

public static class IdentityModule
{
    public static IServiceCollection AddIdentityModule(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<OtpCrypto>();
        services.AddScoped<IdentityService>();
        services.AddScoped<SessionService>();
        services.AddSingleton<Delivery.Contracts.IIdentityDirectory,IdentityDirectory>();
        services.AddScoped<RoleService>();
        services.AddScoped<AccountService>();
        services.AddScoped<FederationService>();
        services.AddOptions<FederationOptions>().BindConfiguration("Identity:Federation");
        services.AddHttpClient("federation", client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<IFederatedProvider, OpenIdProvider>();
        services.AddSingleton<IOtpSender, SmtpOtpSender>();
        services.AddSingleton<IMessageHandler, OtpRequestedHandler>();
        services.AddSingleton<IMessageHandler, UserRegisteredHandler>();
        foreach (var type in new[] { "identity.roles-assigned.v1", "identity.account-status-changed.v1", "identity.account-closed.v1", "identity.consent-changed.v1" })
            services.AddSingleton<IMessageHandler>(new IdentityEventHandler(type));
        services.AddAuthorizationBuilder()
            .AddPolicy("identity.users.lookup", p => p.RequireAuthenticatedUser().RequireAssertion(c => c.User.FindAll("permission").Any(claim => new[]{"identity.users.manage","identity.roles.manage","participants.manage","finance.manage","support.manage"}.Contains(claim.Value))))
            .AddPolicy("identity.roles.manage", p => p.RequireAuthenticatedUser().RequireClaim("permission", "identity.roles.manage"))
            .AddPolicy("identity.users.manage", p => p.RequireAuthenticatedUser().RequireClaim("permission", "identity.users.manage"))
            .AddPolicy("identity.audit.read", p => p.RequireAuthenticatedUser().RequireClaim("permission", "identity.audit.read"));
        return services;
    }
    public static void MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/identity");
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        group.MapGet("/policies", (AccountService service) => Results.Ok(service.Policies()));
        group.MapPost("/otp/request", async (RequestOtp request, IdentityService service, HttpContext context, CancellationToken ct) =>
            Results.Accepted(value: await service.RequestAsync(request, context.TraceIdentifier, ct))).RequireRateLimiting("otp");
        group.MapPost("/otp/verify", async (VerifyOtp request, IdentityService service, HttpContext context, CancellationToken ct) =>
            Results.Ok(await service.VerifyAsync(request, context.TraceIdentifier, ct))).RequireRateLimiting("otp");
        group.MapPost("/login", async (RequestLogin request, IdentityService service, HttpContext context, CancellationToken ct) =>
            Results.Accepted(value: await service.RequestAsync(new(request.Email, "", false, "login", false, request.DeviceName), context.TraceIdentifier, ct))).RequireRateLimiting("otp");
        group.MapPost("/recovery", async (RequestLogin request, IdentityService service, HttpContext context, CancellationToken ct) =>
            Results.Accepted(value: await service.RequestAsync(new(request.Email, "", false, "recovery", false, request.DeviceName), context.TraceIdentifier, ct))).RequireRateLimiting("otp");
        group.MapPost("/sessions/refresh", async (RefreshSession request, SessionService service, HttpContext context, CancellationToken ct) =>
            Results.Ok(await service.RefreshAsync(request, context.TraceIdentifier, ct))).RequireRateLimiting("otp");
        group.MapGet("/sessions", async (SessionService service, ClaimsPrincipal user, CancellationToken ct) => Results.Ok(await service.ListAsync(UserId(user), ct))).RequireAuthorization();
        group.MapDelete("/sessions/{id:guid}", async (Guid id, SessionService service, HttpContext context, CancellationToken ct) =>
        { await service.LogoutAsync(UserId(context.User), id, context.TraceIdentifier, ct); return Results.NoContent(); }).RequireAuthorization();
        group.MapPost("/logout", async (SessionService service, HttpContext context, CancellationToken ct) =>
        { await service.LogoutAsync(UserId(context.User), SessionId(context.User), context.TraceIdentifier, ct); return Results.NoContent(); }).RequireAuthorization();
        group.MapPost("/logout-all", async (SessionService service, HttpContext context, CancellationToken ct) =>
        { await service.LogoutAsync(UserId(context.User), null, context.TraceIdentifier, ct); return Results.NoContent(); }).RequireAuthorization();
        group.MapGet("/me", async (IdentityService service, ClaimsPrincipal principal, CancellationToken ct) =>
        { var user = await service.GetUserAsync(UserId(principal), ct); return user is null ? Results.Unauthorized() : Results.Ok(user); }).RequireAuthorization();
        group.MapGet("/access", (ClaimsPrincipal user) => Results.Ok(new { Roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value), Permissions = user.FindAll("permission").Select(c => c.Value) })).RequireAuthorization();
        group.MapGet("/consents", async (AccountService service, ClaimsPrincipal user, CancellationToken ct) => Results.Ok(await service.ConsentsAsync(UserId(user), ct))).RequireAuthorization();
        group.MapPut("/consents", async (ConsentChange request, AccountService service, HttpContext context, CancellationToken ct) =>
        { await service.SetConsentAsync(UserId(context.User), request, context.TraceIdentifier, ct); return Results.NoContent(); }).RequireAuthorization();
        group.MapPatch("/account", async (AccountNameChange request, AccountService service, HttpContext context, CancellationToken ct) =>
        { await service.ChangeNameAsync(UserId(context.User), request, context.TraceIdentifier, ct); return Results.NoContent(); }).RequireAuthorization();
        group.MapPost("/account/deactivate", async (AccountService service, HttpContext context, CancellationToken ct) =>
        { await service.SetStatusAsync(UserId(context.User), UserId(context.User), "deactivated", false, context.TraceIdentifier, ct); return Results.NoContent(); }).RequireAuthorization();
        group.MapDelete("/account", async (AccountService service, HttpContext context, CancellationToken ct) =>
        { await service.CloseAsync(UserId(context.User), context.TraceIdentifier, ct); return Results.NoContent(); }).RequireAuthorization();
        group.MapGet("/admin/permissions", async (RoleService service, CancellationToken ct) => Results.Ok(await service.PermissionsAsync(ct))).RequireAuthorization("identity.roles.manage");
        group.MapGet("/admin/users", async (string? search, AccountService service, CancellationToken ct) => Results.Ok(await service.UsersAsync(search, ct))).RequireAuthorization("identity.users.lookup");
        group.MapGet("/admin/roles", async (RoleService service, CancellationToken ct) => Results.Ok(await service.ListAsync(ct))).RequireAuthorization("identity.roles.manage");
        group.MapPut("/admin/roles/{id}", async (string id, SaveRole request, RoleService service, HttpContext context, CancellationToken ct) =>
        { await service.SaveAsync(UserId(context.User), id, request, context.TraceIdentifier, ct); return Results.NoContent(); }).RequireAuthorization("identity.roles.manage");
        group.MapDelete("/admin/roles/{id}", async (string id, RoleService service, HttpContext context, CancellationToken ct) =>
        { await service.DeleteAsync(UserId(context.User), id, context.TraceIdentifier, ct); return Results.NoContent(); }).RequireAuthorization("identity.roles.manage");
        group.MapGet("/admin/users/{id:guid}/roles", async (Guid id, RoleService service, CancellationToken ct) => Results.Ok(await service.UserRolesAsync(id, ct))).RequireAuthorization("identity.roles.manage");
        group.MapPut("/admin/users/{id:guid}/roles", async (Guid id, AssignRoles request, RoleService service, HttpContext context, CancellationToken ct) =>
        { await service.AssignAsync(UserId(context.User), id, request, context.TraceIdentifier, ct); return Results.NoContent(); }).RequireAuthorization("identity.roles.manage");
        group.MapPut("/admin/users/{id:guid}/status", async (Guid id, AccountStatusChange request, AccountService service, HttpContext context, CancellationToken ct) =>
        { await service.SetStatusAsync(UserId(context.User), id, request.Status, true, context.TraceIdentifier, ct); return Results.NoContent(); }).RequireAuthorization("identity.users.manage");
        group.MapGet("/admin/audit", async (int? limit, RoleService service, CancellationToken ct) => Results.Ok(await service.AuditAsync(limit ?? 50, ct))).RequireAuthorization("identity.audit.read");

        group.MapPost("/federation/start", async (StartFederation request, FederationService service, HttpContext context, CancellationToken ct) =>
        {
            var result = await service.StartAsync(request, context.TraceIdentifier, ct);
            var state = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(result.AuthorizationUrl).Query)["state"].ToString();
            context.Response.Cookies.Append("delivery.federation", state, new CookieOptions
            { HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Lax, Path = "/api/v1/identity/federation", MaxAge = TimeSpan.FromMinutes(5) });
            return Results.Ok(result);
        }).RequireRateLimiting("otp");
        group.MapPost("/federation/complete", async (CompleteFederation request, FederationService service, HttpContext context, CancellationToken ct) =>
        {
            BindState(context, request.State);
            return Results.Ok(await service.CompleteAsync(request, context.TraceIdentifier, ct));
        }).RequireRateLimiting("otp");
        group.MapGet("/federation/callback", async (string state, string code, FederationService service, HttpContext context, CancellationToken ct) =>
        {
            BindState(context, state);
            return Results.Ok(await service.CompleteAsync(new(state, code), context.TraceIdentifier, ct));
        }).RequireRateLimiting("otp");
    }
    private static void BindState(HttpContext context, string? state)
    {
        var cookie = context.Request.Cookies["delivery.federation"];
        if (state is null || cookie is null || state.Length != 43 || cookie.Length != 43
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(cookie)))
            throw new ApiException(401, "federation_browser_mismatch", "El inicio de sesión no pertenece a este navegador.");
        context.Response.Cookies.Delete("delivery.federation", new CookieOptions { Path = "/api/v1/identity/federation", Secure = context.Request.IsHttps, SameSite = SameSiteMode.Lax });
    }
    private static Guid UserId(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue("sub")!);
    private static Guid SessionId(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue("sid")!);
}
