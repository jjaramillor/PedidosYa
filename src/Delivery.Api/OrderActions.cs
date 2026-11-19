using Delivery.BuildingBlocks;
using Delivery.Contracts;
using Delivery.Modules.Orders;
using Npgsql;

namespace Delivery.Api;
public sealed class OrderActions(NpgsqlDataSource source,OrderService orders,IDeliveryDirectory deliveries)
{
    public async Task ExecuteAsync(Caller caller,Guid order,OrderAction action,CancellationToken ct)
    {
        if(action.State!="cancelled"){await orders.ActionAsync(caller,order,action,ct);return;}
        if((await orders.GetAsync(order,ct)).State=="cancelled"){await orders.ActionAsync(caller,order,action,ct);return;}
        // Delivery pickup and reassignment take this same lock. The order projection can lag behind pickup.
        await using var gate=await ModuleSql.SessionGateAsync(source,"fulfillment:"+order,ct);
        Guard.Require(await deliveries.CanReassignAsync(order,ct),"pickup_already_confirmed","El retiro ya fue confirmado; registra una incidencia para continuar.",409);
        await orders.ActionAsync(caller,order,action,ct);
    }
}
