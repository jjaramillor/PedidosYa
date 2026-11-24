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
    private async Task<DeliveryScenario> PrepareDelivery(string method,bool arrive=true,bool assign=true,bool? merchantFundsDiscount=null)
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
        if(merchantFundsDiscount is bool funded)
            await Service<PricingService>().SaveCouponAsync(admin,Guid.NewGuid(),new("FINANCEQA",branch,fixture.Clock.GetUtcNow().AddMinutes(-1),fixture.Clock.GetUtcNow().AddHours(1),0,2000,1000,10,1,funded,true,"coupon finance QA"),Ct);
        var quote=await Service<PricingService>().QuoteAsync(customerCaller.Id,address,branch,[new(product,1,[])],merchantFundsDiscount is null?null:"FINANCEQA",200,Ct);
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
        if(!assign)return new(admin,customer,owner,courier,courierSession,quote,order){OwnerSession=ownerSession};
        await Service<DispatchService>().DecideOfferAsync(courier,offer.Id,true,Ct);await Drain();
        await Service<OrderService>().ActionAsync(owner,order.Id,new(Guid.NewGuid(),"preparing","prepare"),Ct);
        await Service<OrderService>().ActionAsync(owner,order.Id,new(Guid.NewGuid(),"ready","ready"),Ct);await Drain();
        if(arrive)await Service<DeliveryService>().ActionAsync(courier,order.Id,new(Guid.NewGuid(),"arrived",null,null,"arrived"),Ct);
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
        Assert.Null((await Service<DeliveryService>().GetAsync(owner,order.Id,Ct)).PickupPin);
        Assert.Null((await Service<DeliveryService>().GetAsync(customerCaller,order.Id,Ct)).DeliveryPin);
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
        var terminalPins=await Service<DeliveryService>().GetAsync(scenario.Admin,order,Ct);
        Assert.Null(terminalPins.PickupPin);Assert.Null(terminalPins.DeliveryPin);
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
    public async Task SignalR_transport_isolates_orders_and_removes_logged_out_subscriptions()
    {
        var scenario=await PrepareDelivery("card");var outsider=await Register("live-outsider@mvp.test");
        async Task<SignalRProbe> Connect(AuthSession session)
        {
            var client=fixture.Factory.Server.CreateWebSocketClient();
            client.ConfigureRequest=request=>request.Headers["Authorization"]="Bearer "+session.AccessToken;
            var socket=await client.ConnectAsync(new Uri("ws://localhost/hubs/orders"),Ct);
            var probe=new SignalRProbe(socket);await probe.Send(new{protocol="json",version=1});
            Assert.False((await probe.Read()).TryGetProperty("error",out _));return probe;
        }
        using var customer=await Connect(scenario.Customer);using var merchant=await Connect(scenario.OwnerSession);using var stranger=await Connect(outsider);
        var order=scenario.Order.Id;
        Assert.False((await customer.Invoke("Subscribe",order)).TryGetProperty("error",out _));
        Assert.False((await merchant.Invoke("Subscribe",order)).TryGetProperty("error",out _));
        Assert.True((await stranger.Invoke("Subscribe",order)).TryGetProperty("error",out _));
        var live=Service<Delivery.Api.LiveConnections>();Assert.Equal(2,live.Subscriptions.Count);
        var realtime=Service<IOrderRealtime>();
        await realtime.PublishAsync(Guid.NewGuid(),new{State="unrelated"},Ct);
        await realtime.PublishAsync(order,new{State="ready"},Ct);
        foreach(var probe in new[]{customer,merchant})
        {
            var message=await probe.Read();Assert.Equal("orderChanged",message.GetProperty("target").GetString());
            var data=message.GetProperty("arguments")[0];Assert.Equal(order,data.GetProperty("orderId").GetGuid());
            Assert.Equal("ready",data.GetProperty("update").GetProperty("state").GetString());
        }
        // A protocol completion acts as a barrier: any leaked update would arrive first.
        Assert.True((await stranger.Invoke("Subscribe",order)).TryGetProperty("error",out _));
        Assert.False((await merchant.Invoke("Unsubscribe",order)).TryGetProperty("error",out _));
        Assert.Single(live.Subscriptions);
        using var http=fixture.Factory.CreateClient();http.DefaultRequestHeaders.Authorization=new("Bearer",scenario.Customer.AccessToken);
        using var logout=await http.PostAsJsonAsync("/api/v1/identity/logout",new{});Assert.True(logout.IsSuccessStatusCode);
        await realtime.PublishAsync(order,new{State="must-not-reach-revoked-session"},Ct);
        Assert.Empty(live.Subscriptions);
        Assert.False((await customer.Invoke("Unsubscribe",order)).TryGetProperty("error",out _));
        // No update may leak to an unsubscribed connection, even though its socket stays open.
        Assert.False((await merchant.Invoke("Subscribe",order)).TryGetProperty("error",out _));
        await realtime.PublishAsync(order,new{State="after-resubscribe"},Ct);
        Assert.Equal("after-resubscribe",(await merchant.Read()).GetProperty("arguments")[0].GetProperty("update").GetProperty("state").GetString());
    }

    private async Task<AuthSession> AdditionalCourier(DeliveryScenario scenario,string email)
    {
        var session=await Register(email);var courier=CallerFor(session);
        await Service<DispatchService>().ApplyAsync(courier,new("bicycle",[await Document(courier)]),Ct);
        await Service<DispatchService>().DecideAsync(scenario.Admin,courier.Id,new("approved","replacement verified"),Ct);
        var location=scenario.Quote.Branch.Location;
        var zone=await Service<ConfigurationService>().LocateAsync(location,Ct);
        await Service<DispatchService>().AvailabilityAsync(courier,new(true,zone.ZoneId,location),Ct);
        return session;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reassignment_revokes_former_courier_and_hides_historical_GPS(bool arrived)
    {
        var scenario=await PrepareDelivery("card",arrive:arrived);var order=scenario.Order.Id;
        var delivery=Service<DeliveryService>();var dispatch=Service<DispatchService>();
        var replacement=CallerFor(await AdditionalCourier(scenario,"replacement@mvp.test"));
        var client=fixture.Factory.Server.CreateWebSocketClient();
        client.ConfigureRequest=request=>request.Headers["Authorization"]="Bearer "+scenario.Customer.AccessToken;
        using var liveProbe=new SignalRProbe(await client.ConnectAsync(new Uri("ws://localhost/hubs/orders"),Ct));
        await liveProbe.Send(new{protocol="json",version=1});await liveProbe.Read();
        await liveProbe.Invoke("Subscribe",order);
        await delivery.UpdateLocationAsync(scenario.Courier,order,new(-12.045,-77.045),Ct);
        Assert.NotNull((await delivery.GetAsync(CallerFor(scenario.Customer),order,Ct)).LastLocation);
        await using(var db=await fixture.Source.OpenConnectionAsync())
        await using(var tx=await db.BeginTransactionAsync())
        {
            await Outbox.EnqueueAsync(db,tx,"deliveries.location-updated.v1",new{OrderId=order,Latitude=-12.045,Longitude=-77.045},"legacy-location",Ct);
            await tx.CommitAsync();
        }
        var denied=await Assert.ThrowsAsync<ApiException>(()=>dispatch.ReassignAsync(CallerFor(scenario.Customer),order,replacement.Id,"unauthorized",delivery,Ct));
        Assert.Equal(403,denied.StatusCode);
        var ineligible=await Assert.ThrowsAsync<ApiException>(()=>dispatch.ReassignAsync(scenario.Admin,order,Guid.NewGuid(),"unknown courier",delivery,Ct));
        Assert.Equal("courier_ineligible",ineligible.Code);
        Assert.Equal(scenario.Courier.Id,await dispatch.AssignedAsync(order,Ct));
        await dispatch.ReassignAsync(scenario.Admin,order,replacement.Id,"replacement before pickup",delivery,Ct);
        // Dispatch commits before the delivery consumer runs: old GPS must already be hidden.
        Assert.Null((await delivery.GetAsync(CallerFor(scenario.Customer),order,Ct)).LastLocation);
        await Drain();
        await liveProbe.Send(new{type=1,invocationId="barrier",target="Unsubscribe",arguments=new[]{order}});
        var reachedBarrier=false;var legacyRefresh=false;
        for(var i=0;i<20;i++)
        {
            var frame=await liveProbe.Read();
            if(frame.GetProperty("type").GetInt32()==3){Assert.Equal("barrier",frame.GetProperty("invocationId").GetString());reachedBarrier=true;break;}
            var update=frame.GetProperty("arguments")[0].GetProperty("update");
            if(update.GetProperty("kind").GetString()=="location")legacyRefresh=true;
            Assert.False(update.TryGetProperty("latitude",out _),"A queued location from the former courier leaked after reassignment.");
        }
        Assert.True(reachedBarrier);Assert.True(legacyRefresh);
        var assigned=await delivery.GetAsync(replacement,order,Ct);
        Assert.Equal(replacement.Id,assigned.CourierId);Assert.Equal("assigned",assigned.State);Assert.Null(assigned.LastLocation);
        var former=await Assert.ThrowsAsync<ApiException>(()=>delivery.GetAsync(scenario.Courier,order,Ct));Assert.Equal(403,former.StatusCode);
        await Assert.ThrowsAsync<ApiException>(()=>delivery.UpdateLocationAsync(scenario.Courier,order,new(-12.04,-77.04),Ct));
        await Assert.ThrowsAsync<ApiException>(()=>delivery.ActionAsync(scenario.Courier,order,new(Guid.NewGuid(),"arrived",null,null,"stale courier action"),Ct));
        Assert.DoesNotContain(order,await delivery.ActiveOrdersAsync(scenario.Courier.Id,Ct));
        await liveProbe.Invoke("Subscribe",order);
        var point=new Coordinate(-12.041,-77.041);await delivery.UpdateLocationAsync(replacement,order,point,Ct);await Drain();
        Assert.Equal(point,(await delivery.GetAsync(CallerFor(scenario.Customer),order,Ct)).LastLocation);
        var gps=(await liveProbe.Read()).GetProperty("arguments")[0].GetProperty("update");
        Assert.Equal(point.Latitude,gps.GetProperty("latitude").GetDouble());Assert.Equal(point.Longitude,gps.GetProperty("longitude").GetDouble());
        await liveProbe.Invoke("Unsubscribe",order);
        await delivery.ActionAsync(replacement,order,new(Guid.NewGuid(),"arrived",null,null,"replacement arrived"),Ct);
        var pickup=(await delivery.GetAsync(scenario.Owner,order,Ct)).PickupPin;
        await delivery.ActionAsync(replacement,order,new(Guid.NewGuid(),"picked_up",pickup,null,"replacement pickup"),Ct);await Drain();
        var late=await Assert.ThrowsAsync<ApiException>(()=>dispatch.ReassignAsync(scenario.Admin,order,scenario.Courier.Id,"too late",delivery,Ct));
        Assert.Equal("delivery_already_picked",late.Code);Assert.Equal(409,late.StatusCode);
        Assert.Equal(replacement.Id,await dispatch.AssignedAsync(order,Ct));
        Assert.Equal(1,await fixture.CountAsync("SELECT count(*) FROM platform.audit WHERE action='courier.reassigned'"));
    }

    [Fact]
    public async Task Rejected_offer_moves_to_another_candidate_without_duplicate_assignment()
    {
        var scenario=await PrepareDelivery("card",assign:false);var dispatch=Service<DispatchService>();
        var replacement=CallerFor(await AdditionalCourier(scenario,"replacement@mvp.test"));
        var original=Assert.Single(await dispatch.OffersAsync(scenario.Courier,Ct));
        await dispatch.DecideOfferAsync(scenario.Courier,original.Id,false,Ct);
        Assert.Null(await dispatch.AssignedAsync(scenario.Order.Id,Ct));
        await dispatch.OfferAsync(scenario.Order.Id,Ct);
        var next=Assert.Single(await dispatch.OffersAsync(replacement,Ct));Assert.Equal("pending",next.State);
        Assert.Equal("rejected",Assert.Single(await dispatch.OffersAsync(scenario.Courier,Ct)).State);
        await Task.WhenAll(dispatch.DecideOfferAsync(replacement,next.Id,true,Ct),dispatch.DecideOfferAsync(replacement,next.Id,true,Ct));await Drain();
        Assert.Equal(replacement.Id,await dispatch.AssignedAsync(scenario.Order.Id,Ct));
        Assert.Equal(replacement.Id,(await Service<DeliveryService>().GetAsync(replacement,scenario.Order.Id,Ct)).CourierId);
        Assert.Equal(1,await fixture.CountAsync("SELECT count(*) FROM dispatch.offers WHERE state='accepted'"));
        Assert.Equal(1,await fixture.CountAsync("SELECT count(*) FROM deliveries.deliveries"));
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
    private async Task<DeliveryScenario> CompleteCardDelivery()
    {
        var scenario = await PrepareDelivery("card");
        await StartRoute(scenario);
        var pin = (await Service<DeliveryService>().GetAsync(CallerFor(scenario.Customer), scenario.Order.Id, Ct)).DeliveryPin;
        await Service<DeliveryService>().ActionAsync(scenario.Courier, scenario.Order.Id,
            new(Guid.NewGuid(), "delivered", pin, null, "financial fixture delivery"), Ct);
        await Drain();
        return scenario;
    }

    [Fact]
    public async Task Captured_payment_refunds_are_idempotent_audited_and_fully_reverse_distribution()
    {
        var s = await CompleteCardDelivery();
        var payments = Service<PaymentService>();
        Assert.Equal(s.Quote.Total, (await payments.InfoAsync(s.Order.Id, Ct)).Captured);
        var partial = new RefundInput(Guid.NewGuid(), 100, "partial captured refund QA");
        await Task.WhenAll(payments.RefundAsync(s.Admin, s.Order.Id, partial, Ct), payments.RefundAsync(s.Admin, s.Order.Id, partial, Ct));
        await Drain();
        Assert.Equal(100, (await payments.InfoAsync(s.Order.Id, Ct)).Refunded);
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM payments.refunds"));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM platform.audit WHERE action='payment.refunded' AND reason='partial captured refund QA'"));
        var conflict = await Assert.ThrowsAsync<ApiException>(() => payments.RefundAsync(s.Admin, s.Order.Id, partial with { Amount = 101 }, Ct));
        Assert.Equal("refund_key_conflict", conflict.Code);
        await Service<LedgerService>().RefundAsync(s.Order, 100, partial.OperationId, Ct);
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM ledger.vouchers WHERE kind='refund'"));
        var remaining = new RefundInput(Guid.NewGuid(), s.Quote.Total - 100, "remaining captured refund QA");
        await payments.RefundAsync(s.Admin, s.Order.Id, remaining, Ct);
        await payments.RefundAsync(s.Admin, s.Order.Id, remaining, Ct);
        await Drain(); await Drain();
        var info = await payments.InfoAsync(s.Order.Id, Ct);
        Assert.Equal("refunded", info.State); Assert.Equal(info.Captured, info.Refunded);
        Assert.Equal(2, await fixture.CountAsync("SELECT count(*) FROM payments.refunds"));
        Assert.Equal(2, await fixture.CountAsync("SELECT count(*) FROM platform.audit WHERE action='payment.refunded'"));
        Assert.Equal(2, await fixture.CountAsync("SELECT count(*) FROM ledger.vouchers WHERE kind='refund'"));
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM (SELECT account FROM ledger.lines GROUP BY account HAVING sum(amount)<>0) nonzero"));
        var excess = await Assert.ThrowsAsync<ApiException>(() => payments.RefundAsync(s.Admin, s.Order.Id, new(Guid.NewGuid(), 1, "excess QA"), Ct));
        Assert.Equal("refund_exceeds_capture", excess.Code);
        Assert.Equal(2, await fixture.CountAsync("SELECT count(*) FROM payments.refunds"));
    }

    [Theory]
    [InlineData("match", true)]
    [InlineData("state", false)]
    [InlineData("amount", false)]
    [InlineData("captured", false)]
    [InlineData("refunded", false)]
    [InlineData("currency", false)]
    [InlineData("order", false)]
    [InlineData("provider", false)]
    [InlineData("sandbox", false)]
    public async Task Reconciliation_detects_discrepancies_and_repeated_checks_do_not_post_money(string difference, bool matches)
    {
        var s = await CompleteCardDelivery();
        var info = await Service<PaymentService>().InfoAsync(s.Order.Id, Ct);
        await using var query = fixture.Source.CreateCommand("SELECT provider_id FROM payments.payments WHERE order_id=@order");
        query.Parameters.AddWithValue("order", s.Order.Id);
        var providerId = (string)(await query.ExecuteScalarAsync())!;
        var remote = new GatewayPayment(providerId, info.State, null, info.Captured, info.Currency, info.Amount, s.Order.Id, info.Refunded);
        remote = difference switch {
            "state" => remote with { State = "authorized" }, "amount" => remote with { Amount = info.Amount + 1 },
            "captured" => remote with { Captured = info.Captured - 1 }, "refunded" => remote with { Refunded = 1 },
            "currency" => remote with { Currency = "USD" }, "order" => remote with { OrderId = Guid.NewGuid() },
            "provider" => remote with { Id = "different-provider-record" }, _ => remote
        };
        // Injected snapshot adapter: no real Mercado Pago or other financial request is made.
        var payments = new PaymentService(fixture.Source, new SnapshotGateway(remote), Service<IIdentityDirectory>(),
            Service<ICoverageDirectory>(), Microsoft.Extensions.Options.Options.Create(new PaymentOptions { Provider = difference == "sandbox" ? "sandbox" : "mercadopago" }));
        var vouchers = await fixture.CountAsync("SELECT count(*) FROM ledger.vouchers");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var results = await payments.ReconcileAsync(s.Admin, Ct);
            var result = System.Text.Json.JsonSerializer.SerializeToElement(Assert.Single(results));
            Assert.Equal(matches, result.GetProperty("Matches").GetBoolean());
            Assert.Equal(difference == "sandbox", result.GetProperty("Simulated").GetBoolean());
        }
        Assert.Equal(2, await fixture.CountAsync("SELECT count(*) FROM payments.reconciliations"));
        Assert.Equal(matches ? 2 : 0, await fixture.CountAsync("SELECT count(*) FROM payments.reconciliations WHERE matches"));
        if (difference == "sandbox") Assert.Equal(2, await fixture.CountAsync("SELECT count(*) FROM payments.reconciliations WHERE provider_state='simulated'"));
        Assert.Equal(vouchers, await fixture.CountAsync("SELECT count(*) FROM ledger.vouchers"));
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM payments.refunds"));
        Assert.Equal(info, await Service<PaymentService>().InfoAsync(s.Order.Id, Ct));
        var denied = await Assert.ThrowsAsync<ApiException>(() => payments.ReconcileAsync(CallerFor(s.Customer), Ct));
        Assert.Equal(403, denied.StatusCode);
    }

    [Theory]
    [InlineData("captured")]
    [InlineData("authorized")]
    public async Task Duplicate_or_stale_signed_webhooks_do_not_change_a_captured_payment_or_ledger(string remoteState)
    {
        var s = await CompleteCardDelivery();
        var info = await Service<PaymentService>().InfoAsync(s.Order.Id, Ct);
        await using var query = fixture.Source.CreateCommand("SELECT provider_id FROM payments.payments WHERE order_id=@order");
        query.Parameters.AddWithValue("order", s.Order.Id);
        var providerId = (string)(await query.ExecuteScalarAsync())!;
        const string secret = "automated-fixture-secret";
        const string requestId = "duplicate-payment-fixture";
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var manifest = $"id:{providerId.ToLowerInvariant()};request-id:{requestId};ts:{timestamp};";
        var digest = Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret), System.Text.Encoding.UTF8.GetBytes(manifest)));
        var signature = $"ts={timestamp},v1={digest}";
        var remote = new GatewayPayment(providerId, remoteState, null, remoteState == "captured" ? info.Captured : 0, info.Currency, info.Amount, s.Order.Id);
        var payments = new PaymentService(fixture.Source, new SnapshotGateway(remote), Service<IIdentityDirectory>(),
            Service<ICoverageDirectory>(), Microsoft.Extensions.Options.Options.Create(new PaymentOptions { Provider = "mercadopago", WebhookSecret = secret }));
        var vouchers = await fixture.CountAsync("SELECT count(*) FROM ledger.vouchers");
        var messages = await fixture.CountAsync("SELECT count(*) FROM platform.outbox");
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => payments.WebhookAsync(providerId, requestId, signature, Ct)));
        await Drain();
        Assert.Equal(info, await Service<PaymentService>().InfoAsync(s.Order.Id, Ct));
        Assert.Equal(vouchers, await fixture.CountAsync("SELECT count(*) FROM ledger.vouchers"));
        Assert.Equal(messages, await fixture.CountAsync("SELECT count(*) FROM platform.outbox"));
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM payments.refunds"));
    }

    private static System.Text.Json.JsonElement Json(object value)=>System.Text.Json.JsonSerializer.SerializeToElement(value);

    [Fact]
    public async Task Cash_collection_settlement_and_payment_reference_are_idempotent_and_audited()
    {
        var s=await PrepareDelivery("cash");await StartRoute(s);
        var pin=(await Service<DeliveryService>().GetAsync(CallerFor(s.Customer),s.Order.Id,Ct)).DeliveryPin;
        await Service<DeliveryService>().ActionAsync(s.Courier,s.Order.Id,new(Guid.NewGuid(),"delivered",pin,null,"cash delivered",true),Ct);await Drain();
        var ledger=Service<LedgerService>();
        var pending=Json(await ledger.StatementAsync(s.Admin,"courier",s.Courier.Id,Ct)).GetProperty("Pending").GetInt64();
        Assert.True(pending<0);
        var denied=await Assert.ThrowsAsync<ApiException>(()=>ledger.CashCollectionAsync(s.Courier,s.Courier.Id,Guid.NewGuid(),-pending,"QA","QA",Ct));Assert.Equal(403,denied.StatusCode);
        var excess=await Assert.ThrowsAsync<ApiException>(()=>ledger.CashCollectionAsync(s.Admin,s.Courier.Id,Guid.NewGuid(),-pending+1,"QA","QA",Ct));Assert.Equal("cash_collection_exceeds_debt",excess.Code);
        var operation=Guid.NewGuid();
        await Task.WhenAll(Enumerable.Range(0,2).Select(_=>ledger.CashCollectionAsync(s.Admin,s.Courier.Id,operation,-pending,"FICTICIO-CASH-01","cash collection QA",Ct)));
        Assert.Equal(0,Json(await ledger.StatementAsync(s.Admin,"courier",s.Courier.Id,Ct)).GetProperty("Pending").GetInt64());
        Assert.Equal(1,await fixture.CountAsync("SELECT count(*) FROM ledger.vouchers WHERE kind='cash-collection'"));
        Assert.Equal(1,await fixture.CountAsync("SELECT count(*) FROM platform.audit WHERE action='cash.collected'"));
        var settlement=Guid.NewGuid();var merchant=s.Quote.Branch.MerchantId;
        await Task.WhenAll(Enumerable.Range(0,2).Select(_=>ledger.CloseSettlementAsync(s.Admin,"merchant",merchant,settlement,"settlement QA",Ct)));
        await ledger.MarkPaidAsync(s.Admin,settlement,"FICTICIO-TRANSFER-01","already paid externally QA",Ct);
        await ledger.MarkPaidAsync(s.Admin,settlement,"FICTICIO-TRANSFER-02","retry must not overwrite",Ct);
        var statement=Json(await ledger.StatementAsync(s.Owner,"merchant",merchant,Ct));
        Assert.Equal(0,statement.GetProperty("Pending").GetInt64());
        var item=Assert.Single(statement.GetProperty("Settlements").EnumerateArray());
        Assert.Equal("FICTICIO-TRANSFER-01",item.GetProperty("ExternalReference").GetString());
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null,item.GetProperty("PaidAt").ValueKind);
        Assert.Equal(1,await fixture.CountAsync("SELECT count(*) FROM platform.audit WHERE action='settlement.closed'"));
        Assert.Equal(1,await fixture.CountAsync("SELECT count(*) FROM platform.audit WHERE action='settlement.paid'"));
        Assert.Equal(0,await fixture.CountAsync("SELECT count(*) FROM (SELECT voucher_id FROM ledger.lines GROUP BY voucher_id HAVING sum(amount)<>0) unbalanced"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Funded_coupon_distribution_and_adjustments_preserve_closed_settlements(bool merchantFunded)
    {
        var s=await PrepareDelivery("card",merchantFundsDiscount:merchantFunded);await StartRoute(s);
        var pin=(await Service<DeliveryService>().GetAsync(CallerFor(s.Customer),s.Order.Id,Ct)).DeliveryPin;
        await Service<DeliveryService>().ActionAsync(s.Courier,s.Order.Id,new(Guid.NewGuid(),"delivered",pin,null,"coupon delivered"),Ct);await Drain();
        Assert.Equal(500,s.Quote.Discount);Assert.Equal(merchantFunded,s.Quote.MerchantFundsDiscount);
        var distribution=LedgerService.Distribute(s.Quote);
        Assert.Equal(merchantFunded?1700:2125,distribution.Merchant);
        Assert.Equal(s.Quote.CourierPay+200,distribution.Courier);
        Assert.Equal(s.Quote.Total,distribution.Merchant+distribution.Courier+distribution.Platform);
        Assert.Equal(1,await fixture.CountAsync("SELECT count(*) FROM pricing.coupon_uses"));
        var ledger=Service<LedgerService>();var settlement=Guid.NewGuid();
        await ledger.CloseSettlementAsync(s.Admin,"courier",s.Courier.Id,settlement,"close before adjustment",Ct);
        await ledger.MarkPaidAsync(s.Admin,settlement,"FICTICIO-COUPON-01","external fixture reference",Ct);
        var before=Json(await ledger.StatementAsync(s.Courier,"courier",s.Courier.Id,Ct)).GetProperty("Settlements")[0].GetRawText();
        var incentive=Guid.NewGuid();await ledger.IncentiveAsync(s.Admin,s.Order.Id,s.Courier.Id,incentive,101,"incentive QA",Ct);
        await ledger.IncentiveAsync(s.Admin,s.Order.Id,s.Courier.Id,incentive,101,"retry QA",Ct);
        await Service<PaymentService>().RefundAsync(s.Admin,s.Order.Id,new(Guid.NewGuid(),s.Quote.Total,"full after settlement QA"),Ct);await Drain();
        var after=Json(await ledger.StatementAsync(s.Courier,"courier",s.Courier.Id,Ct));
        Assert.Equal(before,after.GetProperty("Settlements")[0].GetRawText());
        Assert.Equal(101-distribution.Courier,after.GetProperty("Pending").GetInt64());
        Assert.Equal(1,await fixture.CountAsync("SELECT count(*) FROM ledger.vouchers WHERE kind='incentive'"));
        Assert.Equal(0,await fixture.CountAsync("SELECT count(*) FROM (SELECT voucher_id FROM ledger.lines GROUP BY voucher_id HAVING sum(amount)<>0) unbalanced"));
    }

    [Theory]
    [InlineData("card")]
    [InlineData("cash")]
    public async Task Pending_offer_previews_show_pay_and_distances_without_customer_address(string method)
    {
        var s=await PrepareDelivery(method,assign:false);
        var offer=Assert.Single(await Service<DispatchService>().OffersAsync(s.Courier,Ct));
        Assert.NotNull(offer.Preview);Assert.Equal(s.Quote.Branch.Name,offer.Preview.RestaurantName);
        Assert.Equal(0,offer.Preview.PickupKilometers);Assert.True(offer.Preview.DeliveryKilometers>0);
        Assert.Equal(s.Quote.CourierPay+s.Quote.Tip,offer.Preview.EstimatedEarnings);
        Assert.Equal(method=="cash"?s.Quote.Total:0,offer.Preview.CashToCollect);
        var json=System.Text.Json.JsonSerializer.Serialize(offer);
        Assert.DoesNotContain(s.Quote.Address.Street,json);Assert.DoesNotContain("Latitude",json);Assert.DoesNotContain("DeliveryPin",json);
        Assert.Empty(await Service<DispatchService>().OffersAsync(CallerFor(s.Customer),Ct));
        await Service<DispatchService>().DecideOfferAsync(s.Courier,offer.Id,false,Ct);
        Assert.Null(Assert.Single(await Service<DispatchService>().OffersAsync(s.Courier,Ct)).Preview);
    }

    private sealed class SnapshotGateway(GatewayPayment snapshot) : IPaymentGateway
    {
        public Task<GatewayPayment> GetAsync(string id, CancellationToken ct) => Task.FromResult(snapshot);
        public Task<GatewayPayment> CreateAsync(Guid order, long amount, string currency, string email, CardToken token, CancellationToken ct) => throw new NotSupportedException();
        public Task CaptureAsync(string id, Guid operation, CancellationToken ct) => throw new NotSupportedException();
        public Task CancelAsync(string id, Guid operation, CancellationToken ct) => throw new NotSupportedException();
        public Task RefundAsync(string id, long amount, Guid operation, CancellationToken ct) => throw new NotSupportedException();
    }

}
