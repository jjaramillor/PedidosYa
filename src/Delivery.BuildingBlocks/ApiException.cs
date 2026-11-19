namespace Delivery.BuildingBlocks;

public sealed class ApiException(int statusCode, string code, string message, IReadOnlyDictionary<string, string[]>? errors = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public IReadOnlyDictionary<string, string[]>? Errors { get; } = errors;
}
