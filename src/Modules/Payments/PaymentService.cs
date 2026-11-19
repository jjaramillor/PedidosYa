using Delivery.BuildingBlocks;
using Delivery.Contracts;
using Microsoft.Extensions.Options;
using Npgsql;
using static Delivery.BuildingBlocks.ModuleSql;
namespace Delivery.Modules.Payments;
public sealed record RefundInput(Guid OperationId,long Amount,string Reason);
public sealed record PaymentInfo(Guid Id,Guid OrderId,long Amount,string Currency,string Method,string State,long Captured,long Refunded);
public sealed class PaymentService(NpgsqlDataSource source,IPaymentGateway gateway,IIdentityDirectory identities,ICoverageDirectory coverage,IOptions<PaymentOptions> options):IPaymentDirectory,IModuleMigration
{
 public async Task ApplyAsync(CancellationToken ct)
 {
  await MigrateAsync(source,typeof(PaymentService).Assembly,"payments-001","Delivery.Modules.Payments.Infrastructure.001.sql",ct);
  await MigrateAsync(source,typeof(PaymentService).Assembly,"payments-002","Delivery.Modules.Payments.Infrastructure.002.sql",ct);
 }
 public object Settings()=>new{Provider=options.Value.Provider,PublicKey=options.Value.PublicKey,TokenizationRequired=true};
 public async Task WebhookAsync(string providerId,string requestId,string signature,CancellationToken ct)
 {
  Guard.Require(options.Value.Provider=="mercadopago"&&WebhookSignature.Valid(options.Value.WebhookSecret,providerId,requestId,signature,DateTimeOffset.UtcNow),"invalid_webhook_signature","Notificación de pago no autenticada.",401);
  // The signed identifier is only a lookup key. Financial facts come from the provider API.
  var remote=await gateway.GetAsync(providerId,ct);
  await using var db=await source.OpenConnectionAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);await GateAsync(db,tx,"payment:"+remote.OrderId,ct);
  string state;Guid payment;
  await using(var query=Command(db,tx,"SELECT id,state,amount,currency,provider_id FROM payments.payments WHERE order_id=@order FOR UPDATE",("order",remote.OrderId)))
  {
   await using var reader=await query.ExecuteReaderAsync(ct);
   Guard.Require(await reader.ReadAsync(ct),"payment_not_found","El pago aún no fue registrado.",409);
   payment=reader.GetGuid(0);state=reader.GetString(1);
   Guard.Require(reader.GetInt64(2)==remote.Amount&&reader.GetString(3)==remote.Currency&&reader.GetString(4)==remote.Id&&remote.Id==providerId,"provider_mismatch","El pago no coincide con el pedido.",409);
  }
  if(state==remote.State||state is "cancelled" or "refunded" or "rejected"||state=="captured")return;
  if(remote.State is not ("authorized" or "captured" or "cancelled" or "rejected"))return;
  await ExecuteAsync(db,tx,"UPDATE payments.payments SET state=@state,captured=@captured,updated_at=now() WHERE id=@id",ct,("state",remote.State),("captured",remote.Captured),("id",payment));
  await Outbox.EnqueueAsync(db,tx,"payments.state-changed.v1",new{OrderId=remote.OrderId,State=remote.State},requestId,ct);
  await tx.CommitAsync(ct);
 }
 public async Task<PaymentResult> CreateAsync(Guid order,Guid customer,long amount,string currency,string method,string? encodedToken,CancellationToken ct)
 {
  Guard.Require(amount>=0&&currency=="PEN"&&method is "card" or "cash","invalid_payment","Pago inválido.");
  if(method=="cash")Guard.Require(await coverage.FlagAsync("cash.enabled",ct),"cash_disabled","El efectivo está deshabilitado.",422);
  await using var db=await source.OpenConnectionAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);await GateAsync(db,tx,"payment:"+order,ct);
  await using(var existing=Command(db,tx,"SELECT id,state,client_secret FROM payments.payments WHERE order_id=@order",("order",order)))
  {await using var reader=await existing.ExecuteReaderAsync(ct);if(await reader.ReadAsync(ct))return new(reader.GetGuid(0),reader.GetString(1),reader.IsDBNull(2)?null:reader.GetString(2));}
  GatewayPayment result;
  if(method=="cash")result=new("cash:"+order,"authorized",null,0,currency,amount,order);
  else
  {
   Guard.Require(encodedToken is not null&&encodedToken.Length<=1000,"card_token_required","Falta la tokenización de tarjeta.");
   CardToken token;try{token=Decode<CardToken>(encodedToken!);}catch(System.Text.Json.JsonException){throw new ApiException(400,"invalid_token","Token de tarjeta inválido.");}
   var email=await identities.EmailAsync(customer,ct)??throw new ApiException(403,"inactive_customer","Cuenta no activa.");
   result=await gateway.CreateAsync(order,amount,currency,email,token,ct);
   Guard.Require(result.OrderId==order&&result.Amount==amount&&result.Currency==currency,"provider_mismatch","Respuesta de pasarela incompatible.",502);
  }
  var id=Guid.CreateVersion7();
  await ExecuteAsync(db,tx,"INSERT INTO payments.payments(id,order_id,customer_id,amount,currency,method,state,provider_id,client_secret,captured) VALUES (@id,@order,@customer,@amount,@currency,@method,@state,@provider,@secret,@captured)",ct,
   ("id",id),("order",order),("customer",customer),("amount",amount),("currency",currency),("method",method),("state",result.State),("provider",result.Id),("secret",result.ClientSecret),("captured",result.Captured));
  await tx.CommitAsync(ct);return new(id,result.State,result.ClientSecret);
 }
 public async Task<PaymentInfo> InfoAsync(Guid order,CancellationToken ct)
 {
  await using var db=await source.OpenConnectionAsync(ct);await using var command=Command(db,null,"SELECT p.id,p.order_id,p.amount,p.currency,p.method,p.state,p.captured,COALESCE((SELECT sum(amount) FROM payments.refunds r WHERE r.payment_id=p.id AND r.state='completed'),0)::bigint FROM payments.payments p WHERE p.order_id=@order",("order",order));
  await using var reader=await command.ExecuteReaderAsync(ct);if(!await reader.ReadAsync(ct))throw new ApiException(404,"payment_not_found","Pago inexistente.");
  return new(reader.GetGuid(0),reader.GetGuid(1),reader.GetInt64(2),reader.GetString(3),reader.GetString(4),reader.GetString(5),reader.GetInt64(6),reader.GetInt64(7));
 }
 public async Task CaptureAsync(Guid order,Guid operation,CancellationToken ct)=>await ChangeAsync(order,operation,true,ct);
 public async Task CancelAsync(Guid order,Guid operation,CancellationToken ct)=>await ChangeAsync(order,operation,false,ct);
 private async Task ChangeAsync(Guid order,Guid operation,bool capture,CancellationToken ct)
 {
  await using var db=await source.OpenConnectionAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);await GateAsync(db,tx,"payment:"+order,ct);
  Guid id;string provider,method,state;long amount,captured;
  await using(var query=Command(db,tx,"SELECT id,provider_id,method,state,amount,captured FROM payments.payments WHERE order_id=@order FOR UPDATE",("order",order)))
  {
   await using var reader=await query.ExecuteReaderAsync(ct);if(!await reader.ReadAsync(ct))return;
   id=reader.GetGuid(0);provider=reader.GetString(1);method=reader.GetString(2);state=reader.GetString(3);amount=reader.GetInt64(4);captured=reader.GetInt64(5);
  }
  if(capture&&captured==amount&&state=="captured")return;
  if(!capture&&state is "cancelled" or "refunded" or "rejected")return;
  if(capture)
  {
   Guard.Require(state=="authorized","payment_not_authorized","El pago no está autorizado.",409);
   if(method=="card")await gateway.CaptureAsync(provider,operation,ct);
   await ExecuteAsync(db,tx,"UPDATE payments.payments SET state='captured',captured=amount,updated_at=now() WHERE id=@id",ct,("id",id));
  }
  else if(captured>0)
  {
   long refunded;await using(var query=Command(db,tx,"SELECT COALESCE(sum(amount),0)::bigint FROM payments.refunds WHERE payment_id=@id AND state='completed'",("id",id)))refunded=Convert.ToInt64(await query.ExecuteScalarAsync(ct));
   var remaining=captured-refunded;
   if(remaining>0)
   {
    if(method=="card")await gateway.RefundAsync(provider,remaining,operation,ct);
    await ExecuteAsync(db,tx,"INSERT INTO payments.refunds(id,payment_id,amount,state,reason,actor_id) VALUES (@operation,@id,@amount,'completed','cancellation',@actor) ON CONFLICT DO NOTHING",ct,("operation",operation),("id",id),("amount",remaining),("actor",Guid.Empty));
    await Outbox.EnqueueAsync(db,tx,"payments.refunded.v1",new{OrderId=order,Amount=remaining,OperationId=operation},operation.ToString(),ct);
   }
   await ExecuteAsync(db,tx,"UPDATE payments.payments SET state='refunded',updated_at=now() WHERE id=@id",ct,("id",id));
  }
  else
  {
   if(method=="card")await gateway.CancelAsync(provider,operation,ct);
   await ExecuteAsync(db,tx,"UPDATE payments.payments SET state='cancelled',updated_at=now() WHERE id=@id",ct,("id",id));
  }
  await tx.CommitAsync(ct);
 }
 public async Task RefundAsync(Caller caller,Guid order,RefundInput request,CancellationToken ct)
 {
  Guard.Require(caller.Can("finance.manage")||caller.Can("payments.refund"),"refund_access_denied","No puedes autorizar reembolsos.",403);Guard.Require(request.OperationId!=Guid.Empty&&request.Amount>0,"invalid_refund","Reembolso inválido.");var reason=Guard.Text(request.Reason,"reason",500);
  await using var db=await source.OpenConnectionAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);await GateAsync(db,tx,"payment:"+order,ct);
  var info=await InfoAsync(order,ct);
  await using(var previous=Command(db,tx,"SELECT payment_id,amount FROM payments.refunds WHERE id=@id",("id",request.OperationId)))
  {
   await using var reader=await previous.ExecuteReaderAsync(ct);if(await reader.ReadAsync(ct)){Guard.Require(reader.GetGuid(0)==info.Id&&reader.GetInt64(1)==request.Amount,"refund_key_conflict","La clave fue utilizada con otra operación.",409);return;}
  }
  Guard.Require(request.Amount<=info.Captured-info.Refunded,"refund_exceeds_capture","La devolución supera el cobro disponible.",409);
  await using(var query=Command(db,tx,"SELECT provider_id FROM payments.payments WHERE id=@id",("id",info.Id)))
  {
   var provider=(string)(await query.ExecuteScalarAsync(ct))!;
   if(info.Method=="card")await gateway.RefundAsync(provider,request.Amount,request.OperationId,ct);
  }
  await ExecuteAsync(db,tx,"INSERT INTO payments.refunds(id,payment_id,amount,state,reason,actor_id) VALUES (@id,@payment,@amount,'completed',@reason,@actor)",ct,("id",request.OperationId),("payment",info.Id),("amount",request.Amount),("reason",reason),("actor",caller.Id));
  if(info.Refunded+request.Amount==info.Captured)await ExecuteAsync(db,tx,"UPDATE payments.payments SET state='refunded',updated_at=now() WHERE id=@id",ct,("id",info.Id));
  await Outbox.EnqueueAsync(db,tx,"payments.refunded.v1",new{OrderId=order,Amount=request.Amount,OperationId=request.OperationId},caller.Correlation,ct);
  await AuditAsync(db,tx,caller,"payments",order,"payment.refunded",reason,ct);await tx.CommitAsync(ct);
 }
 public async Task<object[]> ReconcileAsync(Caller caller,CancellationToken ct)
 {
  caller.RequirePermission("finance.manage");await using var db=await source.OpenConnectionAsync(ct);var ids=new List<(Guid Id,string Provider,Guid Order)>();
  await using(var query=Command(db,null,"SELECT id,provider_id,order_id FROM payments.payments WHERE method='card' ORDER BY updated_at DESC LIMIT 100"))
  await using(var reader=await query.ExecuteReaderAsync(ct))while(await reader.ReadAsync(ct))ids.Add((reader.GetGuid(0),reader.GetString(1),reader.GetGuid(2)));
  var list=new List<object>();foreach(var payment in ids)
  {
   var remote=await gateway.GetAsync(payment.Provider,ct);var local=await InfoAsync(payment.Order,ct);var simulated=options.Value.Provider=="sandbox";
   var matches=!simulated&&local.State==remote.State&&local.Amount==remote.Amount&&local.Captured==remote.Captured&&local.Refunded==remote.Refunded&&local.Currency==remote.Currency&&remote.OrderId==payment.Order;
   await ExecuteAsync(db,null,"INSERT INTO payments.reconciliations(id,payment_id,local_state,provider_state,matches,local_amount,provider_amount,local_refunded,provider_refunded) VALUES (@id,@payment,@local,@remote,@matches,@amount,@remoteAmount,@refunded,@remoteRefunded)",ct,("id",Guid.CreateVersion7()),("payment",payment.Id),("local",local.State),("remote",simulated?"simulated":remote.State),("matches",matches),("amount",local.Amount),("remoteAmount",remote.Amount),("refunded",local.Refunded),("remoteRefunded",remote.Refunded));
   list.Add(new{PaymentId=payment.Id,Local=local.State,Remote=remote.State,Matches=matches,Simulated=simulated,LocalAmount=local.Amount,ProviderAmount=remote.Amount,LocalRefunded=local.Refunded,ProviderRefunded=remote.Refunded});
  }
  return list.ToArray();
 }
}

