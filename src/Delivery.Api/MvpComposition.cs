using Delivery.BuildingBlocks;
using Delivery.Contracts;
using Delivery.Modules.Customers;
using Delivery.Modules.Configuration;
using Delivery.Modules.Merchants;
using Delivery.Modules.Pricing;
using Delivery.Modules.Orders;
using Delivery.Modules.Payments;
using Delivery.Modules.Dispatch;
using Delivery.Modules.Deliveries;
using Delivery.Modules.Ledger;
using Delivery.Modules.Support;
using Delivery.Modules.Notifications;
namespace Delivery.Api;
public static class MvpComposition
{
 public static IServiceCollection AddMvp(this IServiceCollection services)
 {
  services.AddSingleton<ConfigurationService>();services.AddSingleton<ICoverageDirectory>(s=>s.GetRequiredService<ConfigurationService>());
  services.AddSingleton<CustomerService>();services.AddSingleton<ICustomerDirectory>(s=>s.GetRequiredService<CustomerService>());
  services.AddSingleton<IRatingDirectory>(s=>s.GetRequiredService<CustomerService>());
  services.AddSingleton<MerchantService>();services.AddSingleton<IMerchantDirectory>(s=>s.GetRequiredService<MerchantService>());
  services.AddSingleton<PricingService>();services.AddSingleton<IPricingDirectory>(s=>s.GetRequiredService<PricingService>());
  services.AddSingleton<OrderService>();services.AddSingleton<IOrderDirectory>(s=>s.GetRequiredService<OrderService>());
  services.AddSingleton<OrderActions>();
  services.AddOptions<PaymentOptions>().BindConfiguration("Payments");services.AddHttpClient("payments",c=>c.Timeout=TimeSpan.FromSeconds(30));
  services.AddSingleton<IPaymentGateway,PaymentGateway>();services.AddSingleton<PaymentService>();services.AddSingleton<IPaymentDirectory>(s=>s.GetRequiredService<PaymentService>());
  services.AddSingleton<DispatchService>();services.AddSingleton<ICourierDirectory>(s=>s.GetRequiredService<DispatchService>());services.AddSingleton<IDispatchDirectory>(s=>s.GetRequiredService<DispatchService>());
  services.AddSingleton<DeliveryService>();services.AddSingleton<IDeliveryDirectory>(s=>s.GetRequiredService<DeliveryService>());
  services.AddSingleton<LedgerService>();services.AddSingleton<ILedgerDirectory>(s=>s.GetRequiredService<LedgerService>());
  services.AddSingleton<SupportService>();services.AddSingleton<NotificationService>();
  services.AddSingleton<IOrderEmailSender,SmtpOrderEmail>();services.AddSingleton<IMessageHandler,OrderEmailHandler>();
  services.AddOptions<DocumentOptions>().BindConfiguration("Documents");
  services.AddSingleton<PrivateDocumentStore>();services.AddSingleton<IDocumentDirectory,DocumentDirectoryAdapter>();
  services.AddSignalR();services.AddSingleton<LiveConnections>();services.AddSingleton<IOrderRealtime,OrderRealtime>();
  foreach(var type in new[]{"orders.checkout-requested.v1","orders.state-changed.v1","dispatch.assigned.v1","deliveries.state-changed.v1","deliveries.location-updated.v1","payments.refunded.v1","payments.state-changed.v1"})
   services.AddSingleton<IMessageHandler>(s=>new MvpEventHandler(type,s.GetRequiredService<IServiceScopeFactory>()));
  services.AddHostedService<MvpMaintenance>();
  return services;
 }
 public static async Task ApplyMigrationsAsync(IServiceProvider services,CancellationToken ct)
 {
  IModuleMigration[] migrations=[services.GetRequiredService<ConfigurationService>(),services.GetRequiredService<CustomerService>(),services.GetRequiredService<MerchantService>(),services.GetRequiredService<PricingService>(),services.GetRequiredService<OrderService>(),services.GetRequiredService<PaymentService>(),services.GetRequiredService<DispatchService>(),services.GetRequiredService<DeliveryService>(),services.GetRequiredService<LedgerService>(),services.GetRequiredService<SupportService>(),services.GetRequiredService<NotificationService>()];
  foreach(var migration in migrations)await migration.ApplyAsync(ct);
 }
}
public sealed class DocumentDirectoryAdapter(PrivateDocumentStore store):IDocumentDirectory
{
 public Task<bool> OwnsAsync(Guid user,Guid id,CancellationToken ct)=>store.OwnsAsync(user,id,ct);
 public Task<bool> OwnsImageAsync(Guid user,Guid id,CancellationToken ct)=>store.OwnsImageAsync(user,id,ct);
 public Task GrantAsync(Guid owner,Guid id,Guid[] users,CancellationToken ct)=>store.GrantAsync(owner,id,users,ct);
}
public sealed class MvpEventHandler(string type,IServiceScopeFactory scopes):IMessageHandler
{
 public string MessageType=>type;
 public string Consumer=>"mvp.orchestrator.v1";
 public async Task HandleAsync(IntegrationMessage message,Npgsql.NpgsqlConnection connection,Npgsql.NpgsqlTransaction transaction,CancellationToken ct)
 {
  using var scope=scopes.CreateScope();var s=scope.ServiceProvider;
  using var json=System.Text.Json.JsonDocument.Parse(message.Payload);var payload=json.RootElement;var order=payload.GetProperty("OrderId").GetGuid();
  var orders=s.GetRequiredService<OrderService>();
  switch(type)
  {
   case "orders.checkout-requested.v1":await orders.ProcessCheckoutAsync(order,ct);break;
   case "payments.state-changed.v1":
    var paymentState=payload.GetProperty("State").GetString();
    var paymentOrder=await orders.GetAsync(order,ct);
    if(paymentOrder.State=="payment_pending")
     await orders.TransitionAsync(order,paymentState is "authorized" or "captured"?"confirmed":"cancelled",Guid.Empty,"provider notification",message.Id,ct);
    break;
   case "orders.state-changed.v1":
    var state=payload.GetProperty("State").GetString()!;
    if(state=="cancelled")
    {await orders.CompensateAsync(order,ct);await s.GetRequiredService<DispatchService>().CancelAsync(order,ct);await s.GetRequiredService<DeliveryService>().CancelAsync(order,ct);}
    if(state is "accepted" or "ready")await s.GetRequiredService<DispatchService>().OfferAsync(order,ct);
    await s.GetRequiredService<NotificationService>().RecordAsync(message.Id,order,state,ct);
    await s.GetRequiredService<IOrderRealtime>().PublishAsync(order,new{Kind="order",State=state},ct);break;
   case "dispatch.assigned.v1":await s.GetRequiredService<DeliveryService>().CreateAsync(order,payload.GetProperty("CourierId").GetGuid(),message.Id,ct);await s.GetRequiredService<IOrderRealtime>().PublishAsync(order,new{Kind="assignment"},ct);break;
   case "deliveries.state-changed.v1":
    var deliveryState=payload.GetProperty("State").GetString()!;var operation=payload.GetProperty("OperationId").GetGuid();
    if(deliveryState!="arrived")
     await orders.TransitionAsync(order,deliveryState,payload.GetProperty("CourierId").GetGuid(),"delivery "+deliveryState,operation,ct);
    if(deliveryState=="delivered")
    {
     await s.GetRequiredService<PaymentService>().CaptureAsync(order,OrderService.Derive(order,"capture"),ct);
     var item=await orders.GetAsync(order,ct);
     var deliveryPayment=await s.GetRequiredService<PaymentService>().InfoAsync(order,ct);
     await s.GetRequiredService<LedgerService>().RecordDeliveryAsync(item,payload.GetProperty("CourierId").GetGuid(),message.Id,ct,deliveryPayment.Method=="cash");
     await orders.TransitionAsync(order,"closed",Guid.Empty,"financial closing",OrderService.Derive(order,"close"),ct);
     await s.GetRequiredService<DispatchService>().CompleteAsync(order,ct);
    }
    if(deliveryState=="returned"){await s.GetRequiredService<PaymentService>().CancelAsync(order,OrderService.Derive(order,"return-refund"),ct);await s.GetRequiredService<DispatchService>().CompleteAsync(order,ct);}
    await s.GetRequiredService<IOrderRealtime>().PublishAsync(order,new{Kind="delivery",State=deliveryState},ct);break;
   case "deliveries.location-updated.v1":
    var current=await orders.GetAsync(order,ct);
    if(current.State is not ("delivered" or "closed" or "cancelled" or "returned"))
    {
     // Legacy queued events lack courier identity: refresh clients without exposing coordinates.
     if(!payload.TryGetProperty("CourierId",out var locatedCourier))
      await s.GetRequiredService<IOrderRealtime>().PublishAsync(order,new{Kind="location"},ct);
     else if(await s.GetRequiredService<DispatchService>().AssignedAsync(order,ct)==locatedCourier.GetGuid())
      await s.GetRequiredService<IOrderRealtime>().PublishAsync(order,new{Kind="location",Latitude=payload.GetProperty("Latitude").GetDouble(),Longitude=payload.GetProperty("Longitude").GetDouble()},ct);
    }
    break;
   case "payments.refunded.v1":
    await s.GetRequiredService<LedgerService>().RefundAsync(await orders.GetAsync(order,ct),payload.GetProperty("Amount").GetInt64(),payload.GetProperty("OperationId").GetGuid(),ct);break;
  }
 }
}
public sealed class MvpMaintenance(IServiceScopeFactory scopes,IConfiguration configuration,ILogger<MvpMaintenance> logger):BackgroundService
{
 protected override async Task ExecuteAsync(CancellationToken stoppingToken)
 {
  if(!configuration.GetValue("Messaging:RunWorker",true))return;
  while(!stoppingToken.IsCancellationRequested)
  {
   try
   {
    using var scope=scopes.CreateScope();var s=scope.ServiceProvider;
    await s.GetRequiredService<OrderService>().ExpireAcceptanceAsync(stoppingToken);
    foreach(var item in await s.GetRequiredService<OrderService>().ListAsync(new(Guid.Empty,true,"maintenance"),null,stoppingToken))
     if(item.State is "accepted" or "preparing" or "ready")await s.GetRequiredService<DispatchService>().OfferAsync(item.Id,stoppingToken);
    await s.GetRequiredService<DeliveryService>().PurgeLocationsAsync(stoppingToken);
   }
   catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){break;}
   catch(Exception ex){logger.LogWarning("MVP maintenance failed ({Type})",ex.GetType().Name);}
   await Task.Delay(TimeSpan.FromSeconds(15),stoppingToken);
  }
 }
}
