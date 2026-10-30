using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Gateway;

// gRPC calls have no deadline by default, so a hung backend would block requests forever.
public class DeadlineInterceptor : Interceptor
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        if (context.Options.Deadline is null)
        {
            context = new ClientInterceptorContext<TRequest, TResponse>(
                context.Method, context.Host, context.Options.WithDeadline(DateTime.UtcNow.Add(Timeout)));
        }

        return continuation(request, context);
    }
}
