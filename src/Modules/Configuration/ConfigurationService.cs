using Delivery.BuildingBlocks;
using Delivery.Contracts;
using Npgsql;
using static Delivery.BuildingBlocks.ModuleSql;
namespace Delivery.Modules.Configuration;
public sealed record SaveZone(string Name,string PolygonGeoJson,bool Active,long DeliveryBase,long PerKilometer,long ServiceFee,int CommissionBasisPoints,long CourierBase,int AcceptanceSeconds,bool CashEnabled,string Reason);
public sealed record ZoneInfo(Guid Id,SaveZone Configuration);
public sealed record SaveParameter(System.Text.Json.JsonElement Value,string Reason);
public sealed class ConfigurationService(NpgsqlDataSource source):ICoverageDirectory,IModuleMigration
{
 public Task ApplyAsync(CancellationToken ct)=>MigrateAsync(source,typeof(ConfigurationService).Assembly,"configuration-001","Delivery.Modules.Configuration.Infrastructure.001.sql",ct);
 public async Task<Guid> SaveAsync(Caller caller,Guid id,SaveZone request,CancellationToken ct)
 {
  caller.RequirePermission("configuration.manage");var name=Guard.Text(request.Name,"name");var reason=Guard.Text(request.Reason,"reason",500);
  Guard.Require(request.DeliveryBase>=0&&request.PerKilometer>=0&&request.ServiceFee>=0&&request.CourierBase>=0&&request.CommissionBasisPoints is >=0 and <=10000&&request.AcceptanceSeconds is >=30 and <=3600,"invalid_tariffs","Tarifas o tiempos inválidos.");
  Guard.Require(request.PolygonGeoJson is not null&&request.PolygonGeoJson.Length<=100000,"invalid_polygon","Polígono inválido.");
  await using var db=await source.OpenConnectionAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);
  try
  {
   await using var geometry=Command(db,tx,"SELECT ST_IsValid(g) AND ST_GeometryType(g)='ST_Polygon' AND ST_XMin(g)>=-180 AND ST_XMax(g)<=180 AND ST_YMin(g)>=-90 AND ST_YMax(g)<=90 FROM (SELECT ST_SetSRID(ST_GeomFromGeoJSON(@polygon),4326) g) p",("polygon",request.PolygonGeoJson));
   Guard.Require(await geometry.ExecuteScalarAsync(ct) is true,"invalid_polygon","Polígono inválido.");
   await ExecuteAsync(db,tx,"""
    INSERT INTO configuration.zones(id,name,polygon,active,delivery_base,per_kilometer,service_fee,commission_bps,courier_base,acceptance_seconds,cash_enabled)
    VALUES (@id,@name,ST_SetSRID(ST_GeomFromGeoJSON(@polygon),4326),@active,@delivery,@km,@service,@commission,@courier,@seconds,@cash)
    ON CONFLICT(id) DO UPDATE SET name=excluded.name,polygon=excluded.polygon,active=excluded.active,delivery_base=excluded.delivery_base,
    per_kilometer=excluded.per_kilometer,service_fee=excluded.service_fee,commission_bps=excluded.commission_bps,courier_base=excluded.courier_base,acceptance_seconds=excluded.acceptance_seconds,cash_enabled=excluded.cash_enabled
    """,ct,("id",id),("name",name),("polygon",request.PolygonGeoJson),("active",request.Active),("delivery",request.DeliveryBase),("km",request.PerKilometer),("service",request.ServiceFee),("commission",request.CommissionBasisPoints),("courier",request.CourierBase),("seconds",request.AcceptanceSeconds),("cash",request.CashEnabled));
  }
  catch(PostgresException ex) when(ex.SqlState=="XX000"||ex.SqlState.StartsWith("22",StringComparison.Ordinal)){throw new ApiException(400,"invalid_polygon","GeoJSON inválido.");}
  await AuditAsync(db,tx,caller,"configuration",id,"zone.saved",reason,ct);await tx.CommitAsync(ct);return id;
 }
 public async Task<CoverageSnapshot> LocateAsync(Coordinate point,CancellationToken ct)
 {
  Guard.Require(point.IsValid,"invalid_location","Ubicación inválida.");
  await using var db=await source.OpenConnectionAsync(ct);
  await using var command=Command(db,null,"""
   SELECT id,name,delivery_base,per_kilometer,service_fee,commission_bps,courier_base,acceptance_seconds,cash_enabled
   FROM configuration.zones WHERE active AND ST_Covers(polygon,ST_SetSRID(ST_MakePoint(@lon,@lat),4326))
   ORDER BY ST_Area(polygon),id LIMIT 1
   """,("lon",point.Longitude),("lat",point.Latitude));
  await using var reader=await command.ExecuteReaderAsync(ct);
  if(!await reader.ReadAsync(ct))throw new ApiException(422,"outside_coverage","La dirección está fuera de cobertura.");
  return new(reader.GetGuid(0),reader.GetString(1),reader.GetInt64(2),reader.GetInt64(3),reader.GetInt64(4),reader.GetInt32(5),reader.GetInt64(6),reader.GetInt32(7),reader.GetBoolean(8));
 }
 public async Task<bool> FlagAsync(string name,CancellationToken ct)
 {
  await using var db=await source.OpenConnectionAsync(ct);await using var command=Command(db,null,"SELECT value::text FROM configuration.parameters WHERE name=@name",("name",name));
  return await command.ExecuteScalarAsync(ct) as string=="true";
 }
 public async Task SaveParameterAsync(Caller caller,string name,SaveParameter request,CancellationToken ct)
 {
  caller.RequirePermission("configuration.manage");name=Guard.Text(name,"name",100);var reason=Guard.Text(request.Reason,"reason",500);
  if(name.EndsWith(".enabled",StringComparison.Ordinal))Guard.Require(request.Value.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False,"invalid_flag","Un feature flag requiere un booleano.");
  Guard.Require(request.Value.GetRawText().Length<=10000,"invalid_parameter","Parámetro demasiado grande.");
  await using var db=await source.OpenConnectionAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);
  await ExecuteAsync(db,tx,"INSERT INTO configuration.parameters(name,value) VALUES (@name,@value::jsonb) ON CONFLICT(name) DO UPDATE SET value=excluded.value,updated_at=now()",ct,("name",name),("value",request.Value.GetRawText()));
  await AuditAsync(db,tx,caller,"configuration",Guid.Empty,"parameter.saved",reason,ct);await tx.CommitAsync(ct);
 }
 public async Task<object[]> ZonesAsync(CancellationToken ct)
 {
  await using var db=await source.OpenConnectionAsync(ct);await using var command=Command(db,null,"SELECT id,name,ST_AsGeoJSON(polygon),active,delivery_base,per_kilometer,service_fee,commission_bps,courier_base,acceptance_seconds,cash_enabled FROM configuration.zones ORDER BY name");
  await using var reader=await command.ExecuteReaderAsync(ct);var result=new List<object>();
  while(await reader.ReadAsync(ct))result.Add(new ZoneInfo(reader.GetGuid(0),new(reader.GetString(1),reader.GetString(2),reader.GetBoolean(3),reader.GetInt64(4),reader.GetInt64(5),reader.GetInt64(6),reader.GetInt32(7),reader.GetInt64(8),reader.GetInt32(9),reader.GetBoolean(10),"")));
  return result.ToArray();
 }
 public async Task<object[]> AuditLogAsync(Caller caller,int limit,CancellationToken ct)
 {
  caller.RequirePermission("platform.audit.read");Guard.Require(limit is >=1 and <=200,"invalid_limit","Límite inválido.");
  await using var db=await source.OpenConnectionAsync(ct);await using var command=Command(db,null,"SELECT id,module,actor_id,subject_id,action,reason,correlation_id,occurred_at FROM platform.audit ORDER BY occurred_at DESC LIMIT @limit",("limit",limit));
  await using var reader=await command.ExecuteReaderAsync(ct);var result=new List<object>();
  while(await reader.ReadAsync(ct))result.Add(new {Id=reader.GetGuid(0),Module=reader.GetString(1),Actor=reader.GetGuid(2),Subject=reader.GetGuid(3),Action=reader.GetString(4),Reason=reader.GetString(5),CorrelationId=reader.GetString(6),OccurredAt=reader.GetDateTime(7)});
  return result.ToArray();
 }
 public async Task<object[]> ParametersAsync(Caller caller,CancellationToken ct)
 {
  caller.RequirePermission("configuration.manage");await using var db=await source.OpenConnectionAsync(ct);await using var command=Command(db,null,"SELECT name,value::text FROM configuration.parameters ORDER BY name");
  await using var reader=await command.ExecuteReaderAsync(ct);var result=new List<object>();
  while(await reader.ReadAsync(ct))result.Add(new{Name=reader.GetString(0),Value=System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(reader.GetString(1))});
  return result.ToArray();
 }
}


