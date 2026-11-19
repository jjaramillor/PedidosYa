using Delivery.Modules.Identity;
using Microsoft.AspNetCore.Antiforgery;

namespace Delivery.Api;

public static class BrowserSessions
{
    public const string AccessCookie = "delivery.access";
    private const string RefreshCookie = "delivery.refresh";

    public static void MapBrowserSessions(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/identity/browser");
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        group.MapGet("/csrf", (HttpContext context, IAntiforgery antiforgery) =>
            Results.Ok(new { Token = antiforgery.GetAndStoreTokens(context).RequestToken }));
        group.MapGet("/status",(HttpContext context)=>Results.Ok(new{
            Authenticated=context.User.Identity?.IsAuthenticated==true,
            CanRefresh=context.Request.Cookies.ContainsKey(RefreshCookie)
        }));
        group.MapPost("/verify", async (VerifyOtp request, IdentityService identity, HttpContext context, CancellationToken ct) =>
            Store(context, await identity.VerifyAsync(request, context.TraceIdentifier, ct), app.Environment)).RequireRateLimiting("otp");
        group.MapPost("/refresh", async (SessionService sessions, HttpContext context, CancellationToken ct) =>
        {
            if (!context.Request.Cookies.TryGetValue(RefreshCookie, out var refresh)) return Results.Unauthorized();
            return Store(context, await sessions.RefreshAsync(new(refresh), context.TraceIdentifier, ct), app.Environment);
        }).RequireRateLimiting("otp");
        group.MapPost("/logout", async (SessionService sessions, HttpContext context, CancellationToken ct) =>
        {
            if(context.User.Identity?.IsAuthenticated==true)
                await sessions.LogoutAsync(Guid.Parse(context.User.FindFirst("sub")!.Value),
                    Guid.Parse(context.User.FindFirst("sid")!.Value), context.TraceIdentifier, ct);
            else if(context.Request.Cookies.TryGetValue(RefreshCookie,out var refresh))
            {
                try
                {
                    var session=await sessions.RefreshAsync(new(refresh),context.TraceIdentifier,ct);
                    await sessions.LogoutAsync(session.User.Id,session.SessionId,context.TraceIdentifier,ct);
                }
                catch(Delivery.BuildingBlocks.ApiException error) when(error.StatusCode is 401 or 403) { }
            }
            context.Response.Cookies.Delete(AccessCookie, Options(app.Environment, "/"));
            context.Response.Cookies.Delete(RefreshCookie, Options(app.Environment, "/api/v1/identity/browser"));
            return Results.NoContent();
        });
        group.MapPost("/close-account", async (AccountService accounts, HttpContext context, CancellationToken ct) =>
        {
            await accounts.CloseAsync(Guid.Parse(context.User.FindFirst("sub")!.Value), context.TraceIdentifier, ct);
            context.Response.Cookies.Delete(AccessCookie, Options(app.Environment, "/"));
            context.Response.Cookies.Delete(RefreshCookie, Options(app.Environment, "/api/v1/identity/browser"));
            return Results.NoContent();
        }).RequireAuthorization();
    }

    public static async Task ValidateBrowserMutation(HttpContext context, RequestDelegate next)
    {
        var unsafeMethod = HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method)
            || HttpMethods.IsPatch(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method);
        var browserRoute = context.Request.Path.StartsWithSegments("/api/v1/identity/browser");
        var cookieAuth = context.Request.Cookies.ContainsKey(AccessCookie) && !context.Request.Headers.ContainsKey("Authorization");
        if(cookieAuth&&context.WebSockets.IsWebSocketRequest&&context.Request.Path.StartsWithSegments("/hubs/orders"))
        {
            var origin=context.Request.Headers.Origin.ToString();
            var ownOrigin=$"{context.Request.Scheme}://{context.Request.Host}";
            var allowed=context.RequestServices.GetRequiredService<IConfiguration>().GetSection("Browser:AllowedOrigins").Get<string[]>()??[];
            if(string.IsNullOrEmpty(origin)||(!string.Equals(origin,ownOrigin,StringComparison.OrdinalIgnoreCase)&&!allowed.Contains(origin,StringComparer.OrdinalIgnoreCase)))
            {await Results.Problem(statusCode:403,title:"Origen de conexión no autorizado.").ExecuteAsync(context);return;}
        }
        if (unsafeMethod && (browserRoute || cookieAuth))
        {
            try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException)
            {
                await Results.Problem(statusCode: 400, title: "La sesión del formulario expiró. Actualiza la página.").ExecuteAsync(context);
                return;
            }
        }
        await next(context);
    }

    private static IResult Store(HttpContext context, AuthSession session, IHostEnvironment environment)
    {
        var access = Options(environment, "/"); access.Expires = session.ExpiresAt;
        var refresh = Options(environment, "/api/v1/identity/browser"); refresh.MaxAge = TimeSpan.FromDays(30);
        context.Response.Cookies.Append(AccessCookie, session.AccessToken, access);
        context.Response.Cookies.Append(RefreshCookie, session.RefreshToken, refresh);
        return Results.Ok(new { session.User, session.ExpiresAt, session.SessionId });
    }

    private static CookieOptions Options(IHostEnvironment environment, string path) => new()
    {
        HttpOnly = true, Secure = !environment.IsDevelopment(), SameSite = SameSiteMode.Strict, Path = path,
        IsEssential = true
    };
}
