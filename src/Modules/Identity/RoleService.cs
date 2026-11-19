using Delivery.BuildingBlocks;
using Delivery.Modules.Identity.Infrastructure;
using Npgsql;
namespace Delivery.Modules.Identity;

public sealed record RoleDefinition(string Id, string Name, string[] Permissions, bool SystemRole);
public sealed record SaveRole(string Name, string[] Permissions);
public sealed record AssignRoles(string[] Roles);
public sealed record SecurityAuditEntry(Guid Id, Guid? ActorId, Guid? SubjectId, string Action, string Data, string CorrelationId, DateTimeOffset OccurredAt);

public sealed class RoleService(NpgsqlDataSource source, TimeProvider clock)
{
    public async Task<string[]> PermissionsAsync(CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("SELECT id FROM identity.permissions ORDER BY id", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<string>(); while (await reader.ReadAsync(ct)) result.Add(reader.GetString(0)); return result.ToArray();
    }
    public async Task<IReadOnlyList<RoleDefinition>> ListAsync(CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT r.id,r.name,r.system_role,COALESCE(array_agg(p.permission_id ORDER BY p.permission_id) FILTER(WHERE p.permission_id IS NOT NULL),ARRAY[]::text[])
            FROM identity.roles r LEFT JOIN identity.role_permissions p ON p.role_id=r.id GROUP BY r.id ORDER BY r.id
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<RoleDefinition>();
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetFieldValue<string[]>(3), reader.GetBoolean(2)));
        return result;
    }
    public async Task SaveAsync(Guid actor, string id, SaveRole request, string correlation, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 50 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ApiException(400, "invalid_role", "Identificador de rol inválido.");
        var name = request.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || request.Permissions is null || request.Permissions.Length > 50)
            throw new ApiException(400, "invalid_role", "Nombre y permisos del rol inválidos.");
        var permissions = request.Permissions.Distinct().Order().ToArray();
        await using var connection = await source.OpenConnectionAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        await IdentityStore.AdminGateAsync(connection, tx, ct);
        await RequireAsync(connection, tx, actor, "identity.roles.manage", ct);
        await using (var known = new NpgsqlCommand("SELECT count(*) FROM identity.permissions WHERE id=ANY(@ids)", connection, tx))
        {
            known.Parameters.AddWithValue("ids", permissions);
            if (Convert.ToInt64(await known.ExecuteScalarAsync(ct)) != permissions.Length) throw new ApiException(400, "unknown_permission", "Hay permisos desconocidos.");
        }
        bool same = false;
        await using (var existing = new NpgsqlCommand("""
            SELECT r.name,r.system_role,COALESCE(array_agg(p.permission_id ORDER BY p.permission_id) FILTER(WHERE p.permission_id IS NOT NULL),ARRAY[]::text[])
            FROM identity.roles r LEFT JOIN identity.role_permissions p ON p.role_id=r.id WHERE r.id=@id GROUP BY r.id
            """, connection, tx))
        {
            existing.Parameters.AddWithValue("id", id);
            await using var reader = await existing.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                if (reader.GetBoolean(1)) throw new ApiException(409, "protected_role", "Los roles del sistema no se modifican.");
                same = reader.GetString(0) == name && reader.GetFieldValue<string[]>(2).SequenceEqual(permissions);
            }
        }
        if (!same)
        {
            await using var upsert = new NpgsqlCommand("""
                INSERT INTO identity.roles(id,name) VALUES (@id,@name) ON CONFLICT(id) DO UPDATE SET name=excluded.name;
                DELETE FROM identity.role_permissions WHERE role_id=@id;
                INSERT INTO identity.role_permissions(role_id,permission_id) SELECT @id,unnest(@permissions::text[])
                """, connection, tx);
            upsert.Parameters.AddWithValue("id", id); upsert.Parameters.AddWithValue("name", name); upsert.Parameters.AddWithValue("permissions", permissions);
            await upsert.ExecuteNonQueryAsync(ct);
            await IdentityStore.AuditAsync(connection, tx, actor, null, "role.saved", new { Role = id, Permissions = permissions }, correlation, clock.GetUtcNow(), ct);
        }
        await tx.CommitAsync(ct);
    }
    public async Task DeleteAsync(Guid actor, string id, string correlation, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        await IdentityStore.AdminGateAsync(connection, tx, ct); await RequireAsync(connection, tx, actor, "identity.roles.manage", ct);
        await using (var query = new NpgsqlCommand("SELECT system_role FROM identity.roles WHERE id=@id", connection, tx))
        {
            query.Parameters.AddWithValue("id", id); var exists = await query.ExecuteScalarAsync(ct);
            if (exists is null) { await tx.CommitAsync(ct); return; }
            if (exists is true) throw new ApiException(409, "protected_role", "El rol del sistema no puede eliminarse.");
        }
        await using (var assigned = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM identity.user_roles WHERE role_id=@id)", connection, tx))
        {
            assigned.Parameters.AddWithValue("id", id);
            if (await assigned.ExecuteScalarAsync(ct) is true) throw new ApiException(409, "role_in_use", "El rol tiene usuarios asignados.");
        }
        await using (var delete = new NpgsqlCommand("DELETE FROM identity.role_permissions WHERE role_id=@id; DELETE FROM identity.roles WHERE id=@id", connection, tx))
        { delete.Parameters.AddWithValue("id", id); await delete.ExecuteNonQueryAsync(ct); }
        await IdentityStore.AuditAsync(connection, tx, actor, null, "role.deleted", new { Role = id }, correlation, clock.GetUtcNow(), ct);
        await tx.CommitAsync(ct);
    }
    public async Task<string[]> UserRolesAsync(Guid user, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("SELECT role_id FROM identity.user_roles WHERE user_id=@id ORDER BY role_id", connection);
        command.Parameters.AddWithValue("id", user); await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<string>(); while (await reader.ReadAsync(ct)) result.Add(reader.GetString(0)); return result.ToArray();
    }
    public async Task AssignAsync(Guid actor, Guid user, AssignRoles request, string correlation, CancellationToken ct)
    {
        if (request.Roles is null || request.Roles.Length > 20) throw new ApiException(400, "invalid_roles", "Lista de roles inválida.");
        var roles = request.Roles.Distinct().Order().ToArray();
        await using var connection = await source.OpenConnectionAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        await IdentityStore.AdminGateAsync(connection, tx, ct); await RequireAsync(connection, tx, actor, "identity.roles.manage", ct);
        await IdentityStore.LockUserAsync(connection, tx, user, ct);
        await using (var known = new NpgsqlCommand("SELECT count(*) FROM identity.roles WHERE id=ANY(@ids)", connection, tx))
        {
            known.Parameters.AddWithValue("ids", roles);
            if (Convert.ToInt64(await known.ExecuteScalarAsync(ct)) != roles.Length) throw new ApiException(400, "unknown_role", "Hay roles desconocidos.");
        }
        var previous = new List<string>();
        await using (var query = new NpgsqlCommand("SELECT role_id FROM identity.user_roles WHERE user_id=@id ORDER BY role_id", connection, tx))
        { query.Parameters.AddWithValue("id", user); await using var reader = await query.ExecuteReaderAsync(ct); while (await reader.ReadAsync(ct)) previous.Add(reader.GetString(0)); }
        if (previous.Contains("admin") && !roles.Contains("admin")) await EnsureOtherAdminAsync(connection, tx, user, ct);
        if (!previous.SequenceEqual(roles))
        {
            await using var update = new NpgsqlCommand("""
                DELETE FROM identity.user_roles WHERE user_id=@id;
                INSERT INTO identity.user_roles(user_id,role_id) SELECT @id,unnest(@roles::text[])
                """, connection, tx);
            update.Parameters.AddWithValue("id", user); update.Parameters.AddWithValue("roles", roles); await update.ExecuteNonQueryAsync(ct);
            await IdentityStore.AuditAsync(connection, tx, actor, user, "roles.assigned", new { Roles = roles }, correlation, clock.GetUtcNow(), ct);
            await Outbox.EnqueueAsync(connection, tx, "identity.roles-assigned.v1", new { UserId = user, Roles = roles }, correlation, ct);
        }
        await tx.CommitAsync(ct);
    }
    internal static async Task EnsureOtherAdminAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid user, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM identity.user_roles r JOIN identity.users u ON u.id=r.user_id WHERE r.role_id='admin' AND u.status='active' AND u.id<>@id)
            """, connection, tx);
        command.Parameters.AddWithValue("id", user);
        if (await command.ExecuteScalarAsync(ct) is not true) throw new ApiException(409, "last_admin", "Debe quedar al menos un administrador activo.");
    }
    internal static async Task ProtectAdminAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid user, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM identity.user_roles WHERE user_id=@id AND role_id='admin')", connection, tx);
        command.Parameters.AddWithValue("id", user);
        if (await command.ExecuteScalarAsync(ct) is true) await EnsureOtherAdminAsync(connection, tx, user, ct);
    }
    internal static async Task RequireAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid user, string permission, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM identity.users u JOIN identity.user_roles ur ON ur.user_id=u.id
            JOIN identity.role_permissions p ON p.role_id=ur.role_id WHERE u.id=@id AND u.status='active' AND p.permission_id=@permission)
            """, connection, tx);
        command.Parameters.AddWithValue("id", user); command.Parameters.AddWithValue("permission", permission);
        if (await command.ExecuteScalarAsync(ct) is not true) throw new ApiException(403, "permission_denied", "No tienes permiso para esta operación.");
    }
    // Only exposed via the trusted operator CLI, never through an anonymous HTTP endpoint.
    public async Task BootstrapAdminAsync(string email, CancellationToken ct)
    {
        email = IdentityService.NormalizeEmail(email);
        await using var connection = await source.OpenConnectionAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        await IdentityStore.AdminGateAsync(connection, tx, ct);
        Guid id;
        await using (var query = new NpgsqlCommand("SELECT id FROM identity.users WHERE email=@email AND status='active'", connection, tx))
        { query.Parameters.AddWithValue("email", email); id = await query.ExecuteScalarAsync(ct) is Guid value ? value : throw new ApiException(404, "user_not_found", "Primero registra y verifica esta cuenta."); }
        await using (var insert = new NpgsqlCommand("INSERT INTO identity.user_roles(user_id,role_id) VALUES (@id,'admin') ON CONFLICT DO NOTHING", connection, tx))
        {
            insert.Parameters.AddWithValue("id", id);
            if (await insert.ExecuteNonQueryAsync(ct) > 0) await IdentityStore.AuditAsync(connection, tx, null, id, "admin.bootstrapped", new { }, "operator-cli", clock.GetUtcNow(), ct);
        }
        await tx.CommitAsync(ct);
    }
    public async Task<IReadOnlyList<SecurityAuditEntry>> AuditAsync(int limit, CancellationToken ct)
    {
        if (limit is < 1 or > 200) throw new ApiException(400, "invalid_limit", "El límite debe estar entre 1 y 200.");
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT id,actor_id,subject_id,action,data::text,correlation_id,occurred_at FROM identity.security_audit ORDER BY occurred_at DESC,id DESC LIMIT @limit
            """, connection);
        command.Parameters.AddWithValue("limit", limit); await using var reader = await command.ExecuteReaderAsync(ct); var result = new List<SecurityAuditEntry>();
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetGuid(2),
            reader.GetString(3), reader.GetString(4), reader.GetString(5), new(reader.GetDateTime(6))));
        return result;
    }
}

