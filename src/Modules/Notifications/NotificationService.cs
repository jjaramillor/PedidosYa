using Delivery.BuildingBlocks;
using Delivery.Contracts;
using Npgsql;
using static Delivery.BuildingBlocks.ModuleSql;
namespace Delivery.Modules.Notifications;
public interface IOrderEmailSender {Task SendAsync(Guid user,string title,string body,CancellationToken ct);}
public sealed class NotificationService(NpgsqlDataSource source,IOrderDirectory orders,IMerchantDirectory merchants,IDispatchDirectory dispatch):IModuleMigration
{
 public Task ApplyAsync(CancellationToken ct)=>MigrateAsync(source,typeof(NotificationService).Assembly,"notifications-001","Delivery.Modules.Notifications.Infrastructure.001.sql",ct);
 public async Task RecordAsync(Guid eventId,Guid order,string state,CancellationToken ct)
 {
  var item=await orders.GetAsync(order,ct);var recipients=new HashSet<Guid>(await merchants.OperatorsAsync(item.BranchId,ct)){item.CustomerId};
  if(await dispatch.AssignedAsync(order,ct) is Guid courier)recipients.Add(courier);
  var title=state switch{"confirmed"=>"Pedido confirmado","accepted"=>"El comercio aceptó tu pedido","preparing"=>"Pedido en preparación","ready"=>"Pedido listo","picked_up"=>"Pedido recogido","in_transit"=>"Pedido en camino","delivered"=>"Pedido entregado","closed"=>"Pedido completado","cancelled"=>"Pedido cancelado","incident"=>"Incidencia de entrega",_=>"Actualización del pedido"};
  await using var db=await source.OpenConnectionAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);
  foreach(var user in recipients)
  {
   var body="Tu pedido en "+item.Quote.Branch.Name+": "+title+".";
   await using var insert=Command(db,tx,"INSERT INTO notifications.messages(id,event_id,user_id,order_id,title,body) VALUES (@id,@event,@user,@order,@title,@body) ON CONFLICT(event_id,user_id) DO NOTHING",("id",Guid.CreateVersion7()),("event",eventId),("user",user),("order",order),("title",title),("body",body));
   var inserted=await insert.ExecuteNonQueryAsync(ct);
   if(inserted>0)await Outbox.EnqueueAsync(db,tx,"notifications.email-requested.v1",new{UserId=user,Title=title,Body=body},eventId.ToString(),ct);
  }
  await tx.CommitAsync(ct);
 }
 public async Task<object[]> ListAsync(Guid user,CancellationToken ct)
 {
  await using var db=await source.OpenConnectionAsync(ct);await using var query=Command(db,null,"SELECT id,order_id,title,body,created_at,read_at FROM notifications.messages WHERE user_id=@user ORDER BY created_at DESC LIMIT 100",("user",user));
  await using var reader=await query.ExecuteReaderAsync(ct);var list=new List<object>();while(await reader.ReadAsync(ct))list.Add(new{Id=reader.GetGuid(0),OrderId=reader.GetGuid(1),Title=reader.GetString(2),Body=reader.GetString(3),CreatedAt=reader.GetDateTime(4),Read=!reader.IsDBNull(5)});return list.ToArray();
 }
 public async Task ReadAsync(Guid user,Guid id,CancellationToken ct)
 {await using var db=await source.OpenConnectionAsync(ct);await ExecuteAsync(db,null,"UPDATE notifications.messages SET read_at=now() WHERE id=@id AND user_id=@user",ct,("id",id),("user",user));}
}

