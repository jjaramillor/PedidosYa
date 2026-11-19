using Delivery.BuildingBlocks;
using Delivery.Contracts;
using Npgsql;
using static Delivery.BuildingBlocks.ModuleSql;
namespace Delivery.Modules.Pricing;
public sealed record CartInput(Guid BranchId,BasketLine[] Lines);
public sealed record CouponInput(string Code,Guid? BranchId,DateTimeOffset StartsAt,DateTimeOffset EndsAt,long MinimumOrder,int DiscountBasisPoints,long MaximumDiscount,int TotalLimit,int PerCustomerLimit,bool MerchantFunded,bool Active,string Reason);
public sealed class PricingService(NpgsqlDataSource source,ICustomerDirectory customers,IMerchantDirectory merchants,ICoverageDirectory coverage,TimeProvider clock):IPricingDirectory,IModuleMigration
{
 public Task ApplyAsync(CancellationToken ct)=>MigrateAsync(source,typeof(PricingService).Assembly,"pricing-001","Delivery.Modules.Pricing.Infrastructure.001.sql",ct);
 public static PricedLine[] Price(BasketLine[] lines,ProductSnapshot[] products)
 {
  Guard.Require(lines is {Length:>0 and <=100},"invalid_cart","Carrito inválido.");
  foreach(var group in lines.GroupBy(line=>line.ProductId))
  {
   var product=products.SingleOrDefault(product=>product.Id==group.Key);
   Guard.Require(product is not null&&group.All(line=>line.Quantity is >0 and <=99)&&group.Sum(line=>line.Quantity)<=product.Stock,"product_unavailable","El stock no alcanza para todas las variantes seleccionadas.",409);
  }
  var result=new List<PricedLine>();
  foreach(var line in lines)
  {
   var p=products.SingleOrDefault(p=>p.Id==line.ProductId);
   Guard.Require(p is not null&&p.Available&&line.Quantity is >0 and <=99&&p.Stock>=line.Quantity,"product_unavailable","Un producto no está disponible.",409);
   Guard.Require(line.ModifierIds is {Length:<=50}&&line.ModifierIds.Distinct().Count()==line.ModifierIds.Length,"invalid_modifiers","Opciones inválidas.");
   var selected=p!.Modifiers.Where(m=>line.ModifierIds!.Contains(m.Id)).ToArray();
   Guard.Require(selected.Length==line.ModifierIds!.Length,"unknown_modifier","Hay opciones desconocidas.");
   foreach(var group in p.Modifiers.GroupBy(m=>m.Group))
   {
    var count=selected.Count(m=>m.Group==group.Key);var first=group.First();
    Guard.Require(group.All(m=>m.MinimumSelections==first.MinimumSelections&&m.MaximumSelections==first.MaximumSelections)
     &&count>=first.MinimumSelections&&count<=first.MaximumSelections,"invalid_selection","Faltan opciones obligatorias o hay demasiadas.");
   }
   result.Add(new(p.Id,p.Name,line.Quantity,checked(p.Price+selected.Sum(m=>m.Price)),selected));
  }
  return result.ToArray();
 }
 public async Task SaveCartAsync(Guid user,CartInput request,CancellationToken ct)
 {
  var branch=await merchants.BranchAsync(request.BranchId,ct);
  Guard.Require(request.Lines is {Length:<=100},"invalid_cart","Carrito inválido.");
  if(request.Lines.Length>0)Price(request.Lines,await merchants.ProductsAsync(branch.Id,request.Lines.Select(l=>l.ProductId),ct));
  await using var db=await source.OpenConnectionAsync(ct);
  await ExecuteAsync(db,null,"INSERT INTO pricing.carts(user_id,branch_id,lines) VALUES (@user,@branch,@lines::jsonb) ON CONFLICT(user_id) DO UPDATE SET branch_id=excluded.branch_id,lines=excluded.lines,updated_at=now()",ct,("user",user),("branch",branch.Id),("lines",Encode(request.Lines)));
 }
 public async Task<CartInput?> CartAsync(Guid user,CancellationToken ct)
 {
  await using var db=await source.OpenConnectionAsync(ct);await using var command=Command(db,null,"SELECT branch_id,lines FROM pricing.carts WHERE user_id=@user",("user",user));await using var reader=await command.ExecuteReaderAsync(ct);return await reader.ReadAsync(ct)?new(reader.GetGuid(0),Decode<BasketLine[]>(reader.GetString(1))):null;
 }
 public async Task<Guid> SaveCouponAsync(Caller caller,Guid id,CouponInput request,CancellationToken ct)
 {
  caller.RequirePermission("promotions.manage");var code=Guard.Text(request.Code,"code",30).ToUpperInvariant();var reason=Guard.Text(request.Reason,"reason",500);
  Guard.Require(request.EndsAt>request.StartsAt&&request.MinimumOrder>=0&&request.DiscountBasisPoints is >=1 and <=10000&&request.MaximumDiscount>0&&request.TotalLimit>0&&request.PerCustomerLimit>0,"invalid_coupon","Cupón inválido.");
  if(request.BranchId is Guid branch)await merchants.BranchAsync(branch,ct);
  await using var db=await source.OpenConnectionAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);
  await GateAsync(db,tx,"coupon:"+id,ct);
  await ExecuteAsync(db,tx,"""
   INSERT INTO pricing.coupons(id,code,branch_id,starts_at,ends_at,minimum_order,discount_bps,maximum_discount,total_limit,per_customer_limit,merchant_funded,active)
   VALUES (@id,@code,@branch,@starts,@ends,@minimum,@bps,@maximum,@total,@per,@funded,@active)
   ON CONFLICT(id) DO UPDATE SET starts_at=excluded.starts_at,ends_at=excluded.ends_at,minimum_order=excluded.minimum_order,discount_bps=excluded.discount_bps,
    maximum_discount=excluded.maximum_discount,total_limit=excluded.total_limit,per_customer_limit=excluded.per_customer_limit,merchant_funded=excluded.merchant_funded,active=excluded.active
   """,ct,("id",id),("code",code),("branch",request.BranchId),("starts",request.StartsAt.ToUniversalTime()),("ends",request.EndsAt.ToUniversalTime()),("minimum",request.MinimumOrder),("bps",request.DiscountBasisPoints),("maximum",request.MaximumDiscount),("total",request.TotalLimit),("per",request.PerCustomerLimit),("funded",request.MerchantFunded),("active",request.Active));
  await AuditAsync(db,tx,caller,"pricing",id,"coupon.saved",reason,ct);await tx.CommitAsync(ct);return id;
 }
 public async Task<QuoteSnapshot> QuoteAsync(Guid customer,Guid address,Guid branchId,BasketLine[] lines,string? coupon,long tip,CancellationToken ct)
 {
  Guard.Require(tip is >=0 and <=100000,"invalid_tip","Propina inválida.");
  var addressSnapshot=await customers.AddressAsync(customer,address,ct);var zone=await coverage.LocateAsync(addressSnapshot.Location,ct);
  var branch=await merchants.BranchAsync(branchId,ct);var merchantZone=await coverage.LocateAsync(branch.Location,ct);
  Guard.Require(branch.Open&&zone.ZoneId==merchantZone.ZoneId,"branch_closed","Sucursal cerrada o fuera de la zona de entrega.",422);
  var priced=Price(lines,await merchants.ProductsAsync(branchId,lines.Select(l=>l.ProductId),ct));var subtotal=checked(priced.Sum(l=>checked(l.UnitPrice*l.Quantity)));
  Guard.Require(subtotal>=branch.MinimumOrder,"minimum_order","El carrito no alcanza el pedido mínimo.",422);
  var distance=Geo.Kilometers(branch.Location,addressSnapshot.Location);var delivery=checked(zone.DeliveryBase+(long)Math.Ceiling((decimal)distance*zone.PerKilometer));
  Guid? couponId=null;long discount=0;bool funded=false;
  await using var db=await source.OpenConnectionAsync(ct);
  if(!string.IsNullOrWhiteSpace(coupon))
  {
   await using var command=Command(db,null,"SELECT id,minimum_order,discount_bps,maximum_discount,merchant_funded FROM pricing.coupons WHERE code=@code AND active AND starts_at<=@now AND ends_at>@now AND (branch_id IS NULL OR branch_id=@branch)",("code",coupon.Trim().ToUpperInvariant()),("now",clock.GetUtcNow()),("branch",branchId));
   await using var reader=await command.ExecuteReaderAsync(ct);Guard.Require(await reader.ReadAsync(ct),"coupon_invalid","El cupón no es válido.",422);
   couponId=reader.GetGuid(0);Guard.Require(subtotal>=reader.GetInt64(1),"coupon_minimum","No se alcanza el mínimo del cupón.",422);
   discount=Math.Min(MoneyMath.BasisPoints(subtotal,reader.GetInt32(2)),reader.GetInt64(3));funded=reader.GetBoolean(4);
  }
  var quote=new QuoteSnapshot(Guid.CreateVersion7(),customer,branch,addressSnapshot,priced,branch.Currency,subtotal,discount,delivery,zone.ServiceFee,0,tip,
   MoneyMath.Total(subtotal,discount,delivery,zone.ServiceFee,0,tip),zone.CommissionBasisPoints,checked(zone.CourierBase+(long)Math.Ceiling((decimal)distance*zone.PerKilometer)),couponId,clock.GetUtcNow().AddMinutes(5),funded);
  await ExecuteAsync(db,null,"INSERT INTO pricing.quotes(id,user_id,snapshot,expires_at) VALUES (@id,@user,@snapshot::jsonb,@expires)",ct,("id",quote.Id),("user",customer),("snapshot",Encode(quote)),("expires",quote.ExpiresAt));
  return quote;
 }
 public async Task<QuoteSnapshot> ReadQuoteAsync(Guid customer,Guid quote,CancellationToken ct)
 {
  await using var db=await source.OpenConnectionAsync(ct);await using var command=Command(db,null,"SELECT snapshot FROM pricing.quotes WHERE id=@id AND user_id=@user",("id",quote),("user",customer));
  var value=await command.ExecuteScalarAsync(ct) as string??throw new ApiException(404,"quote_not_found","Cotización inexistente.");return Decode<QuoteSnapshot>(value);
 }
 public async Task<QuoteSnapshot> ConfirmAsync(Guid customer,Guid quoteId,Guid operation,CancellationToken ct)
 {
  var quote=await ReadQuoteAsync(customer,quoteId,ct);
  await using var db=await source.OpenConnectionAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);
  await GateAsync(db,tx,"quote:"+quoteId,ct);
  await using(var existing=Command(db,tx,"SELECT consumed_by FROM pricing.quotes WHERE id=@id",("id",quoteId)))
  {
   var used=await existing.ExecuteScalarAsync(ct);
   if(used is Guid consumed){Guard.Require(consumed==operation,"quote_used","Cotización ya utilizada.",409);return quote;}
  }
  Guard.Require(quote.ExpiresAt>clock.GetUtcNow(),"quote_expired","La cotización venció.",409);
  var currentBranch=await merchants.BranchAsync(quote.Branch.Id,ct);Guard.Require(currentBranch.Open,"branch_closed","El comercio cerró.",409);
  await coverage.LocateAsync(quote.Address.Location,ct);
  var inputs=quote.Lines.Select(l=>new BasketLine(l.ProductId,l.Quantity,l.Modifiers.Select(m=>m.Id).ToArray())).ToArray();
  var current=Price(inputs,await merchants.ProductsAsync(quote.Branch.Id,inputs.Select(l=>l.ProductId),ct));
  Guard.Require(current.Zip(quote.Lines).All(pair=>pair.First.UnitPrice==pair.Second.UnitPrice),"price_changed","Los precios cambiaron. Solicita una nueva cotización.",409);
  if(quote.CouponId is Guid coupon)
  {
   await GateAsync(db,tx,"coupon:"+coupon,ct);
   await using var command=Command(db,tx,"""
    SELECT c.active AND c.starts_at<=@now AND c.ends_at>@now
      AND (c.branch_id IS NULL OR c.branch_id=@branch) AND c.minimum_order<=@subtotal
      AND c.merchant_funded=@funded AND LEAST(round(@subtotal::numeric*c.discount_bps/10000)::bigint,c.maximum_discount)=@discount
      AND (SELECT count(*) FROM pricing.coupon_uses u WHERE u.coupon_id=c.id AND NOT u.released)<c.total_limit
      AND (SELECT count(*) FROM pricing.coupon_uses u WHERE u.coupon_id=c.id AND u.customer_id=@user AND NOT u.released)<c.per_customer_limit
    FROM pricing.coupons c WHERE c.id=@id
    """,("id",coupon),("user",customer),("now",clock.GetUtcNow()),("branch",quote.Branch.Id),("subtotal",quote.Subtotal),("funded",quote.MerchantFundsDiscount),("discount",quote.Discount));
   Guard.Require(await command.ExecuteScalarAsync(ct) is true,"coupon_exhausted","Cupón vencido o agotado.",409);
   await ExecuteAsync(db,tx,"INSERT INTO pricing.coupon_uses(operation_id,coupon_id,customer_id) VALUES (@operation,@coupon,@user)",ct,("operation",operation),("coupon",coupon),("user",customer));
  }
  await ExecuteAsync(db,tx,"UPDATE pricing.quotes SET consumed_by=@operation WHERE id=@id",ct,("operation",operation),("id",quoteId));await tx.CommitAsync(ct);return quote;
 }
 public async Task ReleaseCouponAsync(Guid operation,CancellationToken ct)
 {
  await using var db=await source.OpenConnectionAsync(ct);await ExecuteAsync(db,null,"UPDATE pricing.coupon_uses SET released=true WHERE operation_id=@operation",ct,("operation",operation));
 }
}

