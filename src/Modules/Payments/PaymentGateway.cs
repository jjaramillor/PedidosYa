using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Delivery.BuildingBlocks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
namespace Delivery.Modules.Payments;

public sealed class PaymentOptions
{
 public string Provider {get;set;}="sandbox";
 public string AccessToken {get;set;}="";
 public string PublicKey {get;set;}="";
 public string WebhookSecret {get;set;}="";
}
public sealed record CardToken(string Token,string PaymentMethodId,string? IssuerId,string? DocumentType=null,string? DocumentNumber=null);
public sealed record GatewayPayment(string Id,string State,string? ClientSecret,long Captured,string Currency,long Amount,Guid OrderId,long Refunded=0);
public interface IPaymentGateway
{
 Task<GatewayPayment> CreateAsync(Guid order,long amount,string currency,string email,CardToken token,CancellationToken ct);
 Task<GatewayPayment> GetAsync(string id,CancellationToken ct);
 Task CaptureAsync(string id,Guid operation,CancellationToken ct);
 Task CancelAsync(string id,Guid operation,CancellationToken ct);
 Task RefundAsync(string id,long amount,Guid operation,CancellationToken ct);
}
public sealed class PaymentGateway(IOptions<PaymentOptions> options,IHttpClientFactory clients,IHostEnvironment environment):IPaymentGateway
{
 private bool Sandbox
 {
  get
  {
   Guard.Require(options.Value.Provider is "sandbox" or "mercadopago","unknown_payment_provider","Proveedor no configurado.",503);
   if(options.Value.Provider!="sandbox")return false;
   Guard.Require(environment.IsDevelopment(),"sandbox_disabled","Pagos de prueba deshabilitados fuera de desarrollo.",503);return true;
  }
 }
 public Task<GatewayPayment> CreateAsync(Guid order,long amount,string currency,string email,CardToken token,CancellationToken ct)
 {
  if(Sandbox)
  {
   Guard.Require(token.Token is "sandbox-approved" or "sandbox-declined","invalid_test_token","Utiliza un token de prueba válido.");
   return Task.FromResult(new GatewayPayment(order.ToString("N"),token.Token=="sandbox-approved"?"authorized":"rejected",null,0,currency,amount,order));
  }
  return CreateRemoteAsync(order,amount,currency,email,token,ct);
 }
 private async Task<GatewayPayment> CreateRemoteAsync(Guid order,long amount,string currency,string email,CardToken token,CancellationToken ct)
 {
  Guard.Require(!string.IsNullOrWhiteSpace(token.Token)&&token.Token.Length<=200&&!token.Token.All(char.IsDigit)
   &&!string.IsNullOrWhiteSpace(token.PaymentMethodId),"invalid_card_token","La tarjeta debe tokenizarse mediante el proveedor.");
  var payload=new Dictionary<string,object?>
  {
   ["transaction_amount"]=amount/100m,["token"]=token.Token,["payment_method_id"]=token.PaymentMethodId,
   ["issuer_id"]=token.IssuerId,["installments"]=1,["payer"]=new{email,identification=token.DocumentType is null?null:new{type=token.DocumentType,number=token.DocumentNumber}},["capture"]=false,["external_reference"]=order.ToString()
  };
  using var response=await SendAsync(HttpMethod.Post,"/v1/payments",order,payload,ct);return Parse(await response.Content.ReadFromJsonAsync<JsonElement>(ct));
 }
 public async Task<GatewayPayment> GetAsync(string id,CancellationToken ct)
 {
  if(Sandbox)return new(id,"authorized",null,0,"PEN",0,Guid.ParseExact(id,"N"));
  Guard.Require(long.TryParse(id,out _),"invalid_provider_id","Identificador de pasarela inválido.");
  using var response=await SendAsync(HttpMethod.Get,"/v1/payments/"+id,Guid.Empty,null,ct);
  var payment=Parse(await response.Content.ReadFromJsonAsync<JsonElement>(ct));
  using var refundsResponse=await SendAsync(HttpMethod.Get,"/v1/payments/"+id+"/refunds",Guid.Empty,null,ct);
  var refunds=await refundsResponse.Content.ReadFromJsonAsync<JsonElement>(ct);long refunded=0;
  foreach(var refund in refunds.EnumerateArray())
   if(refund.TryGetProperty("status",out var status)&&status.GetString()=="approved")
    refunded=checked(refunded+(long)decimal.Round(refund.GetProperty("amount").GetDecimal()*100m,0,MidpointRounding.AwayFromZero));
  return payment with {Refunded=refunded};
 }
 public async Task CaptureAsync(string id,Guid operation,CancellationToken ct)
 {
  if(Sandbox)return;using var response=await SendAsync(HttpMethod.Put,"/v1/payments/"+id,operation,new{capture=true},ct);
  var result=Parse(await response.Content.ReadFromJsonAsync<JsonElement>(ct));Guard.Require(result.State=="captured","capture_pending","La captura no se confirmó.",409);
 }
 public async Task CancelAsync(string id,Guid operation,CancellationToken ct)
 {
  if(Sandbox)return;using var response=await SendAsync(HttpMethod.Put,"/v1/payments/"+id,operation,new{status="cancelled"},ct);
 }
 public async Task RefundAsync(string id,long amount,Guid operation,CancellationToken ct)
 {
  if(Sandbox)return;using var response=await SendAsync(HttpMethod.Post,"/v1/payments/"+id+"/refunds",operation,new{amount=amount/100m},ct);
 }
 private async Task<HttpResponseMessage> SendAsync(HttpMethod method,string path,Guid operation,object? payload,CancellationToken ct)
 {
  Guard.Require(!string.IsNullOrWhiteSpace(options.Value.AccessToken),"payment_provider_unconfigured","Faltan credenciales de pagos.",503);
  var client=clients.CreateClient("payments");using var request=new HttpRequestMessage(method,"https://api.mercadopago.com"+path);
  request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",options.Value.AccessToken);
  if(operation!=Guid.Empty)request.Headers.Add("X-Idempotency-Key",operation.ToString());
  if(payload is not null)request.Content=JsonContent.Create(payload);
  var response=await client.SendAsync(request,ct);
  if(!response.IsSuccessStatusCode){response.Dispose();throw new ApiException(502,"payment_provider_error","La pasarela no pudo completar la operación.");}
  return response;
 }
 private static GatewayPayment Parse(JsonElement json)
 {
  var state=json.GetProperty("status").GetString() switch{"authorized"=>"authorized","approved"=>"captured","rejected"=>"rejected","cancelled"=>"cancelled","refunded"=>"refunded",_=>"pending"};
  var amount=checked((long)decimal.Round(json.GetProperty("transaction_amount").GetDecimal()*100m,0,MidpointRounding.AwayFromZero));
  return new(json.GetProperty("id").ToString(),state,null,state is "captured" or "refunded"?amount:0,json.GetProperty("currency_id").GetString()!,amount,Guid.Parse(json.GetProperty("external_reference").GetString()!));
 }
}

