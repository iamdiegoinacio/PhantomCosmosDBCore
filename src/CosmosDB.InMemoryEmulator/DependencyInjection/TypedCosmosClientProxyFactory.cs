#nullable enable
using System.Reflection;
using Castle.DynamicProxy;
using Microsoft.Azure.Cosmos;

namespace CosmosDB.InMemoryEmulator.DependencyInjection;

/// <summary>
/// Creates Castle.Core dynamic proxies for typed <see cref="CosmosClient"/> subclasses,
/// intercepting <see cref="CosmosClient.GetContainer"/> to wrap returned containers in
/// <see cref="PartitionKeyCapturingContainer"/>.
/// </summary>
internal static class TypedCosmosClientProxyFactory
{
    public static TClient CreateProxy<TClient>(
        HttpMessageHandler finalHandler,
        IReadOnlyDictionary<string, FakeCosmosHandler> handlers)
        where TClient : CosmosClient
    {
        var cosmosClientOptions = new CosmosClientOptions
        {
            ConnectionMode = ConnectionMode.Gateway,
            HttpClientFactory = () => new HttpClient(finalHandler)
        };

        try
        {
            var generator = new ProxyGenerator();
            var interceptor = new GetContainerInterceptor(handlers);
            var proxyOptions = new ProxyGenerationOptions(new GetContainerOnlyHook());
            return (TClient)generator.CreateClassProxy(
                typeof(TClient),
                Type.EmptyTypes,
                proxyOptions,
                [FakeCosmosHandler.FakeConnectionString, cosmosClientOptions],
                interceptor);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new InvalidOperationException(
                $"UseInMemoryCosmosDB<{typeof(TClient).Name}>() failed to create a dynamic proxy. " +
                $"Ensure that '{typeof(TClient).Name}' is not sealed and has a public constructor " +
                $"accepting (string connectionString, CosmosClientOptions options). " +
                $"See inner exception for details.", ex);
        }
    }

    private sealed class GetContainerInterceptor(
        IReadOnlyDictionary<string, FakeCosmosHandler> handlers) : IInterceptor
    {
        public void Intercept(IInvocation invocation)
        {
            invocation.Proceed();
            if (invocation.Method.Name == nameof(CosmosClient.GetContainer)
                && invocation.ReturnValue is Container container)
            {
                var databaseId = (string)invocation.Arguments[0];
                var containerId = (string)invocation.Arguments[1];
                if (handlers.TryGetValue(containerId, out var handler) ||
                    handlers.TryGetValue($"{databaseId}/{containerId}", out handler))
                {
                    invocation.ReturnValue = new PartitionKeyCapturingContainer(container, handler);
                }
            }
        }
    }

    private sealed class GetContainerOnlyHook : IProxyGenerationHook
    {
        public bool ShouldInterceptMethod(Type type, MethodInfo methodInfo)
            => methodInfo.Name == nameof(CosmosClient.GetContainer);

        public void NonProxyableMemberNotification(Type type, MemberInfo memberInfo) { }
        public void MethodsInspected() { }
    }
}
