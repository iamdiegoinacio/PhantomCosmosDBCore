#nullable enable
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Azure.Cosmos;
using NSubstitute;

namespace CosmosDB.InMemoryEmulator.Infrastructure.SdkInterop;

/// <summary>
/// Constructs <see cref="ChangeFeedProcessorBuilder"/> instances wired to an in-memory
/// <see cref="ChangeFeedProcessor"/> using SDK reflection, with automatic fallback to an
/// <see cref="NSubstitute"/> proxy when internal SDK fields change.
/// </summary>
internal static class ChangeFeedProcessorBuilderFactory
{
    private static readonly Assembly CosmosAssembly = typeof(Container).Assembly;
    private static readonly BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    internal static readonly string[] RequiredFieldNames =
    [
        "changeFeedProcessor",
        "isBuilt",
        "changeFeedLeaseOptions",
        "changeFeedProcessorOptions",
        "monitoredContainer",
        "applyBuilderConfiguration"
    ];

    internal static readonly string[] RequiredInternalTypes =
    [
        "Microsoft.Azure.Cosmos.ChangeFeed.Configuration.ChangeFeedLeaseOptions",
        "Microsoft.Azure.Cosmos.ChangeFeed.Configuration.ChangeFeedProcessorOptions",
        "Microsoft.Azure.Cosmos.ContainerInlineCore",
        "Microsoft.Azure.Cosmos.ContainerInternal",
        "Microsoft.Azure.Cosmos.ChangeFeed.LeaseManagement.DocumentServiceLeaseStoreManager"
    ];

    internal static bool IsReflectionCompatible()
    {
        var builderType = typeof(ChangeFeedProcessorBuilder);

        foreach (var fieldName in RequiredFieldNames)
        {
            if (builderType.GetField(fieldName, PrivateInstance) is null)
            {
                return false;
            }
        }

        foreach (var typeName in RequiredInternalTypes)
        {
            if (CosmosAssembly.GetType(typeName) is null)
            {
                return false;
            }
        }

        return true;
    }

    internal static ChangeFeedProcessorBuilder Create(string processorName, ChangeFeedProcessor processor)
    {
        try
        {
            return CreateViaReflection(processorName, processor);
        }
        catch (Exception exception)
        {
            Trace.TraceWarning(
                "InMemoryContainer: ChangeFeedProcessorBuilder reflection failed " +
                $"({exception.GetType().Name}: {exception.Message}). " +
                "Falling back to NSubstitute stub — processor.Build() will return a " +
                "no-op processor that does not poll the change feed.");
            return CreateFallbackBuilder(processor);
        }
    }

    private static ChangeFeedProcessorBuilder CreateViaReflection(
        string processorName, ChangeFeedProcessor processor)
    {
        var builder = (ChangeFeedProcessorBuilder)RuntimeHelpers.GetUninitializedObject(
            typeof(ChangeFeedProcessorBuilder));

        var builderType = typeof(ChangeFeedProcessorBuilder);

        SetField(builderType, builder, "changeFeedProcessor", processor);
        SetField(builderType, builder, "isBuilt", false);

        var leaseOptionsType = GetInternalType(
            "Microsoft.Azure.Cosmos.ChangeFeed.Configuration.ChangeFeedLeaseOptions");
        var leaseOptions = Activator.CreateInstance(leaseOptionsType);
        leaseOptionsType.GetProperty("LeasePrefix")!.SetValue(leaseOptions, processorName);
        SetField(builderType, builder, "changeFeedLeaseOptions", leaseOptions);

        var processorOptionsType = GetInternalType(
            "Microsoft.Azure.Cosmos.ChangeFeed.Configuration.ChangeFeedProcessorOptions");
        var processorOptions = Activator.CreateInstance(processorOptionsType, nonPublic: true);
        SetField(builderType, builder, "changeFeedProcessorOptions", processorOptions);

        var containerInlineCore = GetInternalType("Microsoft.Azure.Cosmos.ContainerInlineCore");
        var fakeContainer = RuntimeHelpers.GetUninitializedObject(containerInlineCore);
        SetField(builderType, builder, "monitoredContainer", fakeContainer);

        var leaseStoreManagerType = GetInternalType(
            "Microsoft.Azure.Cosmos.ChangeFeed.LeaseManagement.DocumentServiceLeaseStoreManager");
        var containerInternalType = GetInternalType("Microsoft.Azure.Cosmos.ContainerInternal");

        var actionType = typeof(Action<,,,,,>).MakeGenericType(
            leaseStoreManagerType,
            containerInternalType,
            typeof(string),
            leaseOptionsType,
            processorOptionsType,
            containerInternalType);

        var parameters = actionType.GetMethod("Invoke")!.GetParameters()
            .Select(p => Expression.Parameter(p.ParameterType))
            .ToArray();
        var noopDelegate = Expression.Lambda(actionType, Expression.Empty(), parameters).Compile();
        SetField(builderType, builder, "applyBuilderConfiguration", noopDelegate);

        return builder;
    }

    private static ChangeFeedProcessorBuilder CreateFallbackBuilder(ChangeFeedProcessor processor)
    {
        var builder = Substitute.For<ChangeFeedProcessorBuilder>();
        builder.WithInstanceName(Arg.Any<string>()).Returns(builder);
        builder.WithLeaseContainer(Arg.Any<Container>()).Returns(builder);
        builder.WithStartTime(Arg.Any<DateTime>()).Returns(builder);
        builder.WithPollInterval(Arg.Any<TimeSpan>()).Returns(builder);
        builder.WithMaxItems(Arg.Any<int>()).Returns(builder);
        builder.Build().Returns(processor);
        return builder;
    }

    private static void SetField(Type type, object target, string fieldName, object? value)
    {
        var field = type.GetField(fieldName, PrivateInstance)
            ?? throw new MissingFieldException(type.Name, fieldName);
        field.SetValue(target, value);
    }

    private static Type GetInternalType(string typeName)
    {
        return CosmosAssembly.GetType(typeName)
            ?? throw new TypeLoadException(
                $"Internal type '{typeName}' not found in {CosmosAssembly.GetName().Name} " +
                $"v{CosmosAssembly.GetName().Version}.");
    }
}
