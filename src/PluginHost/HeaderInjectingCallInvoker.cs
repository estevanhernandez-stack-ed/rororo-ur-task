using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Labs626.UrTask.PluginHost;

/// <summary>
/// gRPC CallInvoker wrapper that injects the <c>x-plugin-id</c> header on every
/// outbound call. RoRoRo's CapabilityInterceptor reads this header to identify
/// which plugin is calling — without it, every gated RPC throws FailedPrecondition.
///
/// Wrapped once at channel construction so the rest of the plugin code never has
/// to think about headers; <c>RoRoRoHostClient</c> built on this invoker auto-tags
/// every call.
/// </summary>
internal sealed class HeaderInjectingCallInvoker : CallInvoker
{
    private const string HeaderName = "x-plugin-id";

    private readonly CallInvoker _inner;
    private readonly string _pluginId;

    public HeaderInjectingCallInvoker(CallInvoker inner, string pluginId)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _pluginId = pluginId ?? throw new ArgumentNullException(nameof(pluginId));
    }

    private CallOptions WithHeader(CallOptions options)
    {
        var headers = options.Headers ?? new Metadata();
        if (headers.Get(HeaderName) is null)
        {
            headers.Add(HeaderName, _pluginId);
        }
        return options.WithHeaders(headers);
    }

    public override TResponse BlockingUnaryCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host,
        CallOptions options, TRequest request)
        => _inner.BlockingUnaryCall(method, host, WithHeader(options), request);

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host,
        CallOptions options, TRequest request)
        => _inner.AsyncUnaryCall(method, host, WithHeader(options), request);

    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host,
        CallOptions options, TRequest request)
        => _inner.AsyncServerStreamingCall(method, host, WithHeader(options), request);

    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host,
        CallOptions options)
        => _inner.AsyncClientStreamingCall(method, host, WithHeader(options));

    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host,
        CallOptions options)
        => _inner.AsyncDuplexStreamingCall(method, host, WithHeader(options));
}
