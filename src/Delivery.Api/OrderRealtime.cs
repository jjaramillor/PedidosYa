using System.Collections.Concurrent;
using System.Security.Claims;
using Delivery.BuildingBlocks;
using Delivery.Contracts;
using Delivery.Modules.Identity;
using Delivery.Modules.Orders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
namespace Delivery.Api;
public sealed record LiveLease(Guid User,Guid Session);
public sealed class LiveConnections {public ConcurrentDictionary<(Guid Order,string Connection),LiveLease> Subscriptions{get;}=new();}
[Authorize]
public sealed class OrderHub(LiveConnections live,OrderService orders,IDeliveryDirectory deliveries):Hub
{
 public async Task Subscribe(Guid order)
 {
  var context=Context.GetHttpContext()!;var caller=Caller.From(context);
  Guard.Require(await orders.CanReadAsync(caller,order,Context.ConnectionAborted)||await deliveries.CanReadAsync(caller.Id,order,Context.ConnectionAborted),"order_access_denied","Pedido no autorizado.",403);
  Guard.Require(live.Subscriptions.Keys.Count(k=>k.Connection==Context.ConnectionId)<50,"subscription_limit","Demasiadas suscripciones.");
  live.Subscriptions[(order,Context.ConnectionId)]=new(caller.Id,Guid.Parse(Context.User!.FindFirstValue("sid")!));
 }
 public Task Unsubscribe(Guid order){live.Subscriptions.TryRemove((order,Context.ConnectionId),out _);return Task.CompletedTask;}
 public override Task OnDisconnectedAsync(Exception? exception)
 {foreach(var key in live.Subscriptions.Keys.Where(k=>k.Connection==Context.ConnectionId))live.Subscriptions.TryRemove(key,out _);return base.OnDisconnectedAsync(exception);}
}
public sealed class OrderRealtime(IHubContext<OrderHub> hub,LiveConnections live,IServiceScopeFactory scopes):IOrderRealtime
{
 public async Task PublishAsync(Guid order,object update,CancellationToken ct)
 {
  using var scope=scopes.CreateScope();var s=scope.ServiceProvider;var sessions=s.GetRequiredService<SessionService>();
  foreach(var entry in live.Subscriptions.Where(pair=>pair.Key.Order==order))
  {
   var access=await sessions.ValidateAsync(entry.Value.User,entry.Value.Session,ct);
   if(access is null){live.Subscriptions.TryRemove(entry.Key,out _);continue;}
   var caller=new Caller(entry.Value.User,access.Roles.Contains("admin"),"live",access.Permissions);
   if(await s.GetRequiredService<OrderService>().CanReadAsync(caller,order,ct)||await s.GetRequiredService<IDeliveryDirectory>().CanReadAsync(caller.Id,order,ct))
    await hub.Clients.Client(entry.Key.Connection).SendAsync("orderChanged",new{OrderId=order,Update=update},ct);
   else live.Subscriptions.TryRemove(entry.Key,out _);
  }
 }
}

