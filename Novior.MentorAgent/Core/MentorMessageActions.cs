using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;

namespace Novior.MentorAgent.Core;

/// <summary>
/// Story 1286 — a command or a query registered with <c>MentorOptions.Actions</c>: its type, the handler's interface
/// that runs it, and what it returns. <see cref="MethodDiscoveryInfo.Method"/> is the handler's <c>HandleAsync</c>, so
/// the rules that read the method, and <c>MentorActionInfo.Method</c>, see the handler.
/// </summary>
internal sealed class MentorMessageBinding
{
    private MentorMessageBinding(Type message, bool query, MethodInfo run, Type? result)
    {
        MessageType = message;
        IsQuery = query;
        Run = run;
        ResultType = result;
    }

    /// <summary>The command's or the query's type, whose public properties are the action's parameters.</summary>
    public Type MessageType { get; }

    /// <summary>A query, always read-only; otherwise a command, a write.</summary>
    public bool IsQuery { get; }

    /// <summary>The <c>HandleAsync</c> of the handler's interface: called on the interface, so an explicit implementation runs too.</summary>
    public MethodInfo Run { get; }

    /// <summary>What <c>HandleAsync</c> returns; <see langword="null"/> for a command without a result.</summary>
    public Type? ResultType { get; }

    /// <summary>The trace's origin: <see cref="MentorActionOrigins.Query"/> or <see cref="MentorActionOrigins.Command"/>.</summary>
    public string Origin => IsQuery ? MentorActionOrigins.Query : MentorActionOrigins.Command;

    /// <summary>
    /// The binding of <paramref name="message"/> to <paramref name="handler"/>, and in <paramref name="method"/> the
    /// handler's own <c>HandleAsync</c>; <see langword="null"/>, with <paramref name="why"/>, when the handler handles
    /// it in no way, or in more than one (rule <c>fluent.message-checked</c>).
    /// </summary>
    internal static MentorMessageBinding? For(Type message, Type handler, bool query, out MethodInfo? method, out string? why)
    {
        method = null;
        why = null;

        var handles = handler.GetInterfaces()
            .Where(i => i.IsGenericType && i.GetGenericArguments()[0] == message)
            .Where(i => query
                ? i.GetGenericTypeDefinition() == typeof(IMentorQueryHandler<,>)
                : i.GetGenericTypeDefinition() == typeof(IMentorCommandHandler<>)
                  || i.GetGenericTypeDefinition() == typeof(IMentorCommandHandler<,>))
            .ToList();

        var kind = query ? "IMentorQueryHandler<" + message.Name + ", TResult>"
            : "IMentorCommandHandler<" + message.Name + "> or IMentorCommandHandler<" + message.Name + ", TResult>";
        if (handles.Count == 0)
        {
            why = $"{handler.Name} does not implement {kind}";
            return null;
        }
        if (handles.Count > 1)
        {
            why = $"{handler.Name} implements {kind} {handles.Count} times, and a model reads one result: keep one";
            return null;
        }

        var contract = handles[0];
        var run = contract.GetMethod("HandleAsync")!;
        method = handler.GetInterfaceMap(contract).TargetMethods.Single();
        var result = contract.GetGenericArguments() is { Length: 2 } arguments ? arguments[1] : null;
        return new MentorMessageBinding(message, query, run, result);
    }

    /// <summary>
    /// Why <paramref name="message"/> cannot be a command or a query, or <see langword="null"/>: the model gives an
    /// object, whose properties are the type's (rule <c>fluent.message-checked</c>).
    /// </summary>
    internal static string? NotAnObject(Type message)
    {
        if (message.IsPrimitive || message.IsEnum || message.IsPointer || message.IsByRef || message.IsArray
            || message == typeof(string) || message == typeof(decimal) || message == typeof(object)
            || message.IsAbstract || message.IsInterface || message.ContainsGenericParameters
            || Nullable.GetUnderlyingType(message) is not null)
            return "is not a concrete class or struct";
        if (typeof(System.Collections.IEnumerable).IsAssignableFrom(message))
            return "is a collection";
        return null;
    }

    /// <summary>Calls the handler with the message, and returns what it returned.</summary>
    internal async Task<object?> RunAsync(object handler, object message, CancellationToken cancellationToken)
    {
        var task = (Task)Run.Invoke(handler, BindingFlags.DoNotWrapExceptions, null, [message, cancellationToken], null)!;
        await task.ConfigureAwait(false);
        return ResultType is null ? null : task.GetType().GetProperty(nameof(Task<int>.Result))!.GetValue(task);
    }
}

/// <summary>The tools of the application's actions, as the coordinator, the teams, the specialists and /mcp build them.</summary>
internal static class MentorActionFunctions
{
    /// <summary>
    /// The function of <paramref name="action"/> on <paramref name="target"/>, the instance the container gives: built
    /// from its method, or — for a command or a query (story 1286) — from its type, whose properties are the parameters.
    /// </summary>
    internal static AIFunction Create(MethodDiscoveryInfo action, object target, string description)
        => action.Message is { } message
            ? new MentorMessageFunction(action.ToolName, description, action.Method, message, target)
            : AIFunctionFactory.Create(method: action.Method, target: target, name: action.ToolName, description: description);

    /// <summary>
    /// Story 1773 — the JSON schema of the arguments of the tool <see cref="Create"/> builds for <paramref name="action"/>
    /// with <paramref name="description"/>, built the same way but without an instance: the type's for a command or a
    /// query, the method's parameters for the rest. <see langword="null"/> when the method cannot be described.
    /// </summary>
    internal static JsonElement? Schema(MethodDiscoveryInfo action, string description)
    {
        if (action.Message is { } message)
            return MentorMessageFunction.SchemaOf(message.MessageType);
        try
        {
            var factory = new AIFunctionFactoryOptions { Name = action.ToolName, Description = description };
            var function = action.Method.IsStatic
                ? AIFunctionFactory.Create(action.Method, target: null, factory)
                : AIFunctionFactory.Create(action.Method, _ => throw new InvalidOperationException("Only described."), factory);
            return function.JsonSchema;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>
/// Story 1286 — the tool of a command or a query: its schema is the type's, with its public properties as the
/// parameters (rule <c>fluent.command</c>, <c>fluent.query</c>). A call builds the type from the arguments and gives it
/// to the handler; arguments that do not make one fail the call, and the model reads why.
/// </summary>
internal sealed class MentorMessageFunction(
    string name, string description, MethodInfo handleAsync, MentorMessageBinding binding, object handler) : AIFunction
{
    private static readonly ConcurrentDictionary<Type, JsonElement> Schemas = new();

    /// <inheritdoc/>
    public override string Name => name;

    /// <inheritdoc/>
    public override string Description => description;

    /// <inheritdoc/>
    public override JsonElement JsonSchema => SchemaOf(binding.MessageType);

    /// <summary>The schema of the tool of the command or the query <paramref name="type"/>: its public properties.</summary>
    internal static JsonElement SchemaOf(Type type)
        => Schemas.GetOrAdd(type, t => AIJsonUtilities.CreateJsonSchema(t, serializerOptions: Options));

    /// <summary>The handler's <c>HandleAsync</c>: the gate finds the action by it.</summary>
    public override MethodInfo? UnderlyingMethod => handleAsync;

    /// <inheritdoc/>
    public override JsonSerializerOptions JsonSerializerOptions => Options;

    /// <summary>The command or the query this tool sends.</summary>
    internal MentorMessageBinding Binding => binding;

    private static JsonSerializerOptions Options => AIJsonUtilities.DefaultOptions;

    /// <inheritdoc/>
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var message = Read(arguments);
        var result = await binding.RunAsync(handler, message, cancellationToken).ConfigureAwait(false);
        return result is null ? null : JsonSerializer.SerializeToElement(result, binding.ResultType!, Options);
    }

    /// <summary>The command or the query the arguments make.</summary>
    internal object Read(AIFunctionArguments arguments)
    {
        var json = new JsonObject();
        foreach (var (key, value) in arguments)
            json[key] = value switch
            {
                null => null,
                JsonElement element => JsonNode.Parse(element.GetRawText()),
                JsonNode node => node.DeepClone(),
                _ => JsonSerializer.SerializeToNode(value, value.GetType(), Options),
            };

        try
        {
            return json.Deserialize(binding.MessageType, Options)
                   ?? throw new ArgumentException($"The arguments make no {binding.MessageType.Name}.");
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"The arguments do not make a {binding.MessageType.Name}: {ex.Message}", ex);
        }
    }
}
