using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Gateway.Tests;

public class DeadlineInterceptorTests
{
    private static readonly Method<string, string> TestMethod = new(
        MethodType.Unary, "test", "Call",
        Marshallers.Create(s => [], _ => ""), Marshallers.Create(s => [], _ => ""));

    [Fact]
    public void Sets_default_deadline_when_none_is_given()
    {
        var deadline = CallWithOptions(new CallOptions());

        Assert.InRange(deadline!.Value - DateTime.UtcNow, TimeSpan.FromSeconds(4), DeadlineInterceptor.Timeout);
    }

    [Fact]
    public void Keeps_deadline_set_by_the_caller()
    {
        var callerDeadline = DateTime.UtcNow.AddSeconds(30);

        var deadline = CallWithOptions(new CallOptions(deadline: callerDeadline));

        Assert.Equal(callerDeadline, deadline);
    }

    // Returns the deadline the interceptor passed on to the next step.
    private static DateTime? CallWithOptions(CallOptions options)
    {
        DateTime? deadline = null;
        new DeadlineInterceptor().AsyncUnaryCall(
            "request",
            new ClientInterceptorContext<string, string>(TestMethod, null, options),
            (_, context) =>
            {
                deadline = context.Options.Deadline;
                return null!;
            });
        return deadline;
    }
}
