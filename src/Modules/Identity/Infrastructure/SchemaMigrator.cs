using Npgsql;
namespace Delivery.Modules.Identity.Infrastructure;

public static class SchemaMigrator
{
    public static async Task ApplyAsync(NpgsqlDataSource source, CancellationToken ct = default)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(74910322)", connection, transaction))
            await gate.ExecuteNonQueryAsync(ct);
        await using (var setup = new NpgsqlCommand("""
            CREATE SCHEMA IF NOT EXISTS platform;
            CREATE TABLE IF NOT EXISTS platform.schema_versions(version text PRIMARY KEY,applied_at timestamptz NOT NULL DEFAULT now())
            """, connection, transaction))
            await setup.ExecuteNonQueryAsync(ct);
        foreach (var (version, resource) in new[] { ("identity-001", "001-initial.sql"), ("identity-002", "002-epic-one.sql"), ("identity-003", "003-mvp-permissions.sql") })
        {
            await using var exists = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM platform.schema_versions WHERE version=@version)", connection, transaction);
            exists.Parameters.AddWithValue("version", version);
            if (await exists.ExecuteScalarAsync(ct) is true) continue;
            using var stream = typeof(SchemaMigrator).Assembly.GetManifestResourceStream("Delivery.Modules.Identity.Infrastructure." + resource)
                ?? throw new InvalidOperationException("Migration resource missing: " + resource);
            using var reader = new StreamReader(stream);
            await using var command = new NpgsqlCommand(await reader.ReadToEndAsync(ct), connection, transaction);
            await command.ExecuteNonQueryAsync(ct);
            await using var record = new NpgsqlCommand("INSERT INTO platform.schema_versions(version) VALUES (@version) ON CONFLICT DO NOTHING", connection, transaction);
            record.Parameters.AddWithValue("version", version); await record.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }
}
