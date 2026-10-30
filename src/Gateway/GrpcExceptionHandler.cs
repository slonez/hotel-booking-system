using Grpc.Core;
using Microsoft.AspNetCore.Diagnostics;

namespace Gateway;

// Translates errors from backend gRPC calls into HTTP ProblemDetails responses.
public class GrpcExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        if (exception is not RpcException rpc)
            return false;

        var (status, detail) = rpc.StatusCode switch
        {
            // Backend messages for client errors are safe to show to the user.
            StatusCode.InvalidArgument => (StatusCodes.Status400BadRequest, rpc.Status.Detail),
            StatusCode.NotFound => (StatusCodes.Status404NotFound, rpc.Status.Detail),
            StatusCode.FailedPrecondition => (StatusCodes.Status409Conflict, rpc.Status.Detail),
            StatusCode.Unavailable => (StatusCodes.Status503ServiceUnavailable, "Service is temporarily unavailable."),
            _ => (StatusCodes.Status500InternalServerError, null)
        };

        context.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = exception,
            ProblemDetails = { Detail = detail }
        });
    }
}
