using Delivery.BuildingBlocks;
using Delivery.Contracts;
using Npgsql;
using static Delivery.BuildingBlocks.ModuleSql;
namespace Delivery.Modules.Dispatch;
public sealed record CourierApplication(string Vehicle,string[] DocumentIds);
public sealed record CourierAvailability(bool Available,Guid ZoneId,Coordinate Location);
public sealed record CourierDecision(string Status,string Reason);
public sealed record OfferPreview(string RestaurantName,double? PickupKilometers,double DeliveryKilometers,long EstimatedEarnings,string Currency,string PaymentMethod,long CashToCollect);
public sealed record OfferInfo(Guid Id,Guid OrderId,Guid CourierId,string State,DateTimeOffset ExpiresAt)
{
 public OfferPreview? Preview {get;init;}
}
public sealed class DispatchService(NpgsqlDataSource source,IOrderDirectory orders,IMerchantDirectory merchants,ICoverageDirectory coverage,IIdentityDirectory identities):ICourierDirectory,IDispatchDirectory,IModuleMigration
{
 public Task ApplyAsync(CancellationToken ct)=>MigrateAsync(source,typeof(DispatchService).Assembly,"dispatch-001","Delivery.Modules.Dispatch.Infrastructure.001.sql",ct);
 public async Task ApplyAsync(Caller caller,CourierApplication request,CancellationToken ct)
 {
  Guard.Require(request.Vehicle is "bicycle" or "motorbike" or "car"&&request.DocumentIds is {Length:>0 and <=10}&&request.DocumentIds.All(id=>Guid.TryParse(id,out _)),"invalid_courier","Datos y documentos de repartidor inválidos.");
  await using var db=await source.OpenConnectionAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);
  await ExecuteAsync(db,tx,"INSERT INTO dispatch.couriers(id,vehicle,documents,status) VALUES (@id,@vehicle,@documents::jsonb,'pending') ON CONFLICT(id) DO NOTHING",ct,("id",caller.Id),("vehicle",request.Vehicle),("documents",Encode(request.DocumentIds)));
  await AuditAsync(db,tx,caller,"dispatch",caller.Id,"courier.applied","application",ct);await tx.CommitAsync(ct);
 }
 public async Task DecideAsync(Caller caller,Guid courier,CourierDecision request,CancellationToken ct)
 {
  caller.RequirePermission("participants.manage");Guard.Require(request.Status is "approved" or "rejected" or "suspended","invalid_courier_status","Estado inválido.");var reason=Guard.Text(request.Reason,"reason",500);
  await using var db=await source.OpenConnectionAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);
  await using var update=Command(db,tx,"UPDATE dispatch.couriers SET status=@status,available=false,decision_reason=@reason WHERE id=@id",("status",request.Status),("reason",reason),("id",courier));
  Guard.Require(await update.ExecuteNonQueryAsync(ct)==1,"courier_not_found","Repartidor inexistente.",404);
  await AuditAsync(db,tx,caller,"dispatch",courier,"courier.validated",reason,ct);await tx.CommitAsync(ct);
 }
 public async Task<bool> ApprovedAsync(Guid courier,CancellationToken ct)
 {
  if(!await identities.ActiveAsync(courier,ct))return false;
  await using var db=await source.OpenConnectionAsync(ct);await using var command=Command(db,null,"SELECT EXISTS(SELECT 1 FROM dispatch.couriers WHERE id=@id AND status='approved')",("id",courier));return await command.ExecuteScalarAsync(ct) is true;
 }
 public async Task AvailabilityAsync(Caller caller,CourierAvailability request,CancellationToken ct)
 {
  Guard.Require(request.Location is not null&&request.Location.IsValid,"invalid_location","Ubicación inválida.");
  var zone=await coverage.LocateAsync(request.Location!,ct);Guard.Require(zone.ZoneId==request.ZoneId,"invalid_zone","La ubicación no pertenece a la zona.");
  Guard.Require(await ApprovedAsync(caller.Id,ct),"courier_not_approved","Repartidor no aprobado.",403);
  await using var db=await source.OpenConnectionAsync(ct);
  await ExecuteAsync(db,null,"UPDATE dispatch.couriers SET available=@available,zone_id=@zone,latitude=@lat,longitude=@lon,located_at=now() WHERE id=@id AND status='approved'",ct,("available",request.Available),("zone",request.ZoneId),("lat",request.Location!.Latitude),("lon",request.Location.Longitude),("id",caller.Id));
 }
 public async Task<CourierCandidate[]> CandidatesAsync(Guid zone,CancellationToken ct)
 {
  await using var db=await source.OpenConnectionAsync(ct);await using var query=Command(db,null,"SELECT c.id,c.latitude,c.longitude,c.vehicle FROM dispatch.couriers c WHERE c.status='approved' AND c.available AND c.zone_id=@zone AND c.located_at>now()-interval '2 minutes' AND NOT EXISTS(SELECT 1 FROM dispatch.offers o WHERE o.courier_id=c.id AND o.state='accepted')",("zone",zone));
  var result=new List<CourierCandidate>();
  await using(var reader=await query.ExecuteReaderAsync(ct))while(await reader.ReadAsync(ct))result.Add(new(reader.GetGuid(0),new(reader.GetDouble(1),reader.GetDouble(2)),reader.GetString(3),1));
  await db.CloseAsync();
  var eligible=new List<CourierCandidate>();foreach(var candidate in result)if(await identities.ActiveAsync(candidate.Id,ct))eligible.Add(candidate);
  return eligible.ToArray();
 }
 public async Task OfferAsync(Guid order,CancellationToken ct)
 {
  var item=await orders.GetAsync(order,ct);if(item.State is not ("accepted" or "preparing" or "ready"))return;
  var branch=await merchants.BranchAsync(item.BranchId,ct);var zone=await coverage.LocateAsync(branch.Location,ct);
  await using var db=await source.OpenConnectionAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);await GateAsync(db,tx,"dispatch.assignment",ct);
  await ExecuteAsync(db,tx,"UPDATE dispatch.offers SET state='expired' WHERE order_id=@id AND state='pending' AND expires_at<=now()",ct,("id",order));
  await using(var active=Command(db,tx,"SELECT EXISTS(SELECT 1 FROM dispatch.offers WHERE order_id=@id AND state IN('pending','accepted'))",("id",order)))if(await active.ExecuteScalarAsync(ct) is true)return;
  var candidates=await CandidatesAsync(zone.ZoneId,ct);
  var rejected=new HashSet<Guid>();
  await using(var query=Command(db,tx,"SELECT courier_id FROM dispatch.offers WHERE order_id=@id AND state IN('rejected','expired')",("id",order)))
  await using(var reader=await query.ExecuteReaderAsync(ct))while(await reader.ReadAsync(ct))rejected.Add(reader.GetGuid(0));
  var courier=candidates.Where(c=>!rejected.Contains(c.Id)&&Geo.Kilometers(c.Location,branch.Location)<=20).OrderBy(c=>Geo.Kilometers(c.Location,branch.Location)).ThenBy(c=>c.Id).FirstOrDefault();
  if(courier is null){await tx.CommitAsync(ct);return;}
  var offer=Guid.CreateVersion7();
  await ExecuteAsync(db,tx,"INSERT INTO dispatch.offers(id,order_id,courier_id,state,expires_at) VALUES (@id,@order,@courier,'pending',now()+interval '60 seconds')",ct,("id",offer),("order",order),("courier",courier.Id));
  await AuditAsync(db,tx,new(Guid.Empty,false,order.ToString()),"dispatch",order,"delivery.offered","nearest eligible courier",ct);await tx.CommitAsync(ct);
 }
 public async Task<OfferInfo[]> OffersAsync(Caller caller,CancellationToken ct)
 {
  await using var db=await source.OpenConnectionAsync(ct);
  await using var query=Command(db,null,"SELECT o.id,o.order_id,o.courier_id,o.state,o.expires_at,c.latitude,c.longitude FROM dispatch.offers o LEFT JOIN dispatch.couriers c ON c.id=o.courier_id WHERE @admin OR o.courier_id=@user ORDER BY o.created_at DESC LIMIT 100",("admin",caller.Can("orders.operate")),("user",caller.Id));
  var rows=new List<(OfferInfo Offer,Coordinate? Location)>();
  await using(var reader=await query.ExecuteReaderAsync(ct))
   while(await reader.ReadAsync(ct)) rows.Add((new(reader.GetGuid(0),reader.GetGuid(1),reader.GetGuid(2),reader.GetString(3),new(reader.GetDateTime(4))),reader.IsDBNull(5)||reader.IsDBNull(6)?null:new(reader.GetDouble(5),reader.GetDouble(6))));
  var result=new List<OfferInfo>();
  foreach(var (offer,location) in rows)
  {
   var enriched=offer;
   if(offer.State=="pending"&&offer.ExpiresAt>DateTimeOffset.UtcNow)
   {
    var order=await orders.GetAsync(offer.OrderId,ct);
    if(order.State is "accepted" or "preparing" or "ready")
     enriched=offer with {Preview=new(order.Quote.Branch.Name,
      location is null?null:Math.Round(Geo.Kilometers(location,order.Quote.Branch.Location),1),
      Math.Round(Geo.Kilometers(order.Quote.Branch.Location,order.Quote.Address.Location),1),
      checked(order.Quote.CourierPay+order.Quote.Tip),order.Quote.Currency,order.Method,order.Method=="cash"?order.Quote.Total:0)};
   }
   result.Add(enriched);
  }
  return result.ToArray();
 }
 public async Task DecideOfferAsync(Caller caller,Guid offer,bool accept,CancellationToken ct)
 {
  Guard.Require(await ApprovedAsync(caller.Id,ct),"courier_not_approved","Repartidor no aprobado.",403);
  await using var db=await source.OpenConnectionAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);await GateAsync(db,tx,"dispatch.assignment",ct);
  Guid order;string state;DateTime expiry;
  await using(var query=Command(db,tx,"SELECT order_id,state,expires_at FROM dispatch.offers WHERE id=@id AND courier_id=@user FOR UPDATE",("id",offer),("user",caller.Id)))
  {await using var reader=await query.ExecuteReaderAsync(ct);Guard.Require(await reader.ReadAsync(ct),"offer_not_found","Oferta inexistente.",404);order=reader.GetGuid(0);state=reader.GetString(1);expiry=reader.GetDateTime(2);}
  if(state==(accept?"accepted":"rejected"))return;
  var current=await orders.GetAsync(order,ct);
  Guard.Require(state=="pending"&&expiry>DateTime.UtcNow&&current.State is "accepted" or "preparing" or "ready","offer_expired","Oferta vencida o no elegible.",409);
  if(accept)
  {
   await using var busy=Command(db,tx,"SELECT EXISTS(SELECT 1 FROM dispatch.offers WHERE courier_id=@user AND state='accepted')",("user",caller.Id));
   Guard.Require(await busy.ExecuteScalarAsync(ct) is not true,"courier_busy","Ya tienes una entrega activa.",409);
  }
  await ExecuteAsync(db,tx,"UPDATE dispatch.offers SET state=@state WHERE id=@id",ct,("state",accept?"accepted":"rejected"),("id",offer));
  if(accept)await Outbox.EnqueueAsync(db,tx,"dispatch.assigned.v1",new{OrderId=order,CourierId=caller.Id},caller.Correlation,ct);
  await AuditAsync(db,tx,caller,"dispatch",order,accept?"offer.accepted":"offer.rejected","courier decision",ct);await tx.CommitAsync(ct);
 }
 public async Task<Guid?> AssignedAsync(Guid order,CancellationToken ct)
 {
  await using var db=await source.OpenConnectionAsync(ct);await using var command=Command(db,null,"SELECT courier_id FROM dispatch.offers WHERE order_id=@id AND state IN('accepted','completed') ORDER BY created_at DESC LIMIT 1",("id",order));return await command.ExecuteScalarAsync(ct) is Guid id?id:null;
 }
 public async Task CompleteAsync(Guid order,CancellationToken ct)
 {
  await using var db=await source.OpenConnectionAsync(ct);await ExecuteAsync(db,null,"UPDATE dispatch.offers SET state='completed' WHERE order_id=@id AND state='accepted'",ct,("id",order));
 }
 public async Task CancelAsync(Guid order,CancellationToken ct)
 {
  await using var db=await source.OpenConnectionAsync(ct);await ExecuteAsync(db,null,"UPDATE dispatch.offers SET state='cancelled' WHERE order_id=@id AND state IN('pending','accepted')",ct,("id",order));
 }
 public async Task ReassignAsync(Caller caller,Guid order,Guid courier,string reason,IDeliveryDirectory deliveries,CancellationToken ct)
 {
  caller.RequirePermission("orders.operate");reason=Guard.Text(reason,"reason",500);
  await using var session=await SessionGateAsync(source,"fulfillment:"+order,ct);
  Guard.Require(await deliveries.CanReassignAsync(order,ct),"delivery_already_picked","Una entrega recogida no puede reasignarse.",409);
  var item=await orders.GetAsync(order,ct);Guard.Require(item.State is "accepted" or "preparing" or "ready","order_not_assignable","Pedido no asignable.",409);
  var zone=await coverage.LocateAsync(item.Quote.Branch.Location,ct);Guard.Require((await CandidatesAsync(zone.ZoneId,ct)).Any(c=>c.Id==courier),"courier_ineligible","Repartidor no elegible.",409);
  await using var db=await source.OpenConnectionAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);await GateAsync(db,tx,"dispatch.assignment",ct);
  await using(var busy=Command(db,tx,"SELECT EXISTS(SELECT 1 FROM dispatch.offers WHERE courier_id=@id AND state='accepted')",("id",courier)))Guard.Require(await busy.ExecuteScalarAsync(ct) is not true,"courier_busy","Repartidor ocupado.",409);
  await ExecuteAsync(db,tx,"UPDATE dispatch.offers SET state='cancelled' WHERE order_id=@id AND state IN('pending','accepted')",ct,("id",order));
  await ExecuteAsync(db,tx,"INSERT INTO dispatch.offers(id,order_id,courier_id,state,expires_at) VALUES (@id,@order,@courier,'accepted',now())",ct,("id",Guid.CreateVersion7()),("order",order),("courier",courier));
  await Outbox.EnqueueAsync(db,tx,"dispatch.assigned.v1",new{OrderId=order,CourierId=courier},caller.Correlation,ct);await AuditAsync(db,tx,caller,"dispatch",order,"courier.reassigned",reason,ct);await tx.CommitAsync(ct);
 }
 public async Task<object[]> CouriersAsync(Caller caller,CancellationToken ct)
 {
  caller.RequirePermission("participants.manage");await using var db=await source.OpenConnectionAsync(ct);await using var query=Command(db,null,"SELECT id,vehicle,status,available,documents FROM dispatch.couriers ORDER BY id LIMIT 200");
  await using var reader=await query.ExecuteReaderAsync(ct);var result=new List<object>();while(await reader.ReadAsync(ct))result.Add(new{Id=reader.GetGuid(0),Vehicle=reader.GetString(1),Status=reader.GetString(2),Available=reader.GetBoolean(3),DocumentIds=Decode<string[]>(reader.GetString(4))});return result.ToArray();
 }
}

