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
public sealed record QuoteInput(Guid AddressId,Guid BranchId,BasketLine[] Lines,string? Coupon,long Tip);
public sealed record DecisionInput(bool Accept);
public sealed record MemberInput(Guid UserId,string Role,string? Email=null);
public sealed record ReassignmentInput(Guid CourierId,string Reason);
public sealed record MessageInput(string Message);
public sealed record SettlementInput(Guid OperationId,string Reason);
public sealed record PaidInput(string ExternalReference,string Reason);
public sealed record IncentiveInput(Guid OperationId,Guid CourierId,long Amount,string Reason);
public sealed record CashCollectionInput(Guid OperationId,long Amount,string ExternalReference,string Reason);
public static class MvpEndpoints
{
 public static void MapMvpEndpoints(this WebApplication app)
 {
  var api=app.MapGroup("/api/v1").RequireAuthorization();
  api.AddEndpointFilter(async(context,next)=>{context.HttpContext.Response.Headers.CacheControl="no-store";return await next(context);});
  app.MapGet("/api/v1/maps/settings",(IConfiguration configuration)=>Results.Ok(new{
   TilesUrl=configuration["Maps:TilesUrl"]??"https://tile.openstreetmap.org/{z}/{x}/{y}.png",
   Attribution=configuration["Maps:Attribution"]??"© <a href=\"https://www.openstreetmap.org/copyright\">OpenStreetMap</a> contributors"
  }));
  app.MapPost("/api/v1/payments/webhooks/mercadopago",async(HttpContext c,PaymentService s,CancellationToken ct)=>
  {await s.WebhookAsync(c.Request.Query["data.id"].ToString(),c.Request.Headers["x-request-id"].ToString(),c.Request.Headers["x-signature"].ToString(),ct);return Results.NoContent();});
  api.MapGet("/addresses",async(HttpContext c,CustomerService s,CancellationToken ct)=>Results.Ok(await s.ListAsync(Caller.From(c).Id,ct)));
  api.MapPost("/addresses",async(SaveAddress r,HttpContext c,CustomerService s,CancellationToken ct)=>Results.Ok(new{Id=await s.SaveAsync(Caller.From(c),Guid.CreateVersion7(),r,ct)}));
  api.MapPut("/addresses/{id:guid}",async(Guid id,SaveAddress r,HttpContext c,CustomerService s,CancellationToken ct)=>Results.Ok(new{Id=await s.SaveAsync(Caller.From(c),id,r,ct)}));
  api.MapDelete("/addresses/{id:guid}",async(Guid id,HttpContext c,CustomerService s,CancellationToken ct)=>{await s.DeleteAsync(Caller.From(c),id,ct);return Results.NoContent();});
  api.MapPost("/coverage",async(Coordinate r,ConfigurationService s,CancellationToken ct)=>Results.Ok(await s.LocateAsync(r,ct)));
  app.MapGet("/api/v1/catalog/search",async(double latitude,double longitude,string? text,string? category,double? radius,bool? onlyOpen,long? maximumPrice,double? minimumRating,int? maximumMinutes,MerchantService s,CancellationToken ct)=>Results.Ok(await s.SearchAsync(new(latitude,longitude),text,category,radius??10,onlyOpen??true,ct,maximumPrice,minimumRating??0,maximumMinutes)));
  app.MapGet("/api/v1/catalog/branches/{id:guid}",async(Guid id,MerchantService s,CancellationToken ct)=>{Guard.Require(await s.PublishedAsync(id,ct),"branch_not_found","Sucursal inexistente.",404);return Results.Ok(new{Branch=await s.BranchAsync(id,ct),Products=await s.ProductsAsync(id,[],ct)});});
  api.MapGet("/cart",async(HttpContext c,PricingService s,CancellationToken ct)=>Results.Ok(await s.CartAsync(Caller.From(c).Id,ct)));
  api.MapPut("/cart",async(CartInput r,HttpContext c,PricingService s,CancellationToken ct)=>{await s.SaveCartAsync(Caller.From(c).Id,r,ct);return Results.NoContent();});
  api.MapPost("/quotes",async(QuoteInput r,HttpContext c,PricingService s,CancellationToken ct)=>Results.Ok(await s.QuoteAsync(Caller.From(c).Id,r.AddressId,r.BranchId,r.Lines,r.Coupon,r.Tip,ct)));
  api.MapPost("/orders",async(CheckoutInput r,HttpContext c,OrderService s,CancellationToken ct)=>Results.Accepted(value:await s.PlaceAsync(Caller.From(c),r,ct)));
  api.MapGet("/orders",async(Guid? branchId,HttpContext c,OrderService s,CancellationToken ct)=>Results.Ok(await s.ListAsync(Caller.From(c),branchId,ct)));
  api.MapGet("/orders/{id:guid}",async(Guid id,HttpContext c,OrderService s,IDeliveryDirectory d,CancellationToken ct)=>{Guard.Require(await s.CanReadAsync(Caller.From(c),id,ct)||await d.CanReadAsync(Caller.From(c).Id,id,ct),"order_access_denied","Pedido no autorizado.",403);return Results.Ok(await s.GetAsync(id,ct));});
  api.MapGet("/orders/{id:guid}/timeline",async(Guid id,HttpContext c,OrderService s,IDeliveryDirectory d,CancellationToken ct)=>{Guard.Require(await s.CanReadAsync(Caller.From(c),id,ct)||await d.CanReadAsync(Caller.From(c).Id,id,ct),"order_access_denied","Pedido no autorizado.",403);return Results.Ok(await s.TimelineAsync(id,ct));});
  api.MapPost("/orders/{id:guid}/actions",async(Guid id,OrderAction r,HttpContext c,OrderActions s,CancellationToken ct)=>{await s.ExecuteAsync(Caller.From(c),id,r,ct);return Results.NoContent();});
  api.MapPost("/orders/{id:guid}/rating",async(Guid id,SaveRating r,HttpContext c,CustomerService s,IOrderDirectory orders,CancellationToken ct)=>{await s.RateAsync(Caller.From(c),id,r,orders,ct);return Results.NoContent();});
  api.MapGet("/merchants",async(HttpContext c,MerchantService s,CancellationToken ct)=>Results.Ok(await s.BusinessesAsync(Caller.From(c),ct)));
  api.MapPost("/merchants",async(MerchantApplication r,HttpContext c,MerchantService s,IDocumentDirectory files,CancellationToken ct)=>{foreach(var id in r.DocumentIds??[])Guard.Require(Guid.TryParse(id,out var parsed)&&await files.OwnsAsync(Caller.From(c).Id,parsed,ct),"invalid_documents","Documento no autorizado.");return Results.Ok(new{Id=await s.ApplyAsync(Caller.From(c),r,ct)});});
  api.MapPut("/merchants/{id:guid}/decision",async(Guid id,MerchantDecision r,HttpContext c,MerchantService s,CancellationToken ct)=>{await s.DecideAsync(Caller.From(c),id,r,ct);return Results.NoContent();});
  api.MapPost("/merchants/{id:guid}/branches",async(Guid id,SaveBranch r,HttpContext c,MerchantService s,CancellationToken ct)=>Results.Ok(new{Id=await s.SaveBranchAsync(Caller.From(c),id,Guid.CreateVersion7(),r,ct)}));
  api.MapPut("/merchants/{merchant:guid}/branches/{branch:guid}",async(Guid merchant,Guid branch,SaveBranch r,HttpContext c,MerchantService s,CancellationToken ct)=>Results.Ok(new{Id=await s.SaveBranchAsync(Caller.From(c),merchant,branch,r,ct)}));
  api.MapGet("/merchant/branches",async(HttpContext c,MerchantService s,CancellationToken ct)=>Results.Ok(await s.OwnBranchesAsync(Caller.From(c),ct)));
  api.MapGet("/merchant/branches/{id:guid}/products",async(Guid id,HttpContext c,MerchantService s,CancellationToken ct)=>{Guard.Require(Caller.From(c).Administrator||await s.CanOperateAsync(Caller.From(c).Id,id,ct),"branch_access_denied","Sucursal no autorizada.",403);return Results.Ok(await s.ProductsAsync(id,[],ct));});
  api.MapPost("/merchant/branches/{id:guid}/products",async(Guid id,SaveProduct r,HttpContext c,MerchantService s,CancellationToken ct)=>Results.Ok(new{Id=await s.SaveProductAsync(Caller.From(c),id,Guid.CreateVersion7(),r,ct)}));
  api.MapPut("/merchant/branches/{branch:guid}/products/{id:guid}",async(Guid branch,Guid id,SaveProduct r,HttpContext c,MerchantService s,CancellationToken ct)=>Results.Ok(new{Id=await s.SaveProductAsync(Caller.From(c),branch,id,r,ct)}));
  api.MapPut("/merchant/branches/{id:guid}/members",async(Guid id,MemberInput r,HttpContext c,MerchantService s,IIdentityDirectory identities,CancellationToken ct)=>
  {var caller=Caller.From(c);Guard.Require(await s.CanManageMembersAsync(caller,id,ct),"membership_access_denied","Solo el propietario puede gestionar el equipo.",403);var user=r.Email is null?r.UserId:await identities.ActiveByEmailAsync(r.Email,ct)??Guid.Empty;Guard.Require(r.Role=="remove"||await identities.ActiveAsync(user,ct),"inactive_member","Cuenta no activa.");await s.SetMemberAsync(caller,id,user,r.Role,ct);return Results.NoContent();});
  api.MapGet("/merchant/branches/{id:guid}/members",async(Guid id,HttpContext c,MerchantService s,IIdentityDirectory identities,CancellationToken ct)=>
  {Guard.Require(await s.CanManageMembersAsync(Caller.From(c),id,ct),"membership_access_denied","Equipo no autorizado.",403);var list=new List<object>();foreach(var member in await s.MembersAsync(id,ct))list.Add(new{UserId=member.User,Role=member.Role,Email=await identities.EmailAsync(member.User,ct)});return Results.Ok(list);});
  api.MapPost("/couriers/apply",async(CourierApplication r,HttpContext c,DispatchService s,IDocumentDirectory files,CancellationToken ct)=>{foreach(var id in r.DocumentIds??[])Guard.Require(Guid.TryParse(id,out var parsed)&&await files.OwnsAsync(Caller.From(c).Id,parsed,ct),"invalid_documents","Documento no autorizado.");await s.ApplyAsync(Caller.From(c),r,ct);return Results.NoContent();});
  api.MapGet("/admin/couriers",async(HttpContext c,DispatchService s,CancellationToken ct)=>Results.Ok(await s.CouriersAsync(Caller.From(c),ct)));
  api.MapPut("/admin/couriers/{id:guid}/decision",async(Guid id,CourierDecision r,HttpContext c,DispatchService s,CancellationToken ct)=>{await s.DecideAsync(Caller.From(c),id,r,ct);return Results.NoContent();});
  api.MapPut("/couriers/availability",async(CourierAvailability r,HttpContext c,DispatchService s,CancellationToken ct)=>{await s.AvailabilityAsync(Caller.From(c),r,ct);return Results.NoContent();});
  api.MapGet("/couriers/offers",async(HttpContext c,DispatchService s,CancellationToken ct)=>Results.Ok(await s.OffersAsync(Caller.From(c),ct)));
  api.MapPost("/couriers/offers/{id:guid}/decision",async(Guid id,DecisionInput r,HttpContext c,DispatchService s,CancellationToken ct)=>{await s.DecideOfferAsync(Caller.From(c),id,r.Accept,ct);return Results.NoContent();});
  api.MapGet("/couriers/deliveries",async(HttpContext c,DeliveryService s,CancellationToken ct)=>Results.Ok(await s.ActiveOrdersAsync(Caller.From(c).Id,ct)));
  api.MapGet("/deliveries/{id:guid}",async(Guid id,HttpContext c,DeliveryService s,CancellationToken ct)=>Results.Ok(await s.GetAsync(Caller.From(c),id,ct)));
  api.MapPost("/deliveries/{id:guid}/actions",async(Guid id,DeliveryAction r,HttpContext c,DeliveryService s,CancellationToken ct)=>{await s.ActionAsync(Caller.From(c),id,r,ct);return Results.NoContent();});
  api.MapPost("/deliveries/{id:guid}/location",async(Guid id,Coordinate r,HttpContext c,DeliveryService s,CancellationToken ct)=>{await s.UpdateLocationAsync(Caller.From(c),id,r,ct);return Results.NoContent();});
  api.MapPut("/admin/orders/{id:guid}/courier",async(Guid id,ReassignmentInput r,HttpContext c,DispatchService s,IDeliveryDirectory d,CancellationToken ct)=>{await s.ReassignAsync(Caller.From(c),id,r.CourierId,r.Reason,d,ct);return Results.NoContent();});
  api.MapGet("/payments/settings",(PaymentService s)=>Results.Ok(s.Settings()));
  api.MapGet("/orders/{id:guid}/payment",async(Guid id,HttpContext c,PaymentService s,OrderService orders,CancellationToken ct)=>{Guard.Require(await orders.CanReadAsync(Caller.From(c),id,ct),"payment_access_denied","Pago no autorizado.",403);return Results.Ok(await s.InfoAsync(id,ct));});
  api.MapPost("/admin/orders/{id:guid}/refunds",async(Guid id,RefundInput r,HttpContext c,PaymentService s,CancellationToken ct)=>{await s.RefundAsync(Caller.From(c),id,r,ct);return Results.NoContent();});
  api.MapPost("/admin/payments/reconcile",async(HttpContext c,PaymentService s,CancellationToken ct)=>Results.Ok(await s.ReconcileAsync(Caller.From(c),ct)));
  api.MapGet("/statements/{kind}/{participant:guid}",async(string kind,Guid participant,HttpContext c,LedgerService s,CancellationToken ct)=>Results.Ok(await s.StatementAsync(Caller.From(c),kind,participant,ct)));
  api.MapPost("/admin/couriers/{id:guid}/cash-collections",async(Guid id,CashCollectionInput r,HttpContext c,LedgerService s,CancellationToken ct)=>{await s.CashCollectionAsync(Caller.From(c),id,r.OperationId,r.Amount,r.ExternalReference,r.Reason,ct);return Results.NoContent();});
  api.MapPost("/admin/orders/{id:guid}/incentives",async(Guid id,IncentiveInput r,HttpContext c,LedgerService s,CancellationToken ct)=>{await s.IncentiveAsync(Caller.From(c),id,r.CourierId,r.OperationId,r.Amount,r.Reason,ct);return Results.NoContent();});
  api.MapPost("/admin/settlements/{kind}/{participant:guid}",async(string kind,Guid participant,SettlementInput r,HttpContext c,LedgerService s,CancellationToken ct)=>Results.Ok(new{Id=await s.CloseSettlementAsync(Caller.From(c),kind,participant,r.OperationId,r.Reason,ct)}));
  api.MapPut("/admin/settlements/{id:guid}/paid",async(Guid id,PaidInput r,HttpContext c,LedgerService s,CancellationToken ct)=>{await s.MarkPaidAsync(Caller.From(c),id,r.ExternalReference,r.Reason,ct);return Results.NoContent();});
  api.MapGet("/support/cases",async(HttpContext c,SupportService s,CancellationToken ct)=>Results.Ok(await s.ListAsync(Caller.From(c),ct)));
  api.MapPost("/support/cases",async(CaseInput r,HttpContext c,SupportService s,CancellationToken ct)=>Results.Ok(new{Id=await s.OpenAsync(Caller.From(c),r,ct)}));
  api.MapGet("/support/cases/{id:guid}",async(Guid id,HttpContext c,SupportService s,CancellationToken ct)=>Results.Ok(await s.DetailsAsync(Caller.From(c),id,ct)));
  api.MapPut("/support/cases/{id:guid}",async(Guid id,CaseAction r,HttpContext c,SupportService s,CancellationToken ct)=>{await s.UpdateAsync(Caller.From(c),id,r,ct);return Results.NoContent();});
  api.MapPost("/support/cases/{id:guid}/notes",async(Guid id,MessageInput r,HttpContext c,SupportService s,CancellationToken ct)=>{await s.NoteAsync(Caller.From(c),id,r.Message,ct);return Results.NoContent();});
  api.MapGet("/notifications",async(HttpContext c,NotificationService s,CancellationToken ct)=>Results.Ok(await s.ListAsync(Caller.From(c).Id,ct)));
  api.MapPost("/notifications/{id:guid}/read",async(Guid id,HttpContext c,NotificationService s,CancellationToken ct)=>{await s.ReadAsync(Caller.From(c).Id,id,ct);return Results.NoContent();});
  api.MapGet("/admin/zones",async(HttpContext c,ConfigurationService s,CancellationToken ct)=>{Caller.From(c).RequirePermission("configuration.manage");return Results.Ok(await s.ZonesAsync(ct));});
  api.MapPost("/admin/zones",async(SaveZone r,HttpContext c,ConfigurationService s,CancellationToken ct)=>Results.Ok(new{Id=await s.SaveAsync(Caller.From(c),Guid.CreateVersion7(),r,ct)}));
  api.MapPut("/admin/zones/{id:guid}",async(Guid id,SaveZone r,HttpContext c,ConfigurationService s,CancellationToken ct)=>Results.Ok(new{Id=await s.SaveAsync(Caller.From(c),id,r,ct)}));
  api.MapGet("/admin/parameters",async(HttpContext c,ConfigurationService s,CancellationToken ct)=>Results.Ok(await s.ParametersAsync(Caller.From(c),ct)));
  api.MapPut("/admin/parameters/{name}",async(string name,SaveParameter r,HttpContext c,ConfigurationService s,CancellationToken ct)=>{await s.SaveParameterAsync(Caller.From(c),name,r,ct);return Results.NoContent();});
  api.MapGet("/admin/audit",async(int? limit,HttpContext c,ConfigurationService s,CancellationToken ct)=>Results.Ok(await s.AuditLogAsync(Caller.From(c),limit??100,ct)));
  api.MapPost("/admin/coupons",async(CouponInput r,HttpContext c,PricingService s,CancellationToken ct)=>Results.Ok(new{Id=await s.SaveCouponAsync(Caller.From(c),Guid.CreateVersion7(),r,ct)}));
  api.MapPut("/admin/coupons/{id:guid}",async(Guid id,CouponInput r,HttpContext c,PricingService s,CancellationToken ct)=>Results.Ok(new{Id=await s.SaveCouponAsync(Caller.From(c),id,r,ct)}));
  api.MapPost("/documents",async(IFormFile file,HttpContext c,PrivateDocumentStore s,CancellationToken ct)=>Results.Ok(await s.UploadAsync(Caller.From(c),file,ct))).DisableAntiforgery();
  api.MapGet("/documents/{id:guid}",async(Guid id,HttpContext c,PrivateDocumentStore s,SupportService support,CancellationToken ct)=>{var caller=Caller.From(c);var caseReview=caller.Can("support.manage")&&await support.CaseEvidenceAsync(id,ct);var file=await s.GetAsync(caller,id,ct,caseReview);return Results.File(s.PathFor(id),file.ContentType,file.Name);});
 }
}

