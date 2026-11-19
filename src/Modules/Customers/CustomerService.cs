using Delivery.BuildingBlocks;
using Delivery.Contracts;
using Npgsql;
using static Delivery.BuildingBlocks.ModuleSql;
namespace Delivery.Modules.Customers;
public sealed record SaveAddress(string Label,string Street,string Reference,Coordinate Location);
public sealed record SaveRating(int MerchantStars,int CourierStars,string Comment);
public sealed class CustomerService(NpgsqlDataSource source):ICustomerDirectory,IRatingDirectory,IModuleMigration
{
 public Task ApplyAsync(CancellationToken ct)=>MigrateAsync(source,typeof(CustomerService).Assembly,"customers-001","Delivery.Modules.Customers.Infrastructure.001.sql",ct);
 public async Task<double> AverageAsync(Guid branch,CancellationToken ct)
 {
  await using var db=await source.OpenConnectionAsync(ct);await using var command=Command(db,null,"SELECT COALESCE(avg(merchant_stars),0) FROM customers.ratings WHERE branch_id=@branch",("branch",branch));return Convert.ToDouble(await command.ExecuteScalarAsync(ct));
 }
 public async Task<Guid> SaveAsync(Caller caller,Guid id,SaveAddress request,CancellationToken ct)
 {
  var label=Guard.Text(request.Label,"label",50);var street=Guard.Text(request.Street,"street");var reference=request.Reference?.Trim()??"";
  Guard.Require(reference.Length<=500&&request.Location is not null&&request.Location.IsValid,"invalid_address","Dirección inválida.");
  await using var db=await source.OpenConnectionAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);
  await using var command=Command(db,tx,"""
   INSERT INTO customers.addresses(id,user_id,label,street,reference,latitude,longitude) VALUES (@id,@user,@label,@street,@reference,@lat,@lon)
   ON CONFLICT(id) DO UPDATE SET label=excluded.label,street=excluded.street,reference=excluded.reference,latitude=excluded.latitude,longitude=excluded.longitude,updated_at=now()
   WHERE customers.addresses.user_id=@user
   """,("id",id),("user",caller.Id),("label",label),("street",street),("reference",reference),("lat",request.Location!.Latitude),("lon",request.Location.Longitude));
  Guard.Require(await command.ExecuteNonQueryAsync(ct)==1,"address_not_found","La dirección no existe.",404);
  await AuditAsync(db,tx,caller,"customers",id,"address.saved","user request",ct);await tx.CommitAsync(ct);return id;
 }
 public async Task<AddressSnapshot> AddressAsync(Guid customer,Guid address,CancellationToken ct)
 {
  await using var db=await source.OpenConnectionAsync(ct);await using var command=Command(db,null,"SELECT id,label,street,reference,latitude,longitude FROM customers.addresses WHERE user_id=@user AND id=@id",("user",customer),("id",address));
  await using var reader=await command.ExecuteReaderAsync(ct);
  if(!await reader.ReadAsync(ct))throw new ApiException(404,"address_not_found","La dirección no existe.");
  return new(reader.GetGuid(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),new(reader.GetDouble(4),reader.GetDouble(5)));
 }
 public async Task<AddressSnapshot[]> ListAsync(Guid user,CancellationToken ct)
 {
  await using var db=await source.OpenConnectionAsync(ct);await using var command=Command(db,null,"SELECT id,label,street,reference,latitude,longitude FROM customers.addresses WHERE user_id=@user ORDER BY label LIMIT 100",("user",user));
  await using var reader=await command.ExecuteReaderAsync(ct);var result=new List<AddressSnapshot>();
  while(await reader.ReadAsync(ct))result.Add(new(reader.GetGuid(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),new(reader.GetDouble(4),reader.GetDouble(5))));
  return result.ToArray();
 }
 public async Task DeleteAsync(Caller caller,Guid id,CancellationToken ct)
 {
  await using var db=await source.OpenConnectionAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);
  await ExecuteAsync(db,tx,"DELETE FROM customers.addresses WHERE id=@id AND user_id=@user",ct,("id",id),("user",caller.Id));
  await AuditAsync(db,tx,caller,"customers",id,"address.deleted","user request",ct);await tx.CommitAsync(ct);
 }
 public async Task RateAsync(Caller caller,Guid orderId,SaveRating request,IOrderDirectory orders,CancellationToken ct)
 {
  Guard.Require(request.MerchantStars is >=1 and <=5&&request.CourierStars is >=1 and <=5&&(request.Comment?.Length??0)<=1000,"invalid_rating","Calificación inválida.");
  var order=await orders.GetAsync(orderId,ct);
  Guard.Require(order.CustomerId==caller.Id&&order.State is "delivered" or "closed","rating_not_allowed","Solo puedes calificar tus pedidos entregados.",403);
  await using var db=await source.OpenConnectionAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);
  await ExecuteAsync(db,tx,"INSERT INTO customers.ratings(order_id,user_id,branch_id,merchant_stars,courier_stars,comment) VALUES (@id,@user,@branch,@merchant,@courier,@comment) ON CONFLICT(order_id) DO NOTHING",ct,("id",orderId),("user",caller.Id),("branch",order.BranchId),("merchant",request.MerchantStars),("courier",request.CourierStars),("comment",request.Comment??""));
  await AuditAsync(db,tx,caller,"customers",orderId,"order.rated","customer rating",ct);await tx.CommitAsync(ct);
 }
}
