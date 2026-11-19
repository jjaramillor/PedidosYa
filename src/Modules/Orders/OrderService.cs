using System.Security.Cryptography;
using System.Text;
using Delivery.BuildingBlocks;
using Delivery.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;
using static Delivery.BuildingBlocks.ModuleSql;
namespace Delivery.Modules.Orders;
public sealed record CheckoutInput(Guid OperationId,Guid QuoteId,string Method,string? CardToken);
public sealed record OrderAction(Guid OperationId,string State,string Reason);
public sealed class OrderService(NpgsqlDataSource source,IPricingDirectory pricing,IMerchantDirectory merchants,IPaymentDirectory payments,ICoverageDirectory coverage,IDataProtectionProvider protection):IOrderDirectory,IModuleMigration
{
 private readonly IDataProtector protector=protection.CreateProtector("Delivery.Orders.PaymentToken.v1");
 public Task ApplyAsync(CancellationToken ct)=>MigrateAsync(source,typeof(OrderService).Assembly,"orders-001","Delivery.Modules.Orders.Infrastructure.001.sql",ct);
 public async Task<OrderView> PlaceAsync(Caller caller,CheckoutInput request,CancellationToken ct)
 {
  Guard.Require(request.OperationId!=Guid.Empty&&request.Method is "card" or "cash"&&(request.CardToken?.Length??0)<=1000,"invalid_checkout","Confirmación inválida.");
  Guard.Require(await coverage.FlagAsync("orders.enabled",ct),"ordering_disabled","Los pedidos están temporalmente suspendidos.",503);
  var fingerprint=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Encode(request))));
  await using var db=await source.OpenConnectionAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);await GateAsync(db,tx,"order:"+request.OperationId,ct);
  await using(var query=Command(db,tx,"SELECT customer_id,request_hash FROM orders.orders WHERE id=@id",("id",request.OperationId)))
  {
   await using var reader=await query.ExecuteReaderAsync(ct);
   if(await reader.ReadAsync(ct))
   {
    Guard.Require(reader.GetGuid(0)==caller.Id&&reader.GetString(1)==fingerprint,"checkout_key_conflict","La clave corresponde a otra confirmación.",409);
    await reader.DisposeAsync();return await GetAsync(request.OperationId,ct);
   }
  }
  var quote=await pricing.ReadQuoteAsync(caller.Id,request.QuoteId,ct);
  Guard.Require(quote.ExpiresAt>DateTimeOffset.UtcNow,"quote_expired","La cotización venció.",409);
  if(request.Method=="cash")
  {
   var zone=await coverage.LocateAsync(quote.Address.Location,ct);Guard.Require(zone.CashEnabled&&await coverage.FlagAsync("cash.enabled",ct),"cash_disabled","Efectivo no habilitado.",422);
  }
  else Guard.Require(!string.IsNullOrWhiteSpace(request.CardToken),"card_token_required","Falta token de tarjeta.");
  await ExecuteAsync(db,tx,"INSERT INTO orders.orders(id,customer_id,branch_id,state,quote,method,protected_token,request_hash) VALUES (@id,@customer,@branch,'payment_pending',@quote::jsonb,@method,@token,@hash)",ct,
   ("id",request.OperationId),("customer",caller.Id),("branch",quote.Branch.Id),("quote",Encode(quote)),("method",request.Method),("token",request.CardToken is null?null:protector.Protect(request.CardToken)),("hash",fingerprint));
  await Outbox.EnqueueAsync(db,tx,"orders.checkout-requested.v1",new{OrderId=request.OperationId},caller.Correlation,ct);
  await AuditAsync(db,tx,caller,"orders",request.OperationId,"order.created","checkout",ct);await tx.CommitAsync(ct);return await GetAsync(request.OperationId,ct);
 }
 public async Task<OrderView> GetAsync(Guid order,CancellationToken ct)
 {
  await using var db=await source.OpenConnectionAsync(ct);await using var query=Command(db,null,"SELECT id,customer_id,branch_id,state,quote,created_at,method FROM orders.orders WHERE id=@id",("id",order));
  await using var reader=await query.ExecuteReaderAsync(ct);if(!await reader.ReadAsync(ct))throw new ApiException(404,"order_not_found","Pedido inexistente.");
  return new(reader.GetGuid(0),reader.GetGuid(1),reader.GetGuid(2),reader.GetString(3),Decode<QuoteSnapshot>(reader.GetString(4)),new(reader.GetDateTime(5)),reader.GetString(6));
 }
 public async Task<bool> CanReadAsync(Caller caller,Guid order,CancellationToken ct)
 {
  var item=await GetAsync(order,ct);return caller.Can("orders.operate")||item.CustomerId==caller.Id||await merchants.CanOperateAsync(caller.Id,item.BranchId,ct);
 }
 public async Task<OrderView[]> ListAsync(Caller caller,Guid? branch,CancellationToken ct)
 {
  if(branch is Guid id)Guard.Require(caller.Can("orders.operate")||await merchants.CanOperateAsync(caller.Id,id,ct),"branch_access_denied","Sucursal no autorizada.",403);
  await using var db=await source.OpenConnectionAsync(ct);await using var query=Command(db,null,"SELECT id,customer_id,branch_id,state,quote,created_at,method FROM orders.orders WHERE (@admin OR customer_id=@user OR branch_id=@branch) AND (@all OR branch_id=@branch) ORDER BY created_at DESC LIMIT 200",("admin",caller.Can("orders.operate")),("user",caller.Id),("branch",branch??Guid.Empty),("all",branch is null));
  await using var reader=await query.ExecuteReaderAsync(ct);var result=new List<OrderView>();
  while(await reader.ReadAsync(ct))result.Add(new(reader.GetGuid(0),reader.GetGuid(1),reader.GetGuid(2),reader.GetString(3),Decode<QuoteSnapshot>(reader.GetString(4)),new(reader.GetDateTime(5)),reader.GetString(6)));
  return result.ToArray();
 }
 public async Task ActionAsync(Caller caller,Guid order,OrderAction request,CancellationToken ct)
 {
  var item=await GetAsync(order,ct);Guard.Text(request.Reason,"reason",500);
  var operatorAllowed=caller.Can("orders.operate")||await merchants.CanOperateAsync(caller.Id,item.BranchId,ct);
  if(request.State=="cancelled")
   Guard.Require(operatorAllowed||(item.CustomerId==caller.Id&&item.State is "payment_pending" or "confirmed" or "cancelled"),"cancel_not_allowed","Cancelación no autorizada.",403);
  else Guard.Require(operatorAllowed&&request.State is "accepted" or "preparing" or "ready","operation_not_allowed","Transición no autorizada.",403);
  await ExecuteTransitionAsync(order,request.State,caller.Id,request.Reason,request.OperationId,ct,
   request.State=="cancelled"&&!operatorAllowed?["payment_pending","confirmed"]:null);
 }
 public async Task TransitionAsync(Guid order,string target,Guid actor,string reason,Guid operation,CancellationToken ct)
    =>await ExecuteTransitionAsync(order,target,actor,reason,operation,ct,null);
 private async Task ExecuteTransitionAsync(Guid order,string target,Guid actor,string reason,Guid operation,CancellationToken ct,string[]? allowedFrom)
 {
  Guard.Require(operation!=Guid.Empty,"operation_required","Falta clave de operación.");reason=Guard.Text(reason,"reason",500);
  await using var db=await source.OpenConnectionAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);await GateAsync(db,tx,"order:"+order,ct);
  await using(var previous=Command(db,tx,"SELECT order_id,state FROM orders.operations WHERE id=@id",("id",operation)))
  {await using var reader=await previous.ExecuteReaderAsync(ct);if(await reader.ReadAsync(ct)){Guard.Require(reader.GetGuid(0)==order&&reader.GetString(1)==target,"operation_conflict","Clave de operación ya utilizada.",409);return;}}
  string state;QuoteSnapshot quote;
  await using(var query=Command(db,tx,"SELECT state,quote FROM orders.orders WHERE id=@id FOR UPDATE",("id",order)))
  {await using var reader=await query.ExecuteReaderAsync(ct);Guard.Require(await reader.ReadAsync(ct),"order_not_found","Pedido inexistente.",404);state=reader.GetString(0);quote=Decode<QuoteSnapshot>(reader.GetString(1));}
  if(state!=target)
  {
   Guard.Require(allowedFrom is null||allowedFrom.Contains(state),"cancellation_window_closed","El pedido ya no puede cancelarse por el cliente.",409);
   Guard.Require(OrderStates.Allowed(state,target),"invalid_order_transition","Transición de pedido no permitida.",409);
   var zone=target=="confirmed"?await coverage.LocateAsync(quote.Address.Location,ct):null;
   await ExecuteAsync(db,tx,"UPDATE orders.orders SET state=@state,updated_at=now(),accept_by=CASE WHEN @state='confirmed' THEN now()+make_interval(secs=>@seconds) ELSE accept_by END,protected_token=CASE WHEN @state IN('confirmed','cancelled') THEN NULL ELSE protected_token END WHERE id=@id",ct,("state",target),("seconds",zone?.AcceptanceSeconds??60),("id",order));
   await ExecuteAsync(db,tx,"INSERT INTO orders.transitions(id,order_id,actor_id,from_state,to_state,reason) VALUES (@id,@order,@actor,@from,@to,@reason)",ct,("id",Guid.CreateVersion7()),("order",order),("actor",actor),("from",state),("to",target),("reason",reason));
   await Outbox.EnqueueAsync(db,tx,"orders.state-changed.v1",new{OrderId=order,State=target,Previous=state},operation.ToString(),ct);
   await AuditAsync(db,tx,new(actor,false,operation.ToString()),"orders",order,"order."+target,reason,ct);
  }
  await ExecuteAsync(db,tx,"INSERT INTO orders.operations(id,order_id,state) VALUES (@id,@order,@state)",ct,("id",operation),("order",order),("state",target));await tx.CommitAsync(ct);
 }
 public async Task ProcessCheckoutAsync(Guid order,CancellationToken ct)
 {
  // Serialize the entire saga using a session advisory lock. Each module commits only its own data.
  await using var gate=await source.OpenConnectionAsync(ct);
  await using var acquire=Command(gate,null,"SELECT pg_advisory_lock(hashtextextended(@key,8))",("key","checkout:"+order));await acquire.ExecuteNonQueryAsync(ct);
  try
  {
   var item=await GetAsync(order,ct);if(item.State!="payment_pending")return;
   try
   {
    await pricing.ConfirmAsync(item.CustomerId,item.Quote.Id,order,ct);
    await merchants.ReserveStockAsync(order,item.BranchId,item.Quote.Lines.Select(l=>new BasketLine(l.ProductId,l.Quantity,l.Modifiers.Select(m=>m.Id).ToArray())).ToArray(),ct);
    string method;string? token;
    await using(var query=Command(gate,null,"SELECT method,protected_token FROM orders.orders WHERE id=@id",("id",order)))
    {await using var reader=await query.ExecuteReaderAsync(ct);await reader.ReadAsync(ct);method=reader.GetString(0);token=reader.IsDBNull(1)?null:protector.Unprotect(reader.GetString(1));}
    var result=await payments.CreateAsync(order,item.CustomerId,item.Quote.Total,item.Quote.Currency,method,token,ct);
    if(result.State is "authorized" or "captured")await TransitionAsync(order,"confirmed",Guid.Empty,"payment validated",Derive(order,"confirmed"),ct);
    else if(result.State=="rejected")await TransitionAsync(order,"cancelled",Guid.Empty,"payment declined",Derive(order,"declined"),ct);
   }
   catch(ApiException ex) when(ex.StatusCode<500)
   {await TransitionAsync(order,"cancelled",Guid.Empty,ex.Code,Derive(order,"checkout-failed"),ct);}
  }
  finally{await using var release=Command(gate,null,"SELECT pg_advisory_unlock(hashtextextended(@key,8))",("key","checkout:"+order));await release.ExecuteNonQueryAsync(CancellationToken.None);}
 }
 public async Task CompensateAsync(Guid order,CancellationToken ct)
 {
  await using var session=await SessionGateAsync(source,"checkout-compensation:"+order,ct);
  await using var checkoutDb=await source.OpenConnectionAsync(ct);
  await using var checkoutLock=Command(checkoutDb,null,"SELECT pg_advisory_lock(hashtextextended(@key,8))",("key","checkout:"+order));
  await checkoutLock.ExecuteNonQueryAsync(ct);
  try {
  await payments.CancelAsync(order,Derive(order,"cancel-payment"),ct);
  await using(var prepared=Command(checkoutDb,null,"SELECT EXISTS(SELECT 1 FROM orders.transitions WHERE order_id=@id AND to_state IN('preparing','ready','picked_up','in_transit'))",("id",order)))
   if(await prepared.ExecuteScalarAsync(ct) is not true)await merchants.ReleaseStockAsync(order,ct);
  await pricing.ReleaseCouponAsync(order,ct);
  } finally { await using var unlock=Command(checkoutDb,null,"SELECT pg_advisory_unlock(hashtextextended(@key,8))",("key","checkout:"+order));await unlock.ExecuteNonQueryAsync(CancellationToken.None); }
 }
 public async Task ExpireAcceptanceAsync(CancellationToken ct)
 {
  await using var db=await source.OpenConnectionAsync(ct);var ids=new List<Guid>();
  await using(var query=Command(db,null,"SELECT id FROM orders.orders WHERE state='confirmed' AND accept_by<now() LIMIT 100"))
  await using(var reader=await query.ExecuteReaderAsync(ct))while(await reader.ReadAsync(ct))ids.Add(reader.GetGuid(0));
  foreach(var id in ids)try{await ExecuteTransitionAsync(id,"cancelled",Guid.Empty,"acceptance timeout",Derive(id,"acceptance-timeout"),ct,["confirmed"]);}catch(ApiException ex) when(ex.StatusCode==409){}
 }
 public async Task<object[]> TimelineAsync(Guid order,CancellationToken ct)
 {
  await using var db=await source.OpenConnectionAsync(ct);await using var query=Command(db,null,"SELECT from_state,to_state,actor_id,reason,occurred_at FROM orders.transitions WHERE order_id=@id ORDER BY occurred_at",("id",order));await using var reader=await query.ExecuteReaderAsync(ct);var result=new List<object>();
  while(await reader.ReadAsync(ct))result.Add(new{From=reader.GetString(0),To=reader.GetString(1),Actor=reader.GetGuid(2),Reason=reader.GetString(3),At=reader.GetDateTime(4)});return result.ToArray();
 }
 public static Guid Derive(Guid id,string action)=>new(SHA256.HashData(Encoding.UTF8.GetBytes(id+":"+action)).AsSpan(0,16));
}

