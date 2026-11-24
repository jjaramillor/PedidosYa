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
using Delivery.Modules.Support;
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

    private sealed record DeliveryScenario(Caller Admin,AuthSession Customer,Caller Owner,Caller Courier,AuthSession CourierSession,QuoteSnapshot Quote,OrderView Order)
    {public AuthSession OwnerSession {get;init;}=null!;}
    private async Task<DeliveryScenario> PrepareDelivery(string method)
    {
        await fixture.ResetAsync();
        var admin=CallerFor(await Register("admin@mvp.test"),true);
        var customer=await Register("customer@mvp.test");var customerCaller=CallerFor(customer);
        var ownerSession=await Register("merchant@mvp.test");var owner=CallerFor(ownerSession);var courierSession=await Register("courier@mvp.test");var courier=CallerFor(courierSession);
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
        // An expired offer cannot assign a courier, even if the client still displays it.
        await using(var expire=fixture.Source.CreateCommand("UPDATE dispatch.offers SET expires_at=now()-interval '1 second' WHERE id=@id"))
        {expire.Parameters.AddWithValue("id",offer.Id);await expire.ExecuteNonQueryAsync();}
        foreach(var accept in new[]{true,false})
        {
            var expired=await Assert.ThrowsAsync<ApiException>(()=>Service<DispatchService>().DecideOfferAsync(courier,offer.Id,accept,Ct));
            Assert.Equal(409,expired.StatusCode);Assert.Equal("offer_expired",expired.Code);
        }
        Assert.Null(await Service<DispatchService>().AssignedAsync(order.Id,Ct));
        await using(var renew=fixture.Source.CreateCommand("UPDATE dispatch.offers SET expires_at=now()+interval '2 minutes' WHERE id=@id"))
        {renew.Parameters.AddWithValue("id",offer.Id);await renew.ExecuteNonQueryAsync();}
        await Service<DispatchService>().DecideOfferAsync(courier,offer.Id,true,Ct);await Drain();
        await Service<OrderService>().ActionAsync(owner,order.Id,new(Guid.NewGuid(),"preparing","prepare"),Ct);
        await Service<OrderService>().ActionAsync(owner,order.Id,new(Guid.NewGuid(),"ready","ready"),Ct);await Drain();
        await Service<DeliveryService>().ActionAsync(courier,order.Id,new(Guid.NewGuid(),"arrived",null,null,"arrived"),Ct);
        return new(admin,customer,owner,courier,courierSession,quote,order){OwnerSession=ownerSession};
    }
    private async Task StartRoute(DeliveryScenario scenario)
    {
        var pickup=(await Service<DeliveryService>().GetAsync(scenario.Owner,scenario.Order.Id,Ct)).PickupPin;
        await Service<DeliveryService>().ActionAsync(scenario.Courier,scenario.Order.Id,new(Guid.NewGuid(),"picked_up",pickup,null,"verified pickup"),Ct);await Drain();
        await Service<DeliveryService>().ActionAsync(scenario.Courier,scenario.Order.Id,new(Guid.NewGuid(),"in_transit",null,null,"start route"),Ct);await Drain();
    }

    [Theory]
    [InlineData("card")]
    [InlineData("cash")]
    public async Task Checkout_to_verified_delivery_records_balanced_settlement_and_stops_GPS(string method)
    {
        var (admin,customer,owner,courier,courierSession,quote,order)=await PrepareDelivery(method);
        var customerCaller=CallerFor(customer);
        var pickup=(await Service<DeliveryService>().GetAsync(owner,order.Id,Ct)).PickupPin;
        Assert.Null((await Service<DeliveryService>().GetAsync(courier,order.Id,Ct)).PickupPin);
        fixture.Client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",courierSession.AccessToken);
        var invalidPin=pickup=="000000"?"111111":"000000";
        var invalidResponse=await fixture.Client.PostAsJsonAsync($"/api/v1/deliveries/{order.Id}/actions",new DeliveryAction(Guid.NewGuid(),"picked_up",invalidPin,null,"wrong pickup PIN"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity,invalidResponse.StatusCode);
        Assert.Contains("invalid_delivery_pin",await invalidResponse.Content.ReadAsStringAsync());
        Assert.Equal("arrived",(await Service<DeliveryService>().GetAsync(courier,order.Id,Ct)).State);
        Assert.Equal(1,await fixture.CountAsync("SELECT sum(pickup_attempts) FROM deliveries.deliveries"));
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

    [Theory]
    [InlineData("picked_up")]
    [InlineData("delivered")]
    public async Task Five_invalid_PINs_block_even_the_correct_PIN_without_advancing(string target)
    {
        var scenario=await PrepareDelivery("card");
        if(target=="delivered")await StartRoute(scenario);
        var authorized=target=="picked_up"?scenario.Owner:CallerFor(scenario.Customer);
        var info=await Service<DeliveryService>().GetAsync(authorized,scenario.Order.Id,Ct);
        var correct=target=="picked_up"?info.PickupPin:info.DeliveryPin;
        var wrong=correct=="000000"?"111111":"000000";
        var state=target=="picked_up"?"arrived":"in_transit";
        var operations=await fixture.CountAsync("SELECT count(*) FROM deliveries.operations");
        var counter=target=="picked_up"?"pickup_attempts":"drop_attempts";
        for(var attempt=1;attempt<=5;attempt++)
        {
            var invalid=await Assert.ThrowsAsync<ApiException>(()=>Service<DeliveryService>().ActionAsync(scenario.Courier,scenario.Order.Id,new(Guid.NewGuid(),target,wrong,null,"invalid PIN test"),Ct));
            Assert.Equal(422,invalid.StatusCode);Assert.Equal("invalid_delivery_pin",invalid.Code);
            Assert.Equal(attempt,await fixture.CountAsync($"SELECT {counter} FROM deliveries.deliveries"));
        }
        var blocked=await Assert.ThrowsAsync<ApiException>(()=>Service<DeliveryService>().ActionAsync(scenario.Courier,scenario.Order.Id,new(Guid.NewGuid(),target,correct,null,"correct PIN after lock"),Ct));
        Assert.Equal(422,blocked.StatusCode);
        Assert.Equal(5,await fixture.CountAsync($"SELECT {counter} FROM deliveries.deliveries"));
        Assert.Equal(operations,await fixture.CountAsync("SELECT count(*) FROM deliveries.operations"));
        Assert.Equal(state,(await Service<DeliveryService>().GetAsync(scenario.Courier,scenario.Order.Id,Ct)).State);
        await Drain();
        Assert.Equal(0,(await Service<PaymentService>().InfoAsync(scenario.Order.Id,Ct)).Captured);
        Assert.Equal(0,await fixture.CountAsync("SELECT count(*) FROM ledger.deliveries"));
    }

    [Theory]
    [InlineData("card",false)]
    [InlineData("cash",false)]
    [InlineData("card",true)]
    [InlineData("cash",true)]
    public async Task Incident_can_return_or_resume_with_audited_support_and_idempotent_closure(string method,bool resume)
    {
        var scenario=await PrepareDelivery(method);await StartRoute(scenario);
        var courier=scenario.Courier;var order=scenario.Order.Id;
        var directReturn=await Assert.ThrowsAsync<ApiException>(()=>Service<DeliveryService>().ActionAsync(courier,order,new(Guid.NewGuid(),"returned",null,null,"return without incident"),Ct));
        Assert.Equal("invalid_delivery_transition",directReturn.Code);
        var unauthorized=await Assert.ThrowsAsync<ApiException>(()=>Service<DeliveryService>().ActionAsync(CallerFor(scenario.Customer),order,new(Guid.NewGuid(),"incident",null,null,"customer cannot impersonate courier"),Ct));
        Assert.Equal(403,unauthorized.StatusCode);
        var incident=new DeliveryAction(Guid.NewGuid(),"incident",null,null,"receiver unavailable");
        await Service<DeliveryService>().ActionAsync(courier,order,incident,Ct);
        await Service<DeliveryService>().ActionAsync(courier,order,incident,Ct);await Drain();
        Assert.Equal("incident",(await Service<OrderService>().GetAsync(order,Ct)).State);
        Assert.Equal(1,await fixture.CountAsync("SELECT count(*) FROM deliveries.operations WHERE state='incident'"));
        var support=Service<SupportService>();
        var caseId=await support.OpenAsync(courier,new(order,"Delivery incident","Receiver unavailable; operator review requested",[]),Ct);
        var denied=await Assert.ThrowsAsync<ApiException>(()=>support.UpdateAsync(CallerFor(scenario.Customer),caseId,new("closed","unauthorized resolution",null),Ct));
        Assert.Equal(403,denied.StatusCode);
        await support.NoteAsync(scenario.Admin,caseId,"Contact attempt registered",Ct);
        await support.UpdateAsync(scenario.Admin,caseId,new("investigating","Review in progress",scenario.Admin.Id),Ct);
        DeliveryAction terminal;
        if(resume)
        {
            await Service<DeliveryService>().ActionAsync(courier,order,new(Guid.NewGuid(),"in_transit",null,null,"receiver now available"),Ct);await Drain();
            var pin=(await Service<DeliveryService>().GetAsync(CallerFor(scenario.Customer),order,Ct)).DeliveryPin;
            terminal=new(Guid.NewGuid(),"delivered",pin,null,"resolved incident and verified delivery",method=="cash");
        }
        else terminal=new(Guid.NewGuid(),"returned",null,null,"return confirmed at restaurant");
        await Task.WhenAll(Service<DeliveryService>().ActionAsync(courier,order,terminal,Ct),
            Service<DeliveryService>().ActionAsync(courier,order,terminal,Ct));await Drain();
        await Service<DeliveryService>().ActionAsync(courier,order,terminal,Ct);await Drain();
        Assert.Equal(resume?"closed":"returned",(await Service<OrderService>().GetAsync(order,Ct)).State);
        var payment=await Service<PaymentService>().InfoAsync(order,Ct);
        Assert.Equal(resume?"captured":"cancelled",payment.State);
        Assert.Equal(resume?scenario.Quote.Total:0,payment.Captured);
        Assert.Equal(resume?1:0,await fixture.CountAsync("SELECT count(*) FROM ledger.deliveries"));
        Assert.Equal(resume?1:0,await fixture.CountAsync("SELECT count(*) FROM ledger.vouchers WHERE kind='delivery'"));
        Assert.Equal(0,await fixture.CountAsync("SELECT count(*) FROM (SELECT voucher_id FROM ledger.lines GROUP BY voucher_id HAVING sum(amount)<>0) imbalance"));
        Assert.Equal("completed",Assert.Single(await Service<DispatchService>().OffersAsync(courier,Ct)).State);
        await Assert.ThrowsAsync<ApiException>(()=>Service<DeliveryService>().UpdateLocationAsync(courier,order,new(-12.05,-77.05),Ct));
        await support.UpdateAsync(scenario.Admin,caseId,new("closed",resume?"Delivered after contact":"Returned and authorization cancelled",scenario.Admin.Id),Ct);
        Assert.Equal("closed",Assert.Single(await support.ListAsync(scenario.Admin,Ct)).State);
        var reopen=await Assert.ThrowsAsync<ApiException>(()=>support.UpdateAsync(scenario.Admin,caseId,new("open","attempt reopen",null),Ct));
        Assert.Equal("case_closed",reopen.Code);
        Assert.True(await fixture.CountAsync("SELECT count(*) FROM platform.audit WHERE module='support'")>0);
    }

    [Theory]
    [InlineData("card")]
    [InlineData("cash")]
    public async Task Concurrent_return_and_delivery_have_only_one_terminal_outcome(string method)
    {
        var scenario=await PrepareDelivery(method);await StartRoute(scenario);
        var order=scenario.Order.Id;var delivery=Service<DeliveryService>();
        await delivery.ActionAsync(scenario.Courier,order,new(Guid.NewGuid(),"incident",null,null,"receiver unavailable"),Ct);await Drain();
        var pin=(await delivery.GetAsync(CallerFor(scenario.Customer),order,Ct)).DeliveryPin;
        DeliveryAction[] actions=[new(Guid.NewGuid(),"delivered",pin,null,"receiver found",method=="cash"),new(Guid.NewGuid(),"returned",null,null,"return requested")];
        var outcomes=await Task.WhenAll(actions.Select(action=>Record.ExceptionAsync(()=>delivery.ActionAsync(scenario.Courier,order,action,Ct))));
        Assert.Single(outcomes,error=>error is null);
        var conflict=Assert.IsType<ApiException>(Assert.Single(outcomes,error=>error is not null));
        Assert.Equal(409,conflict.StatusCode);Assert.Equal("invalid_delivery_transition",conflict.Code);
        await Drain();
        var delivered=(await delivery.GetAsync(scenario.Courier,order,Ct)).State=="delivered";
        Assert.Equal(delivered?"closed":"returned",(await Service<OrderService>().GetAsync(order,Ct)).State);
        var payment=await Service<PaymentService>().InfoAsync(order,Ct);
        Assert.Equal(delivered?"captured":"cancelled",payment.State);
        Assert.Equal(delivered?scenario.Quote.Total:0,payment.Captured);
        Assert.Equal(delivered?1:0,await fixture.CountAsync("SELECT count(*) FROM ledger.deliveries"));
        Assert.Equal(1,await fixture.CountAsync("SELECT count(*) FROM deliveries.operations WHERE state IN('delivered','returned')"));
        Assert.Equal(0,await fixture.CountAsync("SELECT count(*) FROM (SELECT voucher_id FROM ledger.lines GROUP BY voucher_id HAVING sum(amount)<>0) imbalance"));
    }

    [Fact]
    public async Task Independent_role_sessions_enforce_access_and_revocation_during_parallel_reads()
    {
        var scenario=await PrepareDelivery("card");
        var operatorSession=await Register("operator@mvp.test");var outsider=await Register("outsider@mvp.test");
        using var scope=fixture.Factory.Services.CreateScope();var roles=scope.ServiceProvider.GetRequiredService<RoleService>();
        await roles.BootstrapAdminAsync("admin@mvp.test",Ct);
        await roles.SaveAsync(scenario.Admin.Id,"delivery-support",new("Delivery support",["support.manage","orders.operate"]),"role-isolation",Ct);
        await roles.AssignAsync(scenario.Admin.Id,operatorSession.User.Id,new(["customer","delivery-support"]),"role-isolation",Ct);
        HttpClient Client(AuthSession session)
        {
            var client=fixture.Factory.CreateClient();
            client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",session.AccessToken);
            client.DefaultRequestHeaders.Add("X-Test-IP","127.0.0.77");return client;
        }
        using var customer=Client(scenario.Customer);using var merchant=Client(scenario.OwnerSession);
        using var courier=Client(scenario.CourierSession);using var support=Client(operatorSession);using var stranger=Client(outsider);
        var path=$"/api/v1/orders/{scenario.Order.Id}";
        var responses=await Task.WhenAll(new[]{customer,merchant,courier,support,stranger}.Select(client=>client.GetAsync(path)));
        try
        {
            Assert.All(responses.Take(4),response=>Assert.Equal(HttpStatusCode.OK,response.StatusCode));
            Assert.Equal(HttpStatusCode.Forbidden,responses[4].StatusCode);
            foreach(var response in responses.Take(4))Assert.Equal(scenario.Order.Id,(await response.Content.ReadFromJsonAsync<OrderView>())!.Id);
        }
        finally{foreach(var response in responses)response.Dispose();}
        var deliveryPath=$"/api/v1/deliveries/{scenario.Order.Id}";
        var customerView=await customer.GetFromJsonAsync<DeliveryInfo>(deliveryPath);
        var merchantView=await merchant.GetFromJsonAsync<DeliveryInfo>(deliveryPath);
        var courierView=await courier.GetFromJsonAsync<DeliveryInfo>(deliveryPath);
        Assert.Null(customerView!.PickupPin);Assert.NotNull(customerView.DeliveryPin);
        Assert.NotNull(merchantView!.PickupPin);Assert.Null(merchantView.DeliveryPin);
        Assert.Null(courierView!.PickupPin);Assert.Null(courierView.DeliveryPin);
        using(var denied=await stranger.GetAsync(deliveryPath))Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);
        using(var denied=await courier.GetAsync(path+"/payment"))Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);
        using(var denied=await customer.PostAsJsonAsync(path+"/actions",new OrderAction(Guid.NewGuid(),"ready","cannot impersonate restaurant")))Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);
        using var opened=await courier.PostAsJsonAsync("/api/v1/support/cases",new CaseInput(scenario.Order.Id,"PIN assistance","Please review pickup",[]));
        Assert.Equal(HttpStatusCode.OK,opened.StatusCode);
        var caseId=(await opened.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetGuid();
        using(var view=await support.GetAsync($"/api/v1/support/cases/{caseId}"))Assert.Equal(HttpStatusCode.OK,view.StatusCode);
        using(var denied=await stranger.GetAsync($"/api/v1/support/cases/{caseId}"))Assert.Equal(HttpStatusCode.NotFound,denied.StatusCode);
        // Existing bearer tokens must immediately lose removed permissions.
        await roles.AssignAsync(scenario.Admin.Id,operatorSession.User.Id,new(["customer"]),"role-isolation",Ct);
        using(var revoked=await support.GetAsync(path))Assert.Equal(HttpStatusCode.Forbidden,revoked.StatusCode);
        using(var revoked=await support.GetAsync($"/api/v1/support/cases/{caseId}"))Assert.Equal(HttpStatusCode.NotFound,revoked.StatusCode);
        using(var loggedOut=await customer.PostAsJsonAsync("/api/v1/identity/logout",new{}))Assert.True(loggedOut.IsSuccessStatusCode);
        using(var revoked=await customer.GetAsync(path))Assert.Equal(HttpStatusCode.Unauthorized,revoked.StatusCode);
        using(var active=await merchant.GetAsync(path))Assert.Equal(HttpStatusCode.OK,active.StatusCode);
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
