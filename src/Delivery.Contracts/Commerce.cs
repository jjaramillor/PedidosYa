namespace Delivery.Contracts;
public sealed record Coordinate(double Latitude,double Longitude)
{
    public bool IsValid=>double.IsFinite(Latitude)&&double.IsFinite(Longitude)&&Latitude is >=-90 and <=90&&Longitude is >=-180 and <=180;
}
public sealed record AddressSnapshot(Guid Id,string Label,string Street,string Reference,Coordinate Location);
public sealed record CoverageSnapshot(Guid ZoneId,string Name,long DeliveryBase,long PerKilometer,long ServiceFee,int CommissionBasisPoints,long CourierBase,int AcceptanceSeconds,bool CashEnabled);
public sealed record BranchSnapshot(Guid Id,Guid MerchantId,string Name,Coordinate Location,string Currency,long MinimumOrder,bool Open,int PreparationMinutes);
public sealed record ModifierSnapshot(Guid Id,string Name,long Price,string Group="extras",int MinimumSelections=0,int MaximumSelections=99);
public sealed record ProductSnapshot(Guid Id,Guid BranchId,string Name,string Category,string Description,string? ImageUrl,string[] Allergens,long Price,int Stock,bool Available,ModifierSnapshot[] Modifiers);
public sealed record BasketLine(Guid ProductId,int Quantity,Guid[] ModifierIds);
public sealed record PricedLine(Guid ProductId,string Name,int Quantity,long UnitPrice,ModifierSnapshot[] Modifiers);
public sealed record QuoteSnapshot(Guid Id,Guid CustomerId,BranchSnapshot Branch,AddressSnapshot Address,PricedLine[] Lines,string Currency,long Subtotal,long Discount,long Delivery,long Service,long Tax,long Tip,long Total,int CommissionBasisPoints,long CourierPay,Guid? CouponId,DateTimeOffset ExpiresAt,bool MerchantFundsDiscount=false);
public interface ICustomerDirectory {Task<AddressSnapshot> AddressAsync(Guid customer,Guid address,CancellationToken ct);}
public interface IRatingDirectory {Task<double> AverageAsync(Guid branch,CancellationToken ct);}
public interface ICoverageDirectory
{
    Task<CoverageSnapshot> LocateAsync(Coordinate point,CancellationToken ct);
    Task<bool> FlagAsync(string name,CancellationToken ct);
}
public interface IMerchantDirectory
{
    Task<bool> OwnsBusinessAsync(Guid actor,Guid merchant,CancellationToken ct);
    Task<Guid[]> OperatorsAsync(Guid branch,CancellationToken ct);
    Task<BranchSnapshot> BranchAsync(Guid branch,CancellationToken ct);
    Task<ProductSnapshot[]> ProductsAsync(Guid branch,IEnumerable<Guid> products,CancellationToken ct);
    Task<bool> CanOperateAsync(Guid actor,Guid branch,CancellationToken ct);
    Task ReserveStockAsync(Guid operation,Guid branch,BasketLine[] lines,CancellationToken ct);
    Task ReleaseStockAsync(Guid operation,CancellationToken ct);
}
public interface IPricingDirectory
{
    Task<QuoteSnapshot> ReadQuoteAsync(Guid customer,Guid quote,CancellationToken ct);
    Task<QuoteSnapshot> QuoteAsync(Guid customer,Guid address,Guid branch,BasketLine[] lines,string? coupon,long tip,CancellationToken ct);
    Task<QuoteSnapshot> ConfirmAsync(Guid customer,Guid quote,Guid operation,CancellationToken ct);
    Task ReleaseCouponAsync(Guid operation,CancellationToken ct);
}
public sealed record OrderView(Guid Id,Guid CustomerId,Guid BranchId,string State,QuoteSnapshot Quote,DateTimeOffset CreatedAt,string Method="card");
public interface IOrderDirectory
{
    Task<OrderView> GetAsync(Guid order,CancellationToken ct);
    Task TransitionAsync(Guid order,string state,Guid actor,string reason,Guid operation,CancellationToken ct);
}
public sealed record PaymentResult(Guid Id,string State,string? ClientSecret);
public interface IPaymentDirectory
{
    Task<PaymentResult> CreateAsync(Guid order,Guid customer,long amount,string currency,string method,string? token,CancellationToken ct);
    Task CancelAsync(Guid order,Guid operation,CancellationToken ct);
    Task CaptureAsync(Guid order,Guid operation,CancellationToken ct);
}
public interface IDeliveryDirectory
{
 Task CreateAsync(Guid order,Guid courier,Guid operation,CancellationToken ct);
 Task<bool> CanReassignAsync(Guid order,CancellationToken ct);
 Task<bool> CanReadAsync(Guid actor,Guid order,CancellationToken ct);
}
public interface IDispatchDirectory
{
 Task<Guid?> AssignedAsync(Guid order,CancellationToken ct);
 Task CompleteAsync(Guid order,CancellationToken ct);
 Task CancelAsync(Guid order,CancellationToken ct);
}
public sealed record CourierCandidate(Guid Id,Coordinate Location,string Vehicle,int Capacity);
public interface ICourierDirectory
{
    Task<CourierCandidate[]> CandidatesAsync(Guid zone,CancellationToken ct);
    Task<bool> ApprovedAsync(Guid courier,CancellationToken ct);
}
public interface ILedgerDirectory {Task RecordDeliveryAsync(OrderView order,Guid courier,Guid operation,CancellationToken ct,bool cash=false);}
public interface IOrderRealtime {Task PublishAsync(Guid order,object update,CancellationToken ct);}
public interface IIdentityDirectory
{
    Task<bool> ActiveAsync(Guid user,CancellationToken ct);
    Task<string?> EmailAsync(Guid user,CancellationToken ct);
    Task<Guid?> ActiveByEmailAsync(string email,CancellationToken ct);
}
public static class Geo
{
    public static double Kilometers(Coordinate a,Coordinate b)
    {
        const double rad=Math.PI/180;var lat=(b.Latitude-a.Latitude)*rad;var lon=(b.Longitude-a.Longitude)*rad;
        var h=Math.Pow(Math.Sin(lat/2),2)+Math.Cos(a.Latitude*rad)*Math.Cos(b.Latitude*rad)*Math.Pow(Math.Sin(lon/2),2);
        return 6371*2*Math.Asin(Math.Min(1,Math.Sqrt(h)));
    }
}
public static class MoneyMath
{
    public static long BasisPoints(long amount,int points)=>checked((long)decimal.Round(amount*(decimal)points/10000,0,MidpointRounding.AwayFromZero));
    public static long Total(long subtotal,long discount,long delivery,long service,long tax,long tip)
    {
        if(new[]{subtotal,discount,delivery,service,tax,tip}.Any(n=>n<0)||discount>subtotal)throw new ArgumentOutOfRangeException(nameof(subtotal));
        return checked(subtotal-discount+delivery+service+tax+tip);
    }
}
