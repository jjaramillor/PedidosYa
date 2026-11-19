using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using StackExchange.Redis;
namespace Delivery.Api;

public sealed class DependencyHealthCheck(NpgsqlDataSource postgres, IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await using var connection = await postgres.OpenConnectionAsync(ct);
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(ct);
            await redis.GetDatabase().PingAsync().WaitAsync(ct);
            return HealthCheckResult.Healthy();
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return HealthCheckResult.Unhealthy("A dependency is unavailable."); }
    }
}

