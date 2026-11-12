using Allowlist.Core;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Allowlist.Api.Auth;

public sealed class ApiExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var context = httpContext;
        context.Response.StatusCode = exception is DomainException domain ? domain.Status : 500;
        await problems.WriteAsync(new() { HttpContext = context, Exception = exception, ProblemDetails = new ProblemDetails
        {
            Status = context.Response.StatusCode, Title = "Request failed",
            Detail = exception is DomainException ? exception.Message : "An unexpected error occurred.",
            Extensions = { ["code"] = exception is DomainException known ? known.Code : "internal_error" }
        } });
        return true;
    }
}
