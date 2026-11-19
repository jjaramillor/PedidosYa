using Delivery.BuildingBlocks;
using Microsoft.AspNetCore.Diagnostics;
namespace Delivery.Api;

public sealed class ApiExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var error = exception as ApiException;
        context.Response.StatusCode = error?.StatusCode ?? (exception is BadHttpRequestException bad ? bad.StatusCode : 500);
        var details = new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = context.Response.StatusCode,
            Title = error?.Message ?? (context.Response.StatusCode < 500 ? "Solicitud inválida." : "Error interno del servidor.")
        };
        details.Extensions["code"] = error?.Code ?? "request_failed";
        if (error?.Errors is not null) details.Extensions["errors"] = error.Errors;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = details
        });
    }
}
