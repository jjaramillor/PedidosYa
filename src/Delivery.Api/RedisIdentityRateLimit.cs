using System.Security.Cryptography;
using System.Text;
using Delivery.BuildingBlocks;
using StackExchange.Redis;
namespace Delivery.Api;

// Redis is authoritative for the shared IP window; the ASP.NET limiter adds an in-process guard.
public sealed class RedisIdentityRateLimit(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IConnectionMultiplexer redis)
    {
        var path = context.Request.Path.Value ?? "";
        var auth = path.StartsWith("/api/v1/identity/", StringComparison.Ordinal)
            && (path.Contains("/otp/", StringComparison.Ordinal) || path.EndsWith("/login", StringComparison.Ordinal)
                || path.EndsWith("/recovery", StringComparison.Ordinal) || path.EndsWith("/sessions/refresh", StringComparison.Ordinal)
                || path.Contains("/federation/", StringComparison.Ordinal));
        if (auth)
        {
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var key = "identity:rate:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ip)));
            try
            {
                var count = (long)await redis.GetDatabase().ScriptEvaluateAsync("""
                    local count = redis.call('INCR', KEYS[1])
                    if count == 1 then redis.call('EXPIRE', KEYS[1], 60) end
                    return count
                    """, [new RedisKey(key)]);
                if (count > 20)
                {
                    context.Response.Headers.RetryAfter = "60";
                    throw new ApiException(429, "rate_limited", "Demasiadas solicitudes de acceso. Espera un minuto.");
                }
            }
            catch (RedisException) { throw new ApiException(503, "rate_limit_unavailable", "El acceso no está disponible temporalmente."); }
        }
        await next(context);
    }
}

