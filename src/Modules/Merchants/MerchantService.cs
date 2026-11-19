using Delivery.BuildingBlocks;
using Delivery.Contracts;
using Npgsql;
using static Delivery.BuildingBlocks.ModuleSql;
namespace Delivery.Modules.Merchants;
public sealed record MerchantApplication(string Name,string TaxId,string BankAccount,string[] DocumentIds);
public sealed record MerchantDecision(string Status,string Reason);
public sealed record OpeningWindow(int Day,int FromMinute,int ToMinute);
public sealed record SaveBranch(string Name,Coordinate Location,long MinimumOrder,int PreparationMinutes,bool Active,OpeningWindow[] Hours,DateTimeOffset? PausedUntil);
public sealed record SaveProduct(string Name,string Category,string Description,string? ImageUrl,string[] Allergens,long Price,int Stock,bool Available,ModifierSnapshot[] Modifiers);
public sealed record MerchantInfo(Guid Id,Guid OwnerId,string Name,string TaxId,string Status,string BankAccount,string[] DocumentIds);
public sealed record BranchCard(BranchSnapshot Branch,double DistanceKilometers,double Rating,long MinimumProductPrice,int EtaMinutes);
public sealed class MerchantService(NpgsqlDataSource source,TimeProvider clock,IRatingDirectory ratings):IMerchantDirectory,IModuleMigration
{
 public Task ApplyAsync(CancellationToken ct)=>MigrateAsync(source,typeof(MerchantService).Assembly,"merchants-001","Delivery.Modules.Merchants.Infrastructure.001.sql",ct);
 public async Task<Guid> ApplyAsync(Caller caller,MerchantApplication request,CancellationToken ct)
 {
  var name=Guard.Text(request.Name,"name");var tax=Guard.Text(request.TaxId,"taxId",30);var bank=Guard.Text(request.BankAccount,"bankAccount",100);
  Guard.Require(request.DocumentIds is {Length:>0 and <=10}&&request.DocumentIds.All(id=>Guid.TryParse(id,out _)),"invalid_documents","Adjunta documentos válidos.");
  var id=Guid.CreateVersion7();await using var db=await source.OpenConnectionAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);
  await ExecuteAsync(db,tx,"INSERT INTO merchants.businesses(id,owner_id,name,tax_id,bank_account,documents,status) VALUES (@id,@user,@name,@tax,@bank,@docs::jsonb,'pending')",ct,
   ("id",id),("user",caller.Id),("name",name),("tax",tax),("bank",bank),("docs",Encode(request.DocumentIds)));
  await AuditAsync(db,tx,caller,"merchants",id,"merchant.applied","application",ct);await tx.CommitAsync(ct);return id;
 }
 public async Task DecideAsync(Caller caller,Guid id,MerchantDecision request,CancellationToken ct)
 {
  caller.RequirePermission("participants.manage");Guard.Require(request.Status is "approved" or "rejected" or "suspended","invalid_status","Decisión inválida.");var reason=Guard.Text(request.Reason,"reason",500);
  await using var db=await source.OpenConnectionAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);
  await using var command=Command(db,tx,"UPDATE merchants.businesses SET status=@status,decision_reason=@reason WHERE id=@id",("id",id),("status",request.Status),("reason",reason));
  Guard.Require(await command.ExecuteNonQueryAsync(ct)==1,"merchant_not_found","Comercio inexistente.",404);
  await AuditAsync(db,tx,caller,"merchants",id,"merchant.validated",reason,ct);await tx.CommitAsync(ct);
 }
 public async Task<MerchantInfo[]> BusinessesAsync(Caller caller,CancellationToken ct)
 {
  await using var db=await source.OpenConnectionAsync(ct);await using var command=Command(db,null,"SELECT id,owner_id,name,tax_id,status,bank_account,documents FROM merchants.businesses WHERE @admin OR owner_id=@user ORDER BY name LIMIT 200",("admin",caller.Can("participants.manage")),("user",caller.Id));
  await using var reader=await command.ExecuteReaderAsync(ct);var list=new List<MerchantInfo>();while(await reader.ReadAsync(ct))list.Add(new(reader.GetGuid(0),reader.GetGuid(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetString(5),Decode<string[]>(reader.GetString(6))));return list.ToArray();
 }
 private async Task OwnerAsync(NpgsqlConnection db,NpgsqlTransaction? tx,Caller caller,Guid merchant,CancellationToken ct)
 {
  await using var query=Command(db,tx,"SELECT owner_id FROM merchants.businesses WHERE id=@id",("id",merchant));
  var owner=await query.ExecuteScalarAsync(ct);Guard.Require(owner is Guid&&(caller.Administrator||owner.Equals(caller.Id)),"merchant_access_denied","No puedes administrar este comercio.",403);
 }
 public async Task<Guid> SaveBranchAsync(Caller caller,Guid merchant,Guid id,SaveBranch request,CancellationToken ct)
 {
  Guard.Require(request.Location is not null&&request.Location.IsValid&&request.MinimumOrder>=0&&request.PreparationMinutes is >=1 and <=180&&request.Hours is {Length:>0 and <=21},"invalid_branch","Sucursal inválida.");
  Guard.Require(request.Hours!.All(h=>h.Day is >=0 and <=6&&h.FromMinute is >=0 and <1440&&h.ToMinute>h.FromMinute&&h.ToMinute<=1440),"invalid_hours","Horario inválido.");
  Guard.Require(request.Hours.GroupBy(h=>h.Day).All(g=>g.OrderBy(h=>h.FromMinute).Zip(g.OrderBy(h=>h.FromMinute).Skip(1)).All(pair=>pair.First.ToMinute<=pair.Second.FromMinute)),"overlapping_hours","Los horarios se superponen.");
  var name=Guard.Text(request.Name,"name");await using var db=await source.OpenConnectionAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);
  await OwnerAsync(db,tx,caller,merchant,ct);
  await using var command=Command(db,tx,"""
   INSERT INTO merchants.branches(id,merchant_id,name,latitude,longitude,currency,minimum_order,preparation_minutes,active,paused_until,hours)
   VALUES (@id,@merchant,@name,@lat,@lon,'PEN',@minimum,@prep,@active,@paused,@hours::jsonb)
   ON CONFLICT(id) DO UPDATE SET name=excluded.name,latitude=excluded.latitude,longitude=excluded.longitude,minimum_order=excluded.minimum_order,
    preparation_minutes=excluded.preparation_minutes,active=excluded.active,paused_until=excluded.paused_until,hours=excluded.hours WHERE merchants.branches.merchant_id=@merchant
   """,("id",id),("merchant",merchant),("name",name),("lat",request.Location!.Latitude),("lon",request.Location.Longitude),("minimum",request.MinimumOrder),("prep",request.PreparationMinutes),("active",request.Active),("paused",request.PausedUntil?.ToUniversalTime()),("hours",Encode(request.Hours)));
  Guard.Require(await command.ExecuteNonQueryAsync(ct)==1,"branch_access_denied","Sucursal inválida.",403);await AuditAsync(db,tx,caller,"merchants",id,"branch.saved","owner request",ct);await tx.CommitAsync(ct);return id;
 }
 public async Task<bool> CanOperateAsync(Guid actor,Guid branch,CancellationToken ct)
 {
  await using var db=await source.OpenConnectionAsync(ct);await using var command=Command(db,null,"SELECT EXISTS(SELECT 1 FROM merchants.branches b JOIN merchants.businesses m ON m.id=b.merchant_id LEFT JOIN merchants.members s ON s.branch_id=b.id AND s.user_id=@actor WHERE b.id=@branch AND (m.owner_id=@actor OR s.user_id IS NOT NULL))",("actor",actor),("branch",branch));
  return await command.ExecuteScalarAsync(ct) is true;
 }
 public async Task<bool> OwnsBusinessAsync(Guid actor,Guid merchant,CancellationToken ct)
 {
  await using var db=await source.OpenConnectionAsync(ct);await using var command=Command(db,null,"SELECT EXISTS(SELECT 1 FROM merchants.businesses WHERE id=@merchant AND owner_id=@actor)",("merchant",merchant),("actor",actor));
  return await command.ExecuteScalarAsync(ct) is true;
 }
 public async Task<Guid[]> OperatorsAsync(Guid branch,CancellationToken ct)
 {
  await using var db=await source.OpenConnectionAsync(ct);await using var command=Command(db,null,"SELECT m.owner_id FROM merchants.branches b JOIN merchants.businesses m ON m.id=b.merchant_id WHERE b.id=@id UNION SELECT user_id FROM merchants.members WHERE branch_id=@id",("id",branch));
  await using var reader=await command.ExecuteReaderAsync(ct);var ids=new List<Guid>();while(await reader.ReadAsync(ct))ids.Add(reader.GetGuid(0));return ids.ToArray();
 }
 public async Task SetMemberAsync(Caller caller,Guid branch,Guid user,string role,CancellationToken ct)
 {
  Guard.Require(role is "manager" or "operator" or "remove","invalid_member_role","Rol de sucursal inválido.");
  var info=await BranchAsync(branch,ct);await using var db=await source.OpenConnectionAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);await OwnerAsync(db,tx,caller,info.MerchantId,ct);
  if(role=="remove")await ExecuteAsync(db,tx,"DELETE FROM merchants.members WHERE branch_id=@branch AND user_id=@user",ct,("branch",branch),("user",user));
  else await ExecuteAsync(db,tx,"INSERT INTO merchants.members(branch_id,user_id,role) VALUES (@branch,@user,@role) ON CONFLICT(branch_id,user_id) DO UPDATE SET role=excluded.role",ct,("branch",branch),("user",user),("role",role));
  await AuditAsync(db,tx,caller,"merchants",branch,"member.changed","membership",ct);await tx.CommitAsync(ct);
 }
 public async Task<bool> CanManageMembersAsync(Caller caller,Guid branch,CancellationToken ct)
 {
  var item=await BranchAsync(branch,ct);return caller.Administrator||await OwnsBusinessAsync(caller.Id,item.MerchantId,ct);
 }
 public async Task<(Guid User,string Role)[]> MembersAsync(Guid branch,CancellationToken ct)
 {
  await using var db=await source.OpenConnectionAsync(ct);await using var query=Command(db,null,"SELECT user_id,role FROM merchants.members WHERE branch_id=@branch",("branch",branch));
  await using var reader=await query.ExecuteReaderAsync(ct);var members=new List<(Guid,string)>();while(await reader.ReadAsync(ct))members.Add((reader.GetGuid(0),reader.GetString(1)));return members.ToArray();
 }
 public async Task<BranchSnapshot> BranchAsync(Guid id,CancellationToken ct)
 {
  await using var db=await source.OpenConnectionAsync(ct);await using var query=Command(db,null,"SELECT b.id,b.merchant_id,b.name,b.latitude,b.longitude,b.currency,b.minimum_order,b.active,b.paused_until,b.hours,b.preparation_minutes,m.status FROM merchants.branches b JOIN merchants.businesses m ON m.id=b.merchant_id WHERE b.id=@id",("id",id));
  await using var reader=await query.ExecuteReaderAsync(ct);if(!await reader.ReadAsync(ct))throw new ApiException(404,"branch_not_found","Sucursal inexistente.");
  var local=TimeZoneInfo.ConvertTime(clock.GetUtcNow(),TimeZoneInfo.FindSystemTimeZoneById("America/Lima"));var minute=local.Hour*60+local.Minute;
  var windows=Decode<OpeningWindow[]>(reader.GetString(9));var opened=reader.GetBoolean(7)&&reader.GetString(11)=="approved"&&(reader.IsDBNull(8)||new DateTimeOffset(reader.GetDateTime(8))<=clock.GetUtcNow())&&windows.Any(h=>h.Day==(int)local.DayOfWeek&&h.FromMinute<=minute&&h.ToMinute>minute);
  return new(reader.GetGuid(0),reader.GetGuid(1),reader.GetString(2),new(reader.GetDouble(3),reader.GetDouble(4)),reader.GetString(5),reader.GetInt64(6),opened,reader.GetInt32(10));
 }
 public async Task<Guid> SaveProductAsync(Caller caller,Guid branch,Guid id,SaveProduct request,CancellationToken ct)
 {
  await using(var accessDb=await source.OpenConnectionAsync(ct))
  await using(var access=Command(accessDb,null,"SELECT EXISTS(SELECT 1 FROM merchants.branches b JOIN merchants.businesses m ON m.id=b.merchant_id LEFT JOIN merchants.members s ON s.branch_id=b.id AND s.user_id=@user WHERE b.id=@branch AND (m.owner_id=@user OR s.role='manager'))",("user",caller.Id),("branch",branch)))
   Guard.Require(caller.Administrator||await access.ExecuteScalarAsync(ct) is true,"catalog_access_denied","Solo el propietario o encargado puede editar la carta.",403);
  var name=Guard.Text(request.Name,"name");var category=Guard.Text(request.Category,"category",100);
  Guard.Require(request.Price>=0&&request.Stock>=0&&(request.Description?.Length??0)<=2000&&request.Allergens is {Length:<=30}&&request.Modifiers is {Length:<=50},"invalid_product","Producto inválido.");
  Guard.Require(request.Modifiers!.All(m=>m is not null&&m.Id!=Guid.Empty&&m.Price>=0&&!string.IsNullOrWhiteSpace(m.Name)&&m.Name.Length<=200&&!string.IsNullOrWhiteSpace(m.Group)&&m.Group.Length<=100&&m.MinimumSelections>=0&&m.MaximumSelections>=m.MinimumSelections&&m.MaximumSelections<=99)&&request.Modifiers.Select(m=>m.Id).Distinct().Count()==request.Modifiers.Length,"invalid_modifiers","Adicionales inválidos.");
  Guard.Require(request.Modifiers.GroupBy(m=>m.Group).All(group=>group.First().MinimumSelections<=group.Count()&&group.All(m=>m.MinimumSelections==group.First().MinimumSelections&&m.MaximumSelections==group.First().MaximumSelections)),"invalid_modifier_group","Cada grupo debe tener límites consistentes y suficientes opciones para cumplir el mínimo.");
  Guard.Require(request.ImageUrl is null||(Uri.TryCreate(request.ImageUrl,UriKind.Absolute,out var image)&&image.Scheme=="https"),"invalid_image","La imagen debe usar HTTPS.");
  await using var db=await source.OpenConnectionAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);
  await GateAsync(db,tx,"branch:"+branch,ct);
  await using var command=Command(db,tx,"""
   INSERT INTO merchants.products(id,branch_id,name,category,description,image_url,allergens,price,stock,available,modifiers)
   VALUES (@id,@branch,@name,@category,@description,@image,@allergens,@price,@stock,@available,@modifiers::jsonb)
   ON CONFLICT(id) DO UPDATE SET name=excluded.name,category=excluded.category,description=excluded.description,image_url=excluded.image_url,
   allergens=excluded.allergens,price=excluded.price,stock=excluded.stock,available=excluded.available,modifiers=excluded.modifiers WHERE merchants.products.branch_id=@branch
   """,("id",id),("branch",branch),("name",name),("category",category),("description",request.Description??""),("image",request.ImageUrl),("allergens",request.Allergens),("price",request.Price),("stock",request.Stock),("available",request.Available),("modifiers",Encode(request.Modifiers)));
  Guard.Require(await command.ExecuteNonQueryAsync(ct)==1,"product_access_denied","Producto no autorizado.",403);await AuditAsync(db,tx,caller,"merchants",id,"product.saved","catalogue update",ct);await tx.CommitAsync(ct);return id;
 }
 public async Task<ProductSnapshot[]> ProductsAsync(Guid branch,IEnumerable<Guid> ids,CancellationToken ct)
 {
  var selected=ids.ToArray();await using var db=await source.OpenConnectionAsync(ct);await using var command=Command(db,null,"SELECT id,name,category,description,image_url,allergens,price,stock,available,modifiers FROM merchants.products WHERE branch_id=@branch AND (@all OR id=ANY(@ids)) ORDER BY category,name LIMIT 500",("branch",branch),("all",selected.Length==0),("ids",selected));
  await using var reader=await command.ExecuteReaderAsync(ct);var list=new List<ProductSnapshot>();
  while(await reader.ReadAsync(ct))list.Add(new(reader.GetGuid(0),branch,reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.IsDBNull(4)?null:reader.GetString(4),reader.GetFieldValue<string[]>(5),reader.GetInt64(6),reader.GetInt32(7),reader.GetBoolean(8),Decode<ModifierSnapshot[]>(reader.GetString(9))));
  return list.ToArray();
 }
 public async Task<bool> PublishedAsync(Guid branch,CancellationToken ct)
 {
  await using var db=await source.OpenConnectionAsync(ct);await using var query=Command(db,null,"SELECT EXISTS(SELECT 1 FROM merchants.branches b JOIN merchants.businesses m ON m.id=b.merchant_id WHERE b.id=@id AND b.active AND m.status='approved')",("id",branch));return await query.ExecuteScalarAsync(ct) is true;
 }
 public async Task<object[]> OwnBranchesAsync(Caller caller,CancellationToken ct)
 {
  await using var db=await source.OpenConnectionAsync(ct);await using var query=Command(db,null,"SELECT b.id,b.merchant_id,b.hours,b.paused_until,b.active FROM merchants.branches b JOIN merchants.businesses m ON m.id=b.merchant_id WHERE @admin OR m.owner_id=@user OR EXISTS(SELECT 1 FROM merchants.members s WHERE s.branch_id=b.id AND s.user_id=@user)",("admin",caller.Can("participants.manage")),("user",caller.Id));
  var rows=new List<(Guid Id,Guid Merchant,OpeningWindow[] Hours,DateTimeOffset? Pause,bool Active)>();
  await using(var reader=await query.ExecuteReaderAsync(ct))while(await reader.ReadAsync(ct))rows.Add((reader.GetGuid(0),reader.GetGuid(1),Decode<OpeningWindow[]>(reader.GetString(2)),reader.IsDBNull(3)?null:new DateTimeOffset(reader.GetDateTime(3)),reader.GetBoolean(4)));
  var result=new List<object>();foreach(var row in rows)result.Add(new{Branch=await BranchAsync(row.Id,ct),MerchantId=row.Merchant,Hours=row.Hours,PausedUntil=row.Pause,Active=row.Active});return result.ToArray();
 }
 public async Task<BranchCard[]> SearchAsync(Coordinate location,string? text,string? category,double radius,bool onlyOpen,CancellationToken ct,long? maximumPrice=null,double minimumRating=0,int? maximumMinutes=null)
 {
  Guard.Require(location.IsValid&&double.IsFinite(radius)&&radius is >0 and <=50,"invalid_search","Búsqueda inválida.");
  await using var db=await source.OpenConnectionAsync(ct);await using var command=Command(db,null,"SELECT b.id FROM merchants.branches b JOIN merchants.businesses m ON m.id=b.merchant_id WHERE b.active AND m.status='approved' AND (@text='' OR b.name ILIKE '%'||@text||'%' OR EXISTS(SELECT 1 FROM merchants.products p WHERE p.branch_id=b.id AND p.available AND p.name ILIKE '%'||@text||'%')) AND (@category='' OR EXISTS(SELECT 1 FROM merchants.products p WHERE p.branch_id=b.id AND p.category=@category AND p.available)) LIMIT 200",("text",text??""),("category",category??""));
  var ids=new List<Guid>();await using(var reader=await command.ExecuteReaderAsync(ct))while(await reader.ReadAsync(ct))ids.Add(reader.GetGuid(0));
  var branches=new List<BranchCard>();foreach(var id in ids)
  {
   var b=await BranchAsync(id,ct);var distance=Geo.Kilometers(location,b.Location);var rating=await ratings.AverageAsync(id,ct);
   var products=(await ProductsAsync(id,[],ct)).Where(p=>p.Available&&p.Stock>0).ToArray();if(products.Length==0)continue;
   var min=products.Min(p=>p.Price);var eta=b.PreparationMinutes+(int)Math.Ceiling(distance/20*60);
   if(distance<=radius&&(!onlyOpen||b.Open)&&rating>=minimumRating&&(maximumPrice is null||min<=maximumPrice)&&(maximumMinutes is null||eta<=maximumMinutes))branches.Add(new(b,distance,rating,min,eta));
  }
  return branches.OrderBy(b=>b.DistanceKilometers).ToArray();
 }
 public async Task ReserveStockAsync(Guid operation,Guid branch,BasketLine[] lines,CancellationToken ct)
 {
  await using var db=await source.OpenConnectionAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);await GateAsync(db,tx,"branch:"+branch,ct);
  await using(var exists=Command(db,tx,"SELECT EXISTS(SELECT 1 FROM merchants.stock_operations WHERE operation_id=@id)",("id",operation)))if(await exists.ExecuteScalarAsync(ct) is true)return;
  foreach(var line in lines.OrderBy(l=>l.ProductId))
  {
   await using var stock=Command(db,tx,"UPDATE merchants.products SET stock=stock-@qty WHERE id=@id AND branch_id=@branch AND available AND stock>=@qty",("id",line.ProductId),("branch",branch),("qty",line.Quantity));
   Guard.Require(line.Quantity is >0 and <=99&&await stock.ExecuteNonQueryAsync(ct)==1,"insufficient_stock","Un producto no tiene disponibilidad suficiente.",409);
  }
  await ExecuteAsync(db,tx,"INSERT INTO merchants.stock_operations(operation_id,branch_id,lines) VALUES (@id,@branch,@lines::jsonb)",ct,("id",operation),("branch",branch),("lines",Encode(lines)));await tx.CommitAsync(ct);
 }
 public async Task ReleaseStockAsync(Guid operation,CancellationToken ct)
 {
  await using var db=await source.OpenConnectionAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);
  Guid branch;BasketLine[] lines;
  await using(var query=Command(db,tx,"SELECT branch_id,lines FROM merchants.stock_operations WHERE operation_id=@id",("id",operation)))
  {await using var reader=await query.ExecuteReaderAsync(ct);if(!await reader.ReadAsync(ct))return;branch=reader.GetGuid(0);lines=Decode<BasketLine[]>(reader.GetString(1));}
  await GateAsync(db,tx,"branch:"+branch,ct);
  await using(var claim=Command(db,tx,"UPDATE merchants.stock_operations SET released=true WHERE operation_id=@id AND NOT released",("id",operation)))if(await claim.ExecuteNonQueryAsync(ct)==0)return;
  foreach(var line in lines)await ExecuteAsync(db,tx,"UPDATE merchants.products SET stock=stock+@qty WHERE id=@id",ct,("id",line.ProductId),("qty",line.Quantity));
  await tx.CommitAsync(ct);
 }
}

