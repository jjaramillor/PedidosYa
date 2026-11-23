using System.Security.Cryptography;
using System.Text;
using Delivery.BuildingBlocks;
using Delivery.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;
using static Delivery.BuildingBlocks.ModuleSql;
namespace Delivery.Modules.Deliveries;
public sealed record DeliveryAction(Guid OperationId,string State,string? Pin,Guid? EvidenceId,string Reason,bool CashCollected=false);
public sealed record DeliveryInfo(Guid OrderId,Guid CourierId,string State,string? PickupPin,string? DeliveryPin,Guid? EvidenceId,Coordinate? LastLocation,int? EtaMinutes);
public sealed class DeliveryService(NpgsqlDataSource source,IOrderDirectory orders,IMerchantDirectory merchants,IDispatchDirectory dispatch,IDocumentDirectory documents,IDataProtectionProvider protection):IDeliveryDirectory,IModuleMigration
{
 private readonly IDataProtector pins=protection.CreateProtector("Delivery.Deliveries.Pins.v1");
 public Task ApplyAsync(CancellationToken ct)=>MigrateAsync(source,typeof(DeliveryService).Assembly,"deliveries-001","Delivery.Modules.Deliveries.Infrastructure.001.sql",ct);
 public async Task CreateAsync(Guid order,Guid courier,Guid operation,CancellationToken ct)
 {
  await using var session=await SessionGateAsync(source,"fulfillment:"+order,ct);
  if(await dispatch.AssignedAsync(order,ct)!=courier)return;
  var current=await orders.GetAsync(order,ct);if(current.State is "cancelled" or "closed" or "returned")return;
  await using var db=await source.OpenConnectionAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);
  await using(var previous=Command(db,tx,"SELECT courier_id,state FROM deliveries.deliveries WHERE order_id=@id FOR UPDATE",("id",order)))
  {
   await using var reader=await previous.ExecuteReaderAsync(ct);
   if(await reader.ReadAsync(ct))
   {
    if(reader.GetGuid(0)==courier)return;
    Guard.Require(reader.GetString(1) is "assigned" or "arrived","already_picked_up","No se puede reasignar una entrega recogida.",409);
   }
  }
  await ExecuteAsync(db,tx,"""
   INSERT INTO deliveries.deliveries(order_id,courier_id,state,pickup_pin,drop_pin) VALUES (@order,@courier,'assigned',@pickup,@drop)
   ON CONFLICT(order_id) DO UPDATE SET courier_id=excluded.courier_id,state='assigned',pickup_pin=excluded.pickup_pin,
    pickup_attempts=0,updated_at=now()
   """,ct,("order",order),("courier",courier),("pickup",pins.Protect(RandomNumberGenerator.GetInt32(0,1000000).ToString("D6"))),("drop",pins.Protect(RandomNumberGenerator.GetInt32(0,1000000).ToString("D6"))));
  await AuditAsync(db,tx,new(Guid.Empty,false,operation.ToString()),"deliveries",order,"delivery.assigned","dispatch assignment",ct);await tx.CommitAsync(ct);
 }
 public async Task<bool> CanReassignAsync(Guid order,CancellationToken ct)
 {
  await using var db=await source.OpenConnectionAsync(ct);await using var query=Command(db,null,"SELECT state FROM deliveries.deliveries WHERE order_id=@id",("id",order));var state=await query.ExecuteScalarAsync(ct) as string;return state is null or "assigned" or "arrived";
 }
 public async Task<bool> CanReadAsync(Guid actor,Guid order,CancellationToken ct)
 {
  var item=await orders.GetAsync(order,ct);return item.CustomerId==actor||await merchants.CanOperateAsync(actor,item.BranchId,ct)||await dispatch.AssignedAsync(order,ct)==actor;
 }
 public async Task<DeliveryInfo> GetAsync(Caller caller,Guid order,CancellationToken ct)
 {
  Guard.Require(caller.Can("orders.operate")||await CanReadAsync(caller.Id,order,ct),"delivery_access_denied","Entrega no autorizada.",403);
  var item=await orders.GetAsync(order,ct);await using var db=await source.OpenConnectionAsync(ct);
  Guid courier;string state;string? pickup,drop;Guid? evidence;
  await using(var query=Command(db,null,"SELECT courier_id,state,pickup_pin,drop_pin,evidence_id FROM deliveries.deliveries WHERE order_id=@id",("id",order)))
  {
   await using var reader=await query.ExecuteReaderAsync(ct);if(!await reader.ReadAsync(ct))throw new ApiException(404,"delivery_not_assigned","Entrega todavía no asignada.");
   courier=reader.GetGuid(0);state=reader.GetString(1);
   var merchant=await merchants.CanOperateAsync(caller.Id,item.BranchId,ct);
   pickup=(merchant||caller.Can("orders.operate"))?pins.Unprotect(reader.GetString(2)):null;
   drop=(item.CustomerId==caller.Id||caller.Can("orders.operate"))?pins.Unprotect(reader.GetString(3)):null;
   evidence=reader.IsDBNull(4)?null:reader.GetGuid(4);
  }
  Coordinate? location=null;
  // Do not expose or collect courier position after delivery/cancellation.
  if(state is "assigned" or "arrived" or "picked_up" or "in_transit" or "incident")
  {
   await using var query=Command(db,null,"SELECT latitude,longitude FROM deliveries.locations WHERE order_id=@id AND recorded_at>now()-interval '5 minutes' ORDER BY recorded_at DESC LIMIT 1",("id",order));
   await using var reader=await query.ExecuteReaderAsync(ct);if(await reader.ReadAsync(ct))location=new(reader.GetDouble(0),reader.GetDouble(1));
  }
  var destination=state is "assigned" or "arrived"?item.Quote.Branch.Location:item.Quote.Address.Location;
  return new(order,courier,state,pickup,drop,evidence,location,location is null?null:Math.Max(1,(int)Math.Ceiling(Geo.Kilometers(location,destination)/20*60)));
 }
 public async Task UpdateLocationAsync(Caller caller,Guid order,Coordinate location,CancellationToken ct)
 {
  Guard.Require(location.IsValid,"invalid_location","Ubicación inválida.");await using var session=await SessionGateAsync(source,"fulfillment:"+order,ct);
  Guard.Require(await dispatch.AssignedAsync(order,ct)==caller.Id,"courier_not_assigned","No eres el repartidor asignado.",403);
  await using var db=await source.OpenConnectionAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);
  await using(var active=Command(db,tx,"SELECT EXISTS(SELECT 1 FROM deliveries.deliveries WHERE order_id=@id AND courier_id=@user AND state IN('assigned','arrived','picked_up','in_transit','incident'))",("id",order),("user",caller.Id)))
   Guard.Require(await active.ExecuteScalarAsync(ct) is true,"location_not_active","El seguimiento no está activo.",409);
  await ExecuteAsync(db,tx,"INSERT INTO deliveries.locations(id,order_id,courier_id,latitude,longitude) VALUES (@id,@order,@user,@lat,@lon)",ct,("id",Guid.CreateVersion7()),("order",order),("user",caller.Id),("lat",location.Latitude),("lon",location.Longitude));
  await Outbox.EnqueueAsync(db,tx,"deliveries.location-updated.v1",new{OrderId=order,location.Latitude,location.Longitude},caller.Correlation,ct);await tx.CommitAsync(ct);
 }
 public async Task ActionAsync(Caller caller,Guid order,DeliveryAction request,CancellationToken ct)
 {
  Guard.Require(request.OperationId!=Guid.Empty,"operation_required","Falta clave de operación.");var reason=Guard.Text(request.Reason,"reason",500);
  await using var session=await SessionGateAsync(source,"fulfillment:"+order,ct);
  Guard.Require(await dispatch.AssignedAsync(order,ct)==caller.Id,"courier_not_assigned","No eres el repartidor asignado.",403);
  var item=await orders.GetAsync(order,ct);await using var db=await source.OpenConnectionAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);
  await using(var previous=Command(db,tx,"SELECT order_id,state FROM deliveries.operations WHERE id=@id",("id",request.OperationId)))
  {await using var reader=await previous.ExecuteReaderAsync(ct);if(await reader.ReadAsync(ct)){Guard.Require(reader.GetGuid(0)==order&&reader.GetString(1)==request.State,"operation_conflict","Clave ya utilizada.",409);return;}}
  string state,pickup,drop;int attempts;
  await using(var query=Command(db,tx,"SELECT state,pickup_pin,drop_pin,CASE WHEN @pickup THEN pickup_attempts ELSE drop_attempts END FROM deliveries.deliveries WHERE order_id=@id AND courier_id=@user FOR UPDATE",("pickup",request.State=="picked_up"),("id",order),("user",caller.Id)))
  {await using var reader=await query.ExecuteReaderAsync(ct);Guard.Require(await reader.ReadAsync(ct),"delivery_not_found","Entrega inexistente.",404);state=reader.GetString(0);pickup=reader.GetString(1);drop=reader.GetString(2);attempts=reader.GetInt32(3);}
  var allowed=(state,request.State) switch
  {("assigned","arrived")=>true,("arrived","picked_up")=>item.State=="ready",("picked_up","in_transit")=>true,("in_transit","delivered")=>true,("in_transit","incident")=>true,("incident","in_transit")=>true,("incident","delivered")=>true,("incident","returned")=>true,_=>false};
  Guard.Require(allowed,"invalid_delivery_transition","Transición de entrega no permitida.",409);
  Guard.Require(request.State!="delivered"||item.Method!="cash"||request.CashCollected,"cash_not_collected","Confirma el importe recibido en efectivo antes de completar la entrega.",409);
  if(request.State is "picked_up" or "delivered")
  {
   var photographic=request.State=="delivered"&&request.EvidenceId is Guid file&&await documents.OwnsImageAsync(caller.Id,file,ct);
   if(!photographic)
   {
    var expected=pins.Unprotect(request.State=="picked_up"?pickup:drop);
    var matches=request.Pin is not null&&request.Pin.Length==6&&CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected),Encoding.UTF8.GetBytes(request.Pin));
    if(attempts>=5||!matches)
    {
     if(attempts<5)await ExecuteAsync(db,tx,"UPDATE deliveries.deliveries SET pickup_attempts=pickup_attempts+CASE WHEN @pickup THEN 1 ELSE 0 END,drop_attempts=drop_attempts+CASE WHEN @pickup THEN 0 ELSE 1 END WHERE order_id=@id",ct,("pickup",request.State=="picked_up"),("id",order));
     await tx.CommitAsync(ct);throw new ApiException(422,"invalid_delivery_pin","PIN inválido o bloqueado. Solicita soporte.");
    }
   }
   if(request.State=="delivered"&&request.EvidenceId is Guid evidence)
   {Guard.Require(await documents.OwnsImageAsync(caller.Id,evidence,ct),"invalid_evidence","Se requiere una fotografía propia de la entrega.");await documents.GrantAsync(caller.Id,evidence,[item.CustomerId],ct);}
  }
  await ExecuteAsync(db,tx,"UPDATE deliveries.deliveries SET state=@state,evidence_id=COALESCE(@evidence,evidence_id),delivered_at=CASE WHEN @state='delivered' THEN now() ELSE delivered_at END,updated_at=now() WHERE order_id=@id",ct,("state",request.State),("evidence",request.EvidenceId),("id",order));
  await ExecuteAsync(db,tx,"INSERT INTO deliveries.operations(id,order_id,state) VALUES (@id,@order,@state)",ct,("id",request.OperationId),("order",order),("state",request.State));
  await Outbox.EnqueueAsync(db,tx,"deliveries.state-changed.v1",new{OrderId=order,State=request.State,CourierId=caller.Id,OperationId=request.OperationId},caller.Correlation,ct);
  await AuditAsync(db,tx,caller,"deliveries",order,"delivery."+request.State,reason,ct);await tx.CommitAsync(ct);
 }
 public async Task CancelAsync(Guid order,CancellationToken ct)
 {
  await using var db=await source.OpenConnectionAsync(ct);await ExecuteAsync(db,null,"UPDATE deliveries.deliveries SET state='cancelled',updated_at=now() WHERE order_id=@id AND state IN('assigned','arrived')",ct,("id",order));
 }
 public async Task<Guid[]> ActiveOrdersAsync(Guid courier,CancellationToken ct)
 {
  await using var db=await source.OpenConnectionAsync(ct);await using var query=Command(db,null,"SELECT order_id FROM deliveries.deliveries WHERE courier_id=@user AND state NOT IN('cancelled','returned') ORDER BY updated_at DESC LIMIT 100",("user",courier));
  await using var reader=await query.ExecuteReaderAsync(ct);var result=new List<Guid>();while(await reader.ReadAsync(ct))result.Add(reader.GetGuid(0));return result.ToArray();
 }
 public async Task PurgeLocationsAsync(CancellationToken ct)
 {await using var db=await source.OpenConnectionAsync(ct);await ExecuteAsync(db,null,"DELETE FROM deliveries.locations WHERE recorded_at<now()-interval '24 hours'",ct);}
}

