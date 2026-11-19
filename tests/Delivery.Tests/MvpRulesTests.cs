using System.Security.Cryptography;
using System.Text;
using Delivery.BuildingBlocks;
using Delivery.Contracts;
using Delivery.Modules.Ledger;
using Delivery.Modules.Orders;
using Delivery.Modules.Payments;
using Delivery.Modules.Pricing;

namespace Delivery.Tests;

public sealed class MvpRulesTests
{
    [Theory]
    [InlineData(101, 5000, 51)]
    [InlineData(999, 1500, 150)]
    [InlineData(1000, 0, 0)]
    public void Commission_rounds_in_minor_units(long amount, int basisPoints, long expected)
        => Assert.Equal(expected, MoneyMath.BasisPoints(amount, basisPoints));

    [Fact]
    public void Discounts_cannot_make_a_negative_total()
        => Assert.Throws<ArgumentOutOfRangeException>(() => MoneyMath.Total(100, 101, 0, 0, 0, 0));

    [Fact]
    public void Totals_detect_overflow()
        => Assert.Throws<OverflowException>(() => MoneyMath.Total(long.MaxValue, 0, 1, 0, 0, 0));

    [Theory]
    [InlineData("picked_up", "cancelled", false)]
    [InlineData("closed", "confirmed", false)]
    [InlineData("confirmed", "accepted", true)]
    [InlineData("ready", "delivered", false)]
    [InlineData("in_transit", "delivered", true)]
    public void Orders_enforce_pickup_and_terminal_boundaries(string from, string to, bool allowed)
        => Assert.Equal(allowed, OrderStates.Allowed(from, to));

    [Fact]
    public void Mandatory_variant_is_checked_before_pricing()
    {
        var product = Product();
        Assert.Throws<ApiException>(() => PricingService.Price([new(product.Id, 1, [])], [product]));
        var line = Assert.Single(PricingService.Price([new(product.Id, 2, [product.Modifiers[0].Id])], [product]));
        Assert.Equal(1250, line.UnitPrice);
        Assert.Equal(2, line.Quantity);
    }

    [Fact]
    public void Unknown_modifiers_and_insufficient_stock_are_rejected()
    {
        var product = Product();
        Assert.Throws<ApiException>(() => PricingService.Price([new(product.Id, 1, [Guid.NewGuid()])], [product]));
        Assert.Throws<ApiException>(() => PricingService.Price([new(product.Id, 11, [product.Modifiers[0].Id])], [product]));
    }

    [Theory]
    [InlineData(false, 8500)]
    [InlineData(true, 7650)]
    public void Settlement_balances_discount_funding_and_tip(bool merchantFunded, long merchantAmount)
    {
        var quote = new QuoteSnapshot(Guid.NewGuid(), Guid.NewGuid(), new(Guid.NewGuid(), Guid.NewGuid(), "Branch", new(0,0), "PEN", 0, true, 20),
            new(Guid.NewGuid(), "Home", "Street", "", new(0,0)), [], "PEN", 10000, 1000, 1000, 200, 0, 300, 10500, 1500, 800, null, DateTimeOffset.UtcNow, merchantFunded);
        var result = LedgerService.Distribute(quote);
        Assert.Equal(merchantAmount, result.Merchant);
        Assert.Equal(1100, result.Courier);
        Assert.Equal(quote.Total, result.Merchant + result.Courier + result.Platform);
    }

    [Fact]
    public void Payment_webhook_binds_identifier_request_and_timestamp()
    {
        var now = DateTimeOffset.UtcNow;
        var timestamp = now.ToUnixTimeMilliseconds();
        var manifest = $"id:123;request-id:request;ts:{timestamp};";
        var digest = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("secret"), Encoding.UTF8.GetBytes(manifest)));
        var signature = $"ts={timestamp},v1={digest}";
        Assert.True(WebhookSignature.Valid("secret", "123", "request", signature, now));
        Assert.False(WebhookSignature.Valid("secret", "124", "request", signature, now));
        Assert.False(WebhookSignature.Valid("secret", "123", "other", signature, now));
        Assert.False(WebhookSignature.Valid("secret", "123", "request", signature, now.AddMinutes(11)));
        Assert.False(WebhookSignature.Valid("", "123", "request", signature, now));
    }

    private static ProductSnapshot Product() => new(Guid.NewGuid(), Guid.NewGuid(), "Pizza", "Pizza", "", null, [], 1000, 10, true,
        [new(Guid.NewGuid(), "Mediana", 250, "size", 1, 1)]);

    [Fact]
    public void Multiple_variants_share_the_same_product_stock()
    {
        var product=Product();
        var second=new ModifierSnapshot(Guid.NewGuid(),"Grande",500,"size",1,1);
        product=product with {Modifiers=[product.Modifiers[0],second]};
        Assert.Equal(2,PricingService.Price([new(product.Id,2,[product.Modifiers[0].Id]),new(product.Id,3,[second.Id])],[product]).Length);
        Assert.Throws<ApiException>(()=>PricingService.Price([new(product.Id,6,[product.Modifiers[0].Id]),new(product.Id,6,[second.Id])],[product]));
    }
}
