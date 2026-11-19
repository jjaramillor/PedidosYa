using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NpgsqlTypes;
namespace Delivery.BuildingBlocks;
public interface IModuleMigration {Task ApplyAsync(CancellationToken ct);}
public static class ModuleSql
{
    public static NpgsqlCommand Command(NpgsqlConnection db,NpgsqlTransaction? tx,string sql,params (string Key,object? Value)[] parameters)
    {
        var command=new NpgsqlCommand(sql,db,tx);
        foreach(var(key,value) in parameters)
            if(value is null)command.Parameters.Add(new NpgsqlParameter(key,NpgsqlDbType.Unknown){Value=DBNull.Value});
            else command.Parameters.AddWithValue(key,value);
        return command;
    }
    public static async Task ExecuteAsync(NpgsqlConnection db,NpgsqlTransaction? tx,string sql,CancellationToken ct,params (string Key,object? Value)[] parameters)
    {await using var command=Command(db,tx,sql,parameters);await command.ExecuteNonQueryAsync(ct);}
    public static async Task GateAsync(NpgsqlConnection db,NpgsqlTransaction tx,string key,CancellationToken ct)
        =>await ExecuteAsync(db,tx,"SELECT pg_advisory_xact_lock(hashtextextended(@key,7))",ct,("key",key));
    public static async Task MigrateAsync(NpgsqlDataSource source,Assembly assembly,string version,string resource,CancellationToken ct)
    {
        await using var db=await source.OpenConnectionAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);
        await GateAsync(db,tx,"migrations",ct);
        await using(var exists=Command(db,tx,"SELECT EXISTS(SELECT 1 FROM platform.schema_versions WHERE version=@version)",("version",version)))
            if(await exists.ExecuteScalarAsync(ct) is true)return;
        using var stream=assembly.GetManifestResourceStream(resource)??throw new InvalidOperationException("Missing migration "+resource);
        using var reader=new StreamReader(stream);await ExecuteAsync(db,tx,await reader.ReadToEndAsync(ct),ct);
        await ExecuteAsync(db,tx,"INSERT INTO platform.schema_versions(version) VALUES (@version)",ct,("version",version));
        await tx.CommitAsync(ct);
    }
    public static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public static async Task<IAsyncDisposable> SessionGateAsync(NpgsqlDataSource source,string key,CancellationToken ct)
    {
        var db=await source.OpenConnectionAsync(ct);
        try {await ExecuteAsync(db,null,"SELECT pg_advisory_lock(hashtextextended(@key,9))",ct,("key",key));return new SessionGate(db,key);}
        catch {await db.DisposeAsync();throw;}
    }
    private sealed class SessionGate(NpgsqlConnection db,string key):IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try {await ExecuteAsync(db,null,"SELECT pg_advisory_unlock(hashtextextended(@key,9))",CancellationToken.None,("key",key));}
            finally {await db.DisposeAsync();}
        }
    }
    public static Task AuditAsync(NpgsqlConnection db,NpgsqlTransaction tx,Caller caller,string module,Guid subject,string action,string reason,CancellationToken ct)
        =>ExecuteAsync(db,tx,"INSERT INTO platform.audit(id,module,actor_id,subject_id,action,reason,correlation_id) VALUES (@id,@module,@actor,@subject,@action,@reason,@correlation)",ct,
            ("id",Guid.CreateVersion7()),("module",module),("actor",caller.Id),("subject",subject),("action",action),("reason",reason),("correlation",caller.Correlation));
    public static string Encode<T>(T value)=>JsonSerializer.Serialize(value,Json);
    public static T Decode<T>(string value)=>JsonSerializer.Deserialize<T>(value,Json)??throw new InvalidOperationException("Invalid persisted payload.");
}
public sealed record Caller(Guid Id,bool Administrator,string Correlation,string[]? Permissions=null)
{
    public static Caller From(HttpContext context)=>new(Guid.Parse(context.User.FindFirstValue("sub")!),context.User.IsInRole("admin"),context.TraceIdentifier,context.User.FindAll("permission").Select(c=>c.Value).ToArray());
    public bool Can(string permission)=>Administrator||(Permissions?.Contains(permission)??false);
    public void RequirePermission(string permission){if(!Can(permission))throw new ApiException(403,"permission_required","No tienes permiso para esta operación.");}
    public void RequireAdmin(){if(!Administrator)throw new ApiException(403,"admin_required","La operación requiere administración.");}
}
public static class Guard
{
    public static string Text(string? value,string field,int maximum=200)
    {
        var text=value?.Trim();
        if(string.IsNullOrWhiteSpace(text)||text.Length>maximum)throw new ApiException(400,"invalid_"+field,"Campo inválido: "+field,
            new Dictionary<string,string[]>{[field]=["Campo obligatorio, hasta "+maximum+" caracteres."]});
        return text;
    }
    public static void Require(bool condition,string code,string message,int status=400){if(!condition)throw new ApiException(status,code,message);}
}
