using System.Security.Cryptography;
using System.Text;
using Delivery.BuildingBlocks;
using Delivery.Contracts;
using Npgsql;
using static Delivery.BuildingBlocks.ModuleSql;
namespace Delivery.Modules.Ledger;
public sealed record Distribution(long Merchant,long Courier,long Platform);
public sealed record SettlementInfo(Guid Id,string Account,string Currency,long Amount,DateTimeOffset ClosedAt,DateTimeOffset? PaidAt,string? ExternalReference);
public sealed class LedgerService(NpgsqlDataSource source,IMerchantDirectory merchants,IDispatchDirectory dispatch):ILedgerDirectory,IModuleMigration
{
 public Task ApplyAsync(CancellationToken ct)=>MigrateAsync(source,typeof(LedgerService).Assembly,"ledger-001","Delivery.Modules.Ledger.Infrastructure.001.sql",ct);
 public static Distribution Distribute(QuoteSnapshot quote)
 {
  var commissionBase=quote.Subtotal-(quote.MerchantFundsDiscount?quote.Discount:0);
  var merchant=checked(commissionBase-MoneyMath.BasisPoints(commissionBase,quote.CommissionBasisPoints));
  var courier=checked(quote.CourierPay+quote.Tip);return new(merchant,courier,checked(quote.Total-merchant-courier));
 }
 public async Task RecordDeliveryAsync(OrderView order,Guid courier,Guid operation,CancellationToken ct,bool cash=false)
 {
  var distribution=Distribute(order.Quote);await using var db=await source.OpenConnectionAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);await GateAsync(db,tx,"ledger:"+order.Id,ct);
  await using(var exists=Command(db,tx,"SELECT EXISTS(SELECT 1 FROM ledger.deliveries WHERE order_id=@id)",("id",order.Id)))if(await exists.ExecuteScalarAsync(ct) is true)return;
  await PostAsync(db,tx,Derive(order.Id,"capture"),order.Id,"capture",order.Quote.Currency,[(cash?"courier:"+courier:"customer-clearing",-order.Quote.Total),("unallocated",order.Quote.Total)],ct);
  long priorRefunds;
  await using(var refunds=Command(db,tx,"SELECT COALESCE(sum(l.amount),0)::bigint FROM ledger.lines l JOIN ledger.vouchers v ON v.id=l.voucher_id WHERE v.order_id=@order AND v.kind='refund' AND l.account='customer-clearing'",("order",order.Id)))priorRefunds=Convert.ToInt64(await refunds.ExecuteScalarAsync(ct));
  Guard.Require(priorRefunds<=order.Quote.Total,"refund_exceeds_total","Devoluciones excesivas.",409);
  var merchantNet=distribution.Merchant-(priorRefunds==0?0:Proportion(distribution.Merchant,priorRefunds,order.Quote.Total));
  var courierNet=distribution.Courier-(priorRefunds==0?0:Proportion(distribution.Courier,priorRefunds,order.Quote.Total));
  var remaining=order.Quote.Total-priorRefunds;
  await PostAsync(db,tx,Derive(operation,"delivery"),order.Id,"delivery",order.Quote.Currency,[("unallocated",-remaining),("merchant:"+order.Quote.Branch.MerchantId,merchantNet),("courier:"+courier,courierNet),("platform",remaining-merchantNet-courierNet)],ct);
  await ExecuteAsync(db,tx,"INSERT INTO ledger.deliveries(order_id,merchant_id,courier_id,total,merchant_amount,courier_amount,platform_amount,refunded) VALUES (@order,@merchant,@courier,@total,@m,@c,@p,@refunded)",ct,("order",order.Id),("merchant",order.Quote.Branch.MerchantId),("courier",courier),("total",order.Quote.Total),("m",distribution.Merchant),("c",distribution.Courier),("p",distribution.Platform),("refunded",priorRefunds));await tx.CommitAsync(ct);
 }
 public async Task RefundAsync(OrderView order,long amount,Guid operation,CancellationToken ct)
 {
  operation=Derive(operation,"refund");
  Guard.Require(amount>0&&amount<=order.Quote.Total,"invalid_refund","Reembolso inválido.");
  await using var db=await source.OpenConnectionAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);await GateAsync(db,tx,"ledger:"+order.Id,ct);
  await using(var existing=Command(db,tx,"SELECT EXISTS(SELECT 1 FROM ledger.vouchers WHERE id=@id)",("id",operation)))if(await existing.ExecuteScalarAsync(ct) is true)return;
  Guid merchant=Guid.Empty,courier=Guid.Empty;long total=0,merchantTotal=0,courierTotal=0,refunded=0;bool allocated=false;
  await using(var query=Command(db,tx,"SELECT merchant_id,courier_id,total,merchant_amount,courier_amount,refunded FROM ledger.deliveries WHERE order_id=@id FOR UPDATE",("id",order.Id)))
  {
   await using var reader=await query.ExecuteReaderAsync(ct);allocated=await reader.ReadAsync(ct);
   if(allocated){merchant=reader.GetGuid(0);courier=reader.GetGuid(1);total=reader.GetInt64(2);merchantTotal=reader.GetInt64(3);courierTotal=reader.GetInt64(4);refunded=reader.GetInt64(5);}
  }
  var captureAccount="customer-clearing";
  if(order.Method=="cash")
  {
   var collectingCourier=allocated?courier:await dispatch.AssignedAsync(order.Id,ct);
   Guard.Require(collectingCourier is not null,"cash_courier_missing","No existe un repartidor responsable del efectivo.",409);
   captureAccount="courier:"+collectingCourier;
  }
  await PostAsync(db,tx,Derive(order.Id,"capture"),order.Id,"capture",order.Quote.Currency,[(captureAccount,-order.Quote.Total),("unallocated",order.Quote.Total)],ct);
  if(allocated)
  {
   Guard.Require(refunded+amount<=total,"refund_exceeds_total","Devolución acumulada excesiva.",409);
   var m=Proportion(merchantTotal,refunded+amount,total)-Proportion(merchantTotal,refunded,total);
   var c=Proportion(courierTotal,refunded+amount,total)-Proportion(courierTotal,refunded,total);
   var p=checked(amount-m-c);
   await PostAsync(db,tx,operation,order.Id,"refund",order.Quote.Currency,[("merchant:"+merchant,-m),("courier:"+courier,-c),("platform",-p),("customer-clearing",amount)],ct);
   await ExecuteAsync(db,tx,"UPDATE ledger.deliveries SET refunded=refunded+@amount WHERE order_id=@id",ct,("id",order.Id),("amount",amount));
  }
  else
  {
   long previousRefunds;await using(var query=Command(db,tx,"SELECT COALESCE(sum(l.amount),0)::bigint FROM ledger.lines l JOIN ledger.vouchers v ON v.id=l.voucher_id WHERE v.order_id=@order AND v.kind='refund' AND account='customer-clearing'",("order",order.Id)))previousRefunds=Convert.ToInt64(await query.ExecuteScalarAsync(ct));
   Guard.Require(previousRefunds+amount<=order.Quote.Total,"refund_exceeds_total","Devolución acumulada excesiva.",409);
   await PostAsync(db,tx,operation,order.Id,"refund",order.Quote.Currency,[("unallocated",-amount),("customer-clearing",amount)],ct);
  }
  await tx.CommitAsync(ct);
 }
 private static long Proportion(long amount,long numerator,long denominator)=>checked((long)decimal.Round(amount*(decimal)numerator/denominator,0,MidpointRounding.AwayFromZero));
 private static async Task PostAsync(NpgsqlConnection db,NpgsqlTransaction tx,Guid id,Guid order,string kind,string currency,(string Account,long Amount)[] lines,CancellationToken ct)
 {
  Guard.Require(lines.Sum(l=>l.Amount)==0,"unbalanced_ledger","Asiento financiero no balanceado.",500);
  await using var command=Command(db,tx,"INSERT INTO ledger.vouchers(id,order_id,kind,currency) VALUES (@id,@order,@kind,@currency) ON CONFLICT DO NOTHING",("id",id),("order",order),("kind",kind),("currency",currency));
  if(await command.ExecuteNonQueryAsync(ct)==0)return;
  foreach(var line in lines.Where(l=>l.Amount!=0))await ExecuteAsync(db,tx,"INSERT INTO ledger.lines(id,voucher_id,account,amount) VALUES (@id,@voucher,@account,@amount)",ct,("id",Guid.CreateVersion7()),("voucher",id),("account",line.Account),("amount",line.Amount));
 }
 private async Task AccessAsync(Caller caller,string kind,Guid participant,CancellationToken ct)
 {
  Guard.Require(kind is "merchant" or "courier","invalid_account","Cuenta inválida.");
  Guard.Require(caller.Can("finance.manage")||(kind=="courier"?participant==caller.Id:await merchants.OwnsBusinessAsync(caller.Id,participant,ct)),"finance_access_denied","Cuenta no autorizada.",403);
 }
 public async Task<object> StatementAsync(Caller caller,string kind,Guid participant,CancellationToken ct)
 {
  await AccessAsync(caller,kind,participant,ct);var account=kind+":"+participant;
  await using var db=await source.OpenConnectionAsync(ct);var lines=new List<object>();
  await using(var query=Command(db,null,"SELECT l.id,v.order_id,v.kind,l.amount,l.created_at FROM ledger.lines l JOIN ledger.vouchers v ON v.id=l.voucher_id WHERE l.account=@account ORDER BY l.created_at DESC LIMIT 200",("account",account)))
  await using(var reader=await query.ExecuteReaderAsync(ct))while(await reader.ReadAsync(ct))lines.Add(new{Id=reader.GetGuid(0),OrderId=reader.GetGuid(1),Kind=reader.GetString(2),Amount=reader.GetInt64(3),At=reader.GetDateTime(4)});
  long pending;await using(var query=Command(db,null,"SELECT COALESCE(sum(l.amount),0)::bigint FROM ledger.lines l WHERE l.account=@account AND NOT EXISTS(SELECT 1 FROM ledger.settlement_lines s WHERE s.line_id=l.id)",("account",account)))pending=Convert.ToInt64(await query.ExecuteScalarAsync(ct));
  return new{Account=account,Currency="PEN",Pending=pending,Movements=lines,Settlements=await SettlementsAsync(account,ct)};
 }
 public async Task<Guid> CloseSettlementAsync(Caller caller,string kind,Guid participant,Guid operation,string reason,CancellationToken ct)
 {
  caller.RequirePermission("finance.manage");await AccessAsync(caller,kind,participant,ct);reason=Guard.Text(reason,"reason",500);Guard.Require(operation!=Guid.Empty,"operation_required","Falta clave de operación.");
  var account=kind+":"+participant;await using var db=await source.OpenConnectionAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);await GateAsync(db,tx,"settlement:"+account,ct);
  await using(var existing=Command(db,tx,"SELECT account FROM ledger.settlements WHERE id=@id",("id",operation)))
  {var prior=await existing.ExecuteScalarAsync(ct) as string;if(prior is not null){Guard.Require(prior==account,"operation_conflict","Clave ya utilizada.",409);return operation;}}
  var lines=new List<(Guid Id,long Amount)>();
  await using(var query=Command(db,tx,"SELECT l.id,l.amount FROM ledger.lines l WHERE l.account=@account AND NOT EXISTS(SELECT 1 FROM ledger.settlement_lines s WHERE s.line_id=l.id) FOR UPDATE",("account",account)))
  await using(var reader=await query.ExecuteReaderAsync(ct))while(await reader.ReadAsync(ct))lines.Add((reader.GetGuid(0),reader.GetInt64(1)));
  var total=lines.Sum(l=>l.Amount);Guard.Require(total>0,"no_payable_balance","No existe saldo positivo para liquidar.",409);
  await ExecuteAsync(db,tx,"INSERT INTO ledger.settlements(id,account,currency,amount) VALUES (@id,@account,'PEN',@amount)",ct,("id",operation),("account",account),("amount",total));
  foreach(var line in lines)await ExecuteAsync(db,tx,"INSERT INTO ledger.settlement_lines(line_id,settlement_id) VALUES (@line,@settlement)",ct,("line",line.Id),("settlement",operation));
  await AuditAsync(db,tx,caller,"ledger",operation,"settlement.closed",reason,ct);await tx.CommitAsync(ct);return operation;
 }
 public async Task MarkPaidAsync(Caller caller,Guid id,string reference,string reason,CancellationToken ct)
 {
  caller.RequirePermission("finance.manage");reference=Guard.Text(reference,"externalReference",150);reason=Guard.Text(reason,"reason",500);
  await using var db=await source.OpenConnectionAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);
  await using var command=Command(db,tx,"UPDATE ledger.settlements SET paid_at=now(),external_reference=@reference WHERE id=@id AND paid_at IS NULL",("id",id),("reference",reference));
  if(await command.ExecuteNonQueryAsync(ct)>0)await AuditAsync(db,tx,caller,"ledger",id,"settlement.paid",reason,ct);await tx.CommitAsync(ct);
 }
 public async Task IncentiveAsync(Caller caller,Guid order,Guid courier,Guid operation,long amount,string reason,CancellationToken ct)
 {
  caller.RequirePermission("finance.manage");reason=Guard.Text(reason,"reason",500);
  Guard.Require(operation!=Guid.Empty&&amount is >0 and <=100000,"invalid_incentive","Incentivo inválido.");
  var id=Derive(operation,"incentive");var account="courier:"+courier;
  await using var db=await source.OpenConnectionAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);await GateAsync(db,tx,"ledger:"+order,ct);
  await using(var eligible=Command(db,tx,"SELECT EXISTS(SELECT 1 FROM ledger.deliveries WHERE order_id=@order AND courier_id=@courier)",("order",order),("courier",courier)))
   Guard.Require(await eligible.ExecuteScalarAsync(ct) is true,"incentive_not_eligible","El incentivo requiere una entrega completada por ese repartidor.",409);
  await using(var previous=Command(db,tx,"SELECT v.order_id,l.account,l.amount FROM ledger.vouchers v JOIN ledger.lines l ON l.voucher_id=v.id WHERE v.id=@id AND l.amount>0",("id",id)))
  {await using var reader=await previous.ExecuteReaderAsync(ct);if(await reader.ReadAsync(ct)){Guard.Require(reader.GetGuid(0)==order&&reader.GetString(1)==account&&reader.GetInt64(2)==amount,"operation_conflict","Clave ya usada con otro incentivo.",409);return;}}
  await PostAsync(db,tx,id,order,"incentive","PEN",[(account,amount),("platform",-amount)],ct);
  await AuditAsync(db,tx,caller,"ledger",order,"courier.incentive",reason,ct);await tx.CommitAsync(ct);
 }
 public async Task CashCollectionAsync(Caller caller,Guid courier,Guid operation,long amount,string reference,string reason,CancellationToken ct)
 {
  caller.RequirePermission("finance.manage");reason=Guard.Text(reason,"reason",500);reference=Guard.Text(reference,"externalReference",150);
  Guard.Require(operation!=Guid.Empty&&amount>0,"invalid_cash_collection","Importe inválido.");
  var account="courier:"+courier;var id=Derive(operation,"cash-collection");
  await using var db=await source.OpenConnectionAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);await GateAsync(db,tx,"settlement:"+account,ct);
  await using(var previous=Command(db,tx,"SELECT account,amount FROM ledger.lines WHERE voucher_id=@id AND amount>0",("id",id)))
  {await using var reader=await previous.ExecuteReaderAsync(ct);if(await reader.ReadAsync(ct)){Guard.Require(reader.GetString(0)==account&&reader.GetInt64(1)==amount,"operation_conflict","Clave ya usada con otro ingreso.",409);return;}}
  long balance;await using(var outstanding=Command(db,tx,"SELECT COALESCE(sum(l.amount),0)::bigint FROM ledger.lines l WHERE account=@account AND NOT EXISTS(SELECT 1 FROM ledger.settlement_lines s WHERE s.line_id=l.id)",("account",account)))balance=Convert.ToInt64(await outstanding.ExecuteScalarAsync(ct));
  Guard.Require(balance<0&&amount<=-balance,"cash_collection_exceeds_debt","El ingreso supera el saldo por rendir del repartidor.",409);
  await PostAsync(db,tx,id,Guid.Empty,"cash-collection","PEN",[(account,amount),("cash-clearing",-amount)],ct);
  await AuditAsync(db,tx,caller,"ledger",courier,"cash.collected",reference+": "+reason,ct);await tx.CommitAsync(ct);
 }
 private async Task<SettlementInfo[]> SettlementsAsync(string account,CancellationToken ct)
 {
  await using var db=await source.OpenConnectionAsync(ct);await using var query=Command(db,null,"SELECT id,account,currency,amount,closed_at,paid_at,external_reference FROM ledger.settlements WHERE account=@account ORDER BY closed_at DESC LIMIT 100",("account",account));
  await using var reader=await query.ExecuteReaderAsync(ct);var list=new List<SettlementInfo>();while(await reader.ReadAsync(ct))list.Add(new(reader.GetGuid(0),reader.GetString(1),reader.GetString(2),reader.GetInt64(3),new(reader.GetDateTime(4)),reader.IsDBNull(5)?null:new DateTimeOffset(reader.GetDateTime(5)),reader.IsDBNull(6)?null:reader.GetString(6)));return list.ToArray();
 }
 private static Guid Derive(Guid id,string action)=>new(SHA256.HashData(Encoding.UTF8.GetBytes(id+":"+action)).AsSpan(0,16));
}
