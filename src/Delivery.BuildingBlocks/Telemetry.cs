using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Delivery.BuildingBlocks;

public static class Telemetry
{
    public const string Name = "Delivery";
    public static readonly ActivitySource Activities = new(Name);
    public static readonly Meter Meter = new(Name);
    public static readonly Counter<long> OtpRequests = Meter.CreateCounter<long>("identity.otp.requests");
    public static readonly Counter<long> OtpVerifications = Meter.CreateCounter<long>("identity.otp.verifications");
    public static readonly Counter<long> MessagesProcessed = Meter.CreateCounter<long>("messaging.processed");
    public static readonly Counter<long> MessagesFailed = Meter.CreateCounter<long>("messaging.failed");
}

