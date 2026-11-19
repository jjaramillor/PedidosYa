using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Delivery.BuildingBlocks;
using Delivery.Contracts;
using Delivery.Modules.Configuration;
using Delivery.Modules.Customers;
using Delivery.Modules.Deliveries;
using Delivery.Modules.Dispatch;
using Delivery.Modules.Identity;
using Delivery.Modules.Ledger;
using Delivery.Modules.Merchants;
using Delivery.Modules.Orders;
using Delivery.Modules.Payments;
using Delivery.Modules.Pricing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Delivery.Tests;

public sealed class MvpIntegrationTests(IntegrationFixture fixture):IClassFixture<IntegrationFixture>
{
    private static readonly CancellationToken Ct=CancellationToken.None;
    private T Service<T>() where T:notnull=>fixture.Factory.Services.GetRequiredService<T>();
    private async Task Drain(){for(var i=0;i<100&&await fixture.Dispatcher.DispatchOneAsync(Ct);i++){} }
    private async Task<AuthSession> Register(string email)
    {
        using var scope=fixture.Factory.Services.CreateScope();var identity=scope.ServiceProvider.GetRequiredService<IdentityService>();
        var challenge=await identity.RequestAsync(new(email,email,true,AcceptPrivacy:true),"mvp-test",Ct);
        await Drain();return await identity.VerifyAsync(new(challenge.ChallengeId,fixture.Sender.Codes[email]),"mvp-test",Ct);
    }
    private static Caller CallerFor(AuthSession user,bool admin=false)=>new(user.User.Id,admin,"mvp-test");
    [Fact]
    public void Manual_outbox_mode_has_no_running_background_dispatcher()
        =>Assert.True(fixture.Dispatcher.ExecuteTask is null||fixture.Dispatcher.ExecuteTask.IsCompleted,
            "Messaging:RunWorker=false must leave message dispatch under test control.");
    private async Task<string> Document(Caller user)
    {
        using var stream=new MemoryStream(System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\nTest evidence"));
        var file=new FormFile(stream,0,stream.Length,"file","application.pdf"){Headers=new HeaderDictionary(),ContentType="application/pdf"};
        return (await Service<PrivateDocumentStore>().UploadAsync(user,file,Ct)).Id.ToString();
    }

    [Theory]
    [InlineData("card")]
    [InlineData("cash")]
    public async Task Checkout_to_verified_delivery_records_balanced_settlement_and_stops_GPS(string method)
    {
        await fixture.ResetAsync();
        var admin=CallerFor(await Register("admin@mvp.test"),true);
        var customer=await Register("customer@mvp.test");var customerCaller=CallerFor(customer);
        var owner=CallerFor(await Register("merchant@mvp.test"));var courier=CallerFor(await Register("courier@mvp.test"));
        var zone=await Service<ConfigurationService>().SaveAsync(admin,Guid.NewGuid(),new("Lima","{\"type\":\"Polygon\",\"coordinates\":[[[-77.1,-12.1],[-77,-12.1],[-77,-12],[-77.1,-12],[-77.1,-12.1]]]}",true,500,100,100,1500,400,120,method=="cash","test zone"),Ct);
        if(method=="cash")await Service<ConfigurationService>().SaveParameterAsync(admin,"cash.enabled",new(System.Text.Json.JsonSerializer.SerializeToElement(true),"cash test"),Ct);
        var merchant=await Service<MerchantService>().ApplyAsync(owner,new("Restaurant","20123456789","bank-account",[await Document(owner)]),Ct);
        await Service<MerchantService>().DecideAsync(admin,merchant,new("approved","verified documents"),Ct);
        var branch=await Service<MerchantService>().SaveBranchAsync(owner,merchant,Guid.NewGuid(),new("Central",new(-12.04,-77.04),0,15,true,Enumerable.Range(0,7).Select(day=>new OpeningWindow(day,0,1440)).ToArray(),null),Ct);
        var product=await Service<MerchantService>().SaveProductAsync(owner,branch,Guid.NewGuid(),new("Lunch","Meals","Meal",null,[],2500,10,true,[]),Ct);
        var address=await Service<CustomerService>().SaveAsync(customerCaller,Guid.NewGuid(),new("Home","Street 1","Door",new(-12.05,-77.05)),Ct);
        await Service<DispatchService>().ApplyAsync(courier,new("bicycle",[await Document(courier)]),Ct);
        await Service<DispatchService>().DecideAsync(admin,courier.Id,new("approved","verified documents"),Ct);
        await Service<DispatchService>().AvailabilityAsync(courier,new(true,zone,new(-12.04,-77.04)),Ct);
        var quote=await Service<PricingService>().QuoteAsync(customerCaller.Id,address,branch,[new(product,1,[])],null,200,Ct);
        var operation=Guid.NewGuid();var input=new CheckoutInput(operation,quote.Id,method,method=="card"?ModuleSql.Encode(new CardToken("sandbox-approved","visa",null)):null);
        var order=await Service<OrderService>().PlaceAsync(customerCaller,input,Ct);
        Assert.Equal(order.Id,(await Service<OrderService>().PlaceAsync(customerCaller,input,Ct)).Id);
        await Drain();Assert.Equal("confirmed",(await Service<OrderService>().GetAsync(order.Id,Ct)).State);
        await Service<OrderService>().ActionAsync(owner,order.Id,new(Guid.NewGuid(),"accepted","accept"),Ct);await Drain();
        var offer=Assert.Single(await Service<DispatchService>().OffersAsync(courier,Ct));
        await Service<DispatchService>().DecideOfferAsync(courier,offer.Id,true,Ct);await Drain();
        await Service<OrderService>().ActionAsync(owner,order.Id,new(Guid.NewGuid(),"preparing","prepare"),Ct);
        await Service<OrderService>().ActionAsync(owner,order.Id,new(Guid.NewGuid(),"ready","ready"),Ct);await Drain();
        await Service<DeliveryService>().ActionAsync(courier,order.Id,new(Guid.NewGuid(),"arrived",null,null,"arrived"),Ct);
        var pickup=(await Service<DeliveryService>().GetAsync(owner,order.Id,Ct)).PickupPin;
        Assert.Null((await Service<DeliveryService>().GetAsync(courier,order.Id,Ct)).PickupPin);
        await Service<DeliveryService>().ActionAsync(courier,order.Id,new(Guid.NewGuid(),"picked_up",pickup,null,"pickup verified"),Ct);await Drain();
        await Assert.ThrowsAsync<ApiException>(()=>Service<Delivery.Api.OrderActions>().ExecuteAsync(owner,order.Id,new(Guid.NewGuid(),"cancelled","late cancellation"),Ct));
        await Service<DeliveryService>().ActionAsync(courier,order.Id,new(Guid.NewGuid(),"in_transit",null,null,"route"),Ct);await Drain();
        await Service<DeliveryService>().UpdateLocationAsync(courier,order.Id,new(-12.045,-77.045),Ct);await Drain();
        var pin=(await Service<DeliveryService>().GetAsync(customerCaller,order.Id,Ct)).DeliveryPin;
        await Service<DeliveryService>().ActionAsync(courier,order.Id,new(Guid.NewGuid(),"delivered",pin,null,"delivery verified",method=="cash"),Ct);await Drain();
        Assert.Equal("closed",(await Service<OrderService>().GetAsync(order.Id,Ct)).State);
        Assert.Equal(quote.Total,(await Service<PaymentService>().InfoAsync(order.Id,Ct)).Captured);
        Assert.Equal(0,await fixture.CountAsync("SELECT count(*) FROM (SELECT voucher_id FROM ledger.lines GROUP BY voucher_id HAVING sum(amount)<>0) imbalance"));
        await Assert.ThrowsAsync<ApiException>(()=>Service<DeliveryService>().UpdateLocationAsync(courier,order.Id,new(-12.05,-77.05),Ct));
        var refund=new RefundInput(Guid.NewGuid(),quote.Total+1,"test over-refund");
        await Assert.ThrowsAsync<ApiException>(()=>Service<PaymentService>().RefundAsync(admin,order.Id,refund,Ct));
        await Service<PaymentService>().RefundAsync(admin,order.Id,new(Guid.NewGuid(),100,"partial refund"),Ct);await Drain();
        Assert.Equal(100,(await Service<PaymentService>().InfoAsync(order.Id,Ct)).Refunded);
        var incentiveId=Guid.NewGuid();await Service<LedgerService>().IncentiveAsync(admin,order.Id,courier.Id,incentiveId,100,"delivery incentive",Ct);
        await Service<LedgerService>().IncentiveAsync(admin,order.Id,courier.Id,incentiveId,100,"delivery incentive retry",Ct);
        Assert.Equal(1,await fixture.CountAsync("SELECT count(*) FROM ledger.vouchers WHERE kind='incentive'"));
        fixture.Client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",customer.AccessToken);
        var restricted=await fixture.Client.GetAsync("/api/v1/admin/zones");Assert.Equal(HttpStatusCode.Forbidden,restricted.StatusCode);
    }

    [Fact]
    public async Task Browser_verify_requires_antiforgery_while_native_bearer_contract_is_unchanged()
    {
        await fixture.ResetAsync();
        var response=await fixture.Client.PostAsJsonAsync("/api/v1/identity/browser/verify",new VerifyOtp(Guid.NewGuid(),"123456"));
        Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);
        var user=await Register("native@mvp.test");fixture.Client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",user.AccessToken);
        var address=await fixture.Client.PostAsJsonAsync("/api/v1/addresses",new SaveAddress("Home","Street","",new(-12,-77)));
        Assert.Equal(HttpStatusCode.OK,address.StatusCode);
    }
}
