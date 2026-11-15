#nullable enable
using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Scripts;
using Newtonsoft.Json.Linq;
using static CosmosDB.InMemoryEmulator.Domain.Primitives.ContainerConstants;

namespace CosmosDB.InMemoryEmulator.Application.Scripts;

/// <summary>
/// Executes pre-triggers and post-triggers (both C# delegates and JavaScript bodies)
/// with partition key and document ID immutability enforcement.
/// </summary>
internal static class TriggerExecutionService
{
    public static JObject ExecutePreTriggers(
        ItemRequestOptions? requestOptions,
        JObject jObj,
        string operationName,
        ScriptCatalog catalog,
        IReadOnlyList<string> partitionKeyPaths,
        IJsTriggerEngine? jsTriggerEngine,
        Func<JObject, PartitionScopedCollectionContext> createCollectionContext)
    {
        var triggerNames = requestOptions?.PreTriggers;
        if (triggerNames is null) return jObj;

        var currentOp = OperationNameToTriggerOp(operationName);

        foreach (var name in triggerNames)
        {
            // Priority 1: C# handler
            if (catalog.Triggers.TryGetValue(name, out var trigger))
            {
                if (trigger.TriggerType != TriggerType.Pre || trigger.PreHandler is null) continue;
                if (!TriggerOperationMatches(trigger.TriggerOperation, currentOp)) continue;

                try
                {
                    jObj = trigger.PreHandler(jObj);
                }
                catch (CosmosException) { throw; }
                catch (Exception ex)
                {
                    throw InMemoryCosmosException.Create(
                        $"Pre-trigger '{name}' failed: {ex.Message}",
                        HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
                }
                break; // Real Cosmos only fires the first matching trigger
            }

            // Priority 2: JS body via JsTriggerEngine
            if (catalog.TriggerProperties.TryGetValue(name, out var props) && props.Body is not null)
            {
                if (props.TriggerType != TriggerType.Pre) continue;
                if (!TriggerOperationMatches(props.TriggerOperation, currentOp)) continue;

                if (jsTriggerEngine is null)
                {
                    throw InMemoryCosmosException.Create(
                        $"Trigger '{name}' has a JavaScript body but no JS trigger engine is configured. " +
                        "Install the CosmosDB.InMemoryEmulator.JsTriggers package and call container.UseJsTriggers().",
                        HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
                }

                var originalId = jObj["id"]?.ToString();
                var pkPath = partitionKeyPaths[0].TrimStart('/');
                var originalPk = jObj.SelectToken(pkPath)?.ToString();

                jObj = jsTriggerEngine.ExecutePreTrigger(props.Body, jObj, createCollectionContext(jObj));

                // Validate the trigger did not change id or partition key
                var newId = jObj["id"]?.ToString();
                var newPk = jObj.SelectToken(pkPath)?.ToString();
                if (!string.Equals(originalId, newId, StringComparison.Ordinal))
                {
                    throw InMemoryCosmosException.Create(
                        $"Pre-trigger '{name}' is not allowed to modify the document id.",
                        HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
                }
                if (!string.Equals(originalPk, newPk, StringComparison.Ordinal))
                {
                    throw InMemoryCosmosException.Create(
                        $"Pre-trigger '{name}' is not allowed to modify the partition key.",
                        HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
                }

                break; // Real Cosmos only fires the first matching trigger
            }

            throw InMemoryCosmosException.Create(
                $"Trigger '{name}' is not registered. Register it via RegisterTrigger() or CreateTriggerAsync() before referencing it in PreTriggers.",
                HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
        }

        return jObj;
    }

    public static JObject? ExecutePostTriggers(
        ItemRequestOptions? requestOptions,
        JObject committedDoc,
        string operationName,
        ScriptCatalog catalog,
        IJsTriggerEngine? jsTriggerEngine,
        Func<JObject, PartitionScopedCollectionContext> createCollectionContext)
    {
        var triggerNames = requestOptions?.PostTriggers;
        if (triggerNames is null) return null;

        JObject? responseBodyOverride = null;
        var currentOp = OperationNameToTriggerOp(operationName);

        foreach (var name in triggerNames)
        {
            // Priority 1: C# handler
            if (catalog.Triggers.TryGetValue(name, out var trigger))
            {
                if (trigger.TriggerType != TriggerType.Post || trigger.PostHandler is null) continue;
                if (!TriggerOperationMatches(trigger.TriggerOperation, currentOp)) continue;

                try
                {
                    trigger.PostHandler(committedDoc);
                }
                catch (CosmosException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw InMemoryCosmosException.Create(
                        $"Post-trigger '{name}' failed: {ex.Message}",
                        HttpStatusCode.InternalServerError, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
                }
                break; // Real Cosmos only fires the first matching trigger
            }

            // Priority 2: JS body via JsTriggerEngine
            if (catalog.TriggerProperties.TryGetValue(name, out var props) && props.Body is not null)
            {
                if (props.TriggerType != TriggerType.Post) continue;
                if (!TriggerOperationMatches(props.TriggerOperation, currentOp)) continue;

                if (jsTriggerEngine is null)
                {
                    throw InMemoryCosmosException.Create(
                        $"Trigger '{name}' has a JavaScript body but no JS trigger engine is configured. " +
                        "Install the CosmosDB.InMemoryEmulator.JsTriggers package and call container.UseJsTriggers().",
                        HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
                }

                try
                {
                    var setBodyResult = jsTriggerEngine.ExecutePostTrigger(
                        props.Body, committedDoc, createCollectionContext(committedDoc));
                    if (setBodyResult is not null)
                    {
                        responseBodyOverride = setBodyResult;
                    }
                }
                catch (CosmosException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw InMemoryCosmosException.Create(
                        $"Post-trigger '{name}' failed: {ex.Message}",
                        HttpStatusCode.InternalServerError, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
                }
                break; // Real Cosmos only fires the first matching trigger
            }

            throw InMemoryCosmosException.Create(
                $"Trigger '{name}' is not registered. Register it via RegisterTrigger() or CreateTriggerAsync() before referencing it in PostTriggers.",
                HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
        }

        return responseBodyOverride;
    }

    private static TriggerOperation OperationNameToTriggerOp(string operationName) => operationName switch
    {
        "Create" => TriggerOperation.Create,
        "Replace" => TriggerOperation.Replace,
        "Upsert" => TriggerOperation.Upsert,
        "Delete" => TriggerOperation.Delete,
        _ => TriggerOperation.All
    };

    private static bool TriggerOperationMatches(TriggerOperation registered, TriggerOperation current)
        => registered == TriggerOperation.All || registered == current;
}
