using System.ComponentModel;
using System.Reflection;
using Novior.MentorAgent.Attributes;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Utility;

namespace Novior.MentorAgent.Options;

/// <summary>
/// The actions the application registers in code, without attributes: <see cref="MentorOptions.Actions"/>. Each one is a
/// public instance method of a class the application registers in its services, with what <c>[MentorAction]</c> would
/// declare.
/// <para>
/// A registered action is one of the coordinator's actions, like a <c>[MentorAction]</c> method found in
/// <see cref="MentorOptions.ScanAssemblies"/>: it is named after its method, and the same roles, confirmations,
/// <c>IMentorActionGovernor</c>, MCP server and A2A card apply. The registrations are read once, when MentorAgent first
/// discovers the actions. A mistake — a method that is not there, a registration without a description, a method that
/// is already an action by its attributes — stops the application when it starts, naming the method.
/// </para>
/// <code>
/// options.Actions.Add&lt;OrderActions&gt;(nameof(OrderActions.CancelOrderAsync), action =&gt; action
///     .Describe("Cancels an order that has not shipped yet")
///     .RequireRoles("Manager")
///     .RequireConfirmation());
/// </code>
/// <para>
/// A command or a query is registered the same way, as a type of its own with its handler: <see cref="AddCommand{TCommand, THandler}"/>
/// and <see cref="AddQuery{TQuery, THandler}"/>.
/// </para>
/// </summary>
public sealed class MentorActionRegistrations
{
    private readonly List<Registration> _registrations = [];

    /// <summary>One per <see cref="MentorOptions"/>: <see cref="MentorOptions.Actions"/>.</summary>
    internal MentorActionRegistrations() { }

    /// <summary>What was registered, in order.</summary>
    internal IReadOnlyList<Registration> All => _registrations;

    /// <summary>How many actions are registered.</summary>
    public int Count => _registrations.Count;

    /// <summary>
    /// Registers <paramref name="method"/> of <typeparamref name="T"/> as an action of the coordinator.
    /// </summary>
    /// <typeparam name="T">A class the application registers in its services: MentorAgent asks them for the instance.</typeparam>
    /// <param name="method">
    /// The name of a public instance method declared on <typeparamref name="T"/> itself, not generic and the only one
    /// with that name — written with <c>nameof</c>, it follows a rename.
    /// </param>
    /// <param name="configure">What the action declares; <see cref="MentorActionBuilder.Describe"/> is required.</param>
    /// <returns>These registrations, to add the next one.</returns>
    /// <exception cref="ArgumentException">
    /// <typeparamref name="T"/> is not a concrete class, it has no such method or more than one, the method is static or
    /// generic, or the action has no description.
    /// </exception>
    public MentorActionRegistrations Add<T>(string method, Action<MentorActionBuilder> configure) where T : class
        => Add(typeof(T), method, configure);

    /// <summary>
    /// Registers <paramref name="method"/> of <paramref name="type"/> as an action of the coordinator: the form for code
    /// that is generated, where the type is known by name.
    /// </summary>
    /// <param name="type">A class the application registers in its services: MentorAgent asks them for the instance.</param>
    /// <param name="method">
    /// The name of a public instance method declared on <paramref name="type"/> itself, not generic and the only one
    /// with that name.
    /// </param>
    /// <param name="configure">What the action declares; <see cref="MentorActionBuilder.Describe"/> is required.</param>
    /// <returns>These registrations, to add the next one.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="type"/> is not a concrete class, it has no such method or more than one, the method is static or
    /// generic, or the action has no description.
    /// </exception>
    public MentorActionRegistrations Add(Type type, string method, Action<MentorActionBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(configure);

        if (!type.IsClass || type.IsAbstract || type.ContainsGenericParameters)
            throw new ArgumentException(
                $"[MentorAgent] Actions: {type.Name} is not a concrete class. An action is a method of a class the " +
                "application registers in its services.", nameof(type));

        var named = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => m.Name == method && !m.IsSpecialName)
            .ToList();
        if (named.Count == 0)
            throw new ArgumentException(
                $"[MentorAgent] Actions: {type.Name} declares no public method {method}. The method has to be declared on " +
                "the class itself, as a public instance method.", nameof(method));
        if (named.Count > 1)
            throw new ArgumentException(
                $"[MentorAgent] Actions: {type.Name} declares {named.Count} public methods named {method}. A model calls an " +
                "action by its name, so it can be only one: rename the others.", nameof(method));

        var info = named[0];
        if (info.IsStatic)
            throw new ArgumentException(
                $"[MentorAgent] Actions: {type.Name}.{method} is static. An action is an instance method: MentorAgent asks " +
                $"the services for a {type.Name} and calls the method on it.", nameof(method));
        if (info.IsGenericMethodDefinition)
            throw new ArgumentException(
                $"[MentorAgent] Actions: {type.Name}.{method} is generic. A model cannot choose a type argument.",
                nameof(method));

        var action = new MentorActionBuilder();
        configure(action);
        if (string.IsNullOrWhiteSpace(action.Description))
            throw new ArgumentException(
                $"[MentorAgent] Actions: {type.Name}.{method} has no description. Call Describe: the description is " +
                "what the model reads to decide when to call the action.", nameof(configure));

        _registrations.Add(new Registration(type, info, action.Description!, action.CategoryValue, action.Confirmation,
            action.IsReadOnly, [.. action.Roles], action.Enabled, action.Page, action.ProactiveHint)
        {
            ToolName = ToolName(action, MentorUtils.ToSnakeCase(info.Name), $"{type.Name}.{method}"),
        });
        return this;
    }

    /// <summary>
    /// Story 1286 — registers the command <typeparamref name="TCommand"/> as an action of the coordinator, run by
    /// <typeparamref name="THandler"/>: a request that changes data, written as a type of its own. The action is named
    /// after the type in snake_case (<c>CancelOrder</c> is <c>cancel_order</c>) unless <see cref="MentorActionBuilder.Name"/>
    /// names it; its parameters are the type's public properties, which the model gives; it is a write, so the default
    /// confirmation for writes applies, and the same roles, governor, policies, MCP server, A2A card and teams apply as to
    /// a method registered with <see cref="Add{T}"/>.
    /// <code>
    /// public sealed record CancelOrder(int OrderId, string Reason);
    /// public sealed class CancelOrderHandler(OrderStore store) : IMentorCommandHandler&lt;CancelOrder&gt;
    /// {
    ///     public Task HandleAsync(CancelOrder command, CancellationToken ct) =&gt; store.CancelAsync(command.OrderId, command.Reason, ct);
    /// }
    ///
    /// builder.Services.AddScoped&lt;CancelOrderHandler&gt;();
    /// options.Actions.AddCommand&lt;CancelOrder, CancelOrderHandler&gt;(a =&gt; a.Describe("Cancels an order that has not shipped"));
    /// </code>
    /// </summary>
    /// <typeparam name="TCommand">The command: a class, a record or a struct whose public properties the model gives.</typeparam>
    /// <typeparam name="THandler">
    /// A class the application registers in its services, implementing
    /// <c>IMentorCommandHandler&lt;TCommand&gt;</c> or <c>IMentorCommandHandler&lt;TCommand, TResult&gt;</c>, once.
    /// </typeparam>
    /// <param name="configure">What the action declares; <see cref="MentorActionBuilder.Describe"/> is required.</param>
    /// <returns>These registrations, to add the next one.</returns>
    /// <exception cref="ArgumentException">
    /// <typeparamref name="TCommand"/> is not an object with properties, <typeparamref name="THandler"/> is not a concrete
    /// class or does not handle the command once, the action has no description, its name is not a tool name, or it is
    /// declared <see cref="MentorActionBuilder.ReadOnly"/>.
    /// </exception>
    public MentorActionRegistrations AddCommand<TCommand, THandler>(Action<MentorActionBuilder> configure)
        where TCommand : notnull where THandler : class
        => AddMessage(typeof(TCommand), typeof(THandler), query: false, configure);

    /// <summary>
    /// Story 1286 — registers the query <typeparamref name="TQuery"/> as an action of the coordinator, answered by
    /// <typeparamref name="THandler"/>: a request that only reads, written as a type of its own. The action is named after
    /// the type in snake_case unless <see cref="MentorActionBuilder.Name"/> names it; its parameters are the type's public
    /// properties; it is always read-only, and the model reads what the handler returns. The same roles, confirmation,
    /// governor, policies, MCP server, A2A card and teams apply as to a method registered with <see cref="Add{T}"/>.
    /// <code>
    /// public sealed record FindOrder(int OrderId);
    /// public sealed class FindOrderHandler(OrderStore store) : IMentorQueryHandler&lt;FindOrder, OrderView?&gt;
    /// {
    ///     public Task&lt;OrderView?&gt; HandleAsync(FindOrder query, CancellationToken ct) =&gt; store.FindAsync(query.OrderId, ct);
    /// }
    ///
    /// builder.Services.AddScoped&lt;FindOrderHandler&gt;();
    /// options.Actions.AddQuery&lt;FindOrder, FindOrderHandler&gt;(a =&gt; a.Describe("Reads one order"));
    /// </code>
    /// </summary>
    /// <typeparam name="TQuery">The query: a class, a record or a struct whose public properties the model gives.</typeparam>
    /// <typeparam name="THandler">
    /// A class the application registers in its services, implementing <c>IMentorQueryHandler&lt;TQuery, TResult&gt;</c>, once.
    /// </typeparam>
    /// <param name="configure">What the action declares; <see cref="MentorActionBuilder.Describe"/> is required.</param>
    /// <returns>These registrations, to add the next one.</returns>
    /// <exception cref="ArgumentException">
    /// <typeparamref name="TQuery"/> is not an object with properties, <typeparamref name="THandler"/> is not a concrete
    /// class or does not answer the query once, the action has no description, or its name is not a tool name.
    /// </exception>
    public MentorActionRegistrations AddQuery<TQuery, THandler>(Action<MentorActionBuilder> configure)
        where TQuery : notnull where THandler : class
        => AddMessage(typeof(TQuery), typeof(THandler), query: true, configure);

    /// <summary>Rule <c>fluent.message-checked</c>: a command or a query MentorAgent can build and send, checked here.</summary>
    private MentorActionRegistrations AddMessage(Type message, Type handler, bool query, Action<MentorActionBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var what = query ? "query" : "command";

        if (MentorMessageBinding.NotAnObject(message) is { } notAnObject)
            throw new ArgumentException(
                $"[MentorAgent] Actions: the {what} {message.Name} {notAnObject}. A model gives an object, whose properties " +
                $"are the {what}'s: write it as a class, a record or a struct.");
        if (!handler.IsClass || handler.IsAbstract || handler.ContainsGenericParameters)
            throw new ArgumentException(
                $"[MentorAgent] Actions: {handler.Name}, the handler of the {what} {message.Name}, is not a concrete class. " +
                "A handler is a class the application registers in its services.");

        var binding = MentorMessageBinding.For(message, handler, query, out var method, out var why)
                      ?? throw new ArgumentException($"[MentorAgent] Actions: the {what} {message.Name} has no handler: {why}.");

        var action = new MentorActionBuilder();
        configure(action);
        if (string.IsNullOrWhiteSpace(action.Description))
            throw new ArgumentException(
                $"[MentorAgent] Actions: the {what} {message.Name} has no description. Call Describe: the description is " +
                "what the model reads to decide when to call the action.", nameof(configure));
        if (!query && action.IsReadOnly)
            throw new ArgumentException(
                $"[MentorAgent] Actions: the command {message.Name} is declared ReadOnly. A command changes data; a request " +
                "that only reads is a query: register it with AddQuery.", nameof(configure));

        _registrations.Add(new Registration(handler, method!, action.Description!, action.CategoryValue, action.Confirmation,
            query, [.. action.Roles], action.Enabled, action.Page, action.ProactiveHint)
        {
            ToolName = ToolName(action, MentorUtils.ToSnakeCase(message.Name), $"the {what} {message.Name}"),
            Message = binding,
        });
        return this;
    }

    /// <summary>The name the model calls the action by: the one declared with <see cref="MentorActionBuilder.Name"/>, or <paramref name="named"/>.</summary>
    private static string ToolName(MentorActionBuilder action, string named, string what)
    {
        var name = action.ToolName ?? named;
        return MentorPageActionTrace.IsActionName(name) ? name
            : throw new ArgumentException(
                $"[MentorAgent] Actions: {what} would be called '{name}', which is not a tool name (letters, digits, _ and -, " +
                "up to 64). Name it with Name.");
    }

    /// <summary>
    /// Rule <c>fluent.no-double</c>: a method is registered once, and a method that is already an action by its attributes
    /// in <paramref name="scanned"/> is not registered again. Called by <see cref="MentorOptions.Validate"/>, when every
    /// option is set.
    /// </summary>
    internal void Validate(IReadOnlyCollection<Assembly> scanned)
    {
        // fluent.message-checked: a command or a query is registered once, and its name is no other registration's.
        if (_registrations.Where(r => r.Message is not null).GroupBy(r => r.Message!.MessageType)
                .FirstOrDefault(g => g.Count() > 1) is { } sent)
            throw new InvalidOperationException(
                $"[MentorAgent] Actions registers {sent.Key.Name} {sent.Count()} times, as a command or a query. Register it once.");
        if (_registrations.GroupBy(r => r.ToolName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(g => g.Count() > 1 && g.Any(r => r.Message is not null)) is { } named)
            throw new InvalidOperationException(
                $"[MentorAgent] Actions registers {named.Count()} actions named {named.Key}: {string.Join(", ", named.Select(Name))}. " +
                "A model calls an action by its name: give the command or the query another one with Name.");

        if (_registrations.GroupBy(r => r.Method).FirstOrDefault(g => g.Count() > 1) is { } twice)
            throw new InvalidOperationException(
                $"[MentorAgent] Actions registers {Name(twice.First())} {twice.Count()} times. Register it once.");

        foreach (var registration in _registrations)
        {
            if (!scanned.Contains(registration.Type.Assembly)) continue;

            var attribute = registration.Method.GetCustomAttribute<MentorActionAttribute>() is not null ? "[MentorAction]"
                : registration.Method.GetCustomAttribute<DescriptionAttribute>() is { } description
                  && !string.IsNullOrWhiteSpace(description.Description) ? "[Description]"
                : null;
            if (attribute is not null)
                throw new InvalidOperationException(
                    $"[MentorAgent] {Name(registration)} is registered twice: it is an action by its {attribute} attribute, " +
                    $"in {registration.Type.Assembly.GetName().Name}, one of ScanAssemblies, and Actions registers it too. " +
                    "Keep one: remove the attribute or the registration.");
        }
    }

    private static string Name(Registration registration)
        => registration.Message is { } message
            ? $"the {(message.IsQuery ? "query" : "command")} {message.MessageType.Name}"
            : $"{registration.Type.Name}.{registration.Method.Name}";

    /// <summary>
    /// One registered action, as <see cref="MentorActionBuilder"/> left it. For a command or a query (story 1286),
    /// <see cref="Type"/> and <see cref="Method"/> are its handler's and its <c>HandleAsync</c>, and <see cref="Message"/>
    /// says what it sends.
    /// </summary>
    internal sealed record Registration(
        Type Type,
        MethodInfo Method,
        string Description,
        string? Category,
        bool? Confirmation,
        bool ReadOnly,
        string[] Roles,
        bool Enabled,
        string? NavigateTo,
        string? ProactiveHint)
    {
        /// <summary>The name the model calls it by.</summary>
        public string ToolName { get; init; } = "";

        /// <summary>The command or the query it sends; <see langword="null"/> for a method.</summary>
        public MentorMessageBinding? Message { get; init; }
    }
}

/// <summary>
/// What a registered action declares, as <c>[MentorAction]</c> would: given to the callback of
/// <see cref="MentorActionRegistrations.Add{T}"/>, <see cref="MentorActionRegistrations.AddCommand{TCommand, THandler}"/>
/// and <see cref="MentorActionRegistrations.AddQuery{TQuery, THandler}"/>. Each call returns the builder, to write the next.
/// </summary>
public sealed class MentorActionBuilder
{
    internal MentorActionBuilder() { }

    internal string? Description { get; private set; }
    internal string? CategoryValue { get; private set; }
    internal bool? Confirmation { get; private set; }
    internal bool IsReadOnly { get; private set; }
    internal IReadOnlyList<string> Roles { get; private set; } = [];
    internal bool Enabled { get; private set; } = true;
    internal string? Page { get; private set; }
    internal string? ProactiveHint { get; private set; }
    internal string? ToolName { get; private set; }

    /// <summary>
    /// The name the model calls the action by, instead of the method's name, or the command's or the query's type's, in
    /// snake_case: letters, digits, <c>_</c> and <c>-</c>, up to 64. Another name stops the registration.
    /// </summary>
    public MentorActionBuilder Name(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ToolName = name;
        return this;
    }

    /// <summary>What the action does, in the words the model reads to decide when to call it. Required.</summary>
    public MentorActionBuilder Describe(string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        Description = description;
        return this;
    }

    /// <summary>
    /// A free-form category, published as the skill's tag on the A2A agent card, as <c>[MentorAction(Category = …)]</c>.
    /// It does not change what the assistant sees or does.
    /// </summary>
    public MentorActionBuilder Category(string category)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        CategoryValue = category;
        return this;
    }

    /// <summary>
    /// Whether the user confirms before the action runs, declared: <see langword="true"/> asks, <see langword="false"/>
    /// opts the action out. Not called, the action follows <see cref="MentorOptions.RequireConfirmationForWrites"/>, as a
    /// <c>[MentorAction]</c> that declares nothing: on by default, so an action that is not <see cref="ReadOnly"/> asks.
    /// </summary>
    public MentorActionBuilder RequireConfirmation(bool required = true)
    {
        Confirmation = required;
        return this;
    }

    /// <summary>
    /// The action only reads: it changes no data. MentorAgent takes your word for it, as for
    /// <c>[MentorAction(ReadOnly = true)]</c>. Ignored when the action also asks for confirmation. A query is always
    /// read-only; a command cannot be.
    /// </summary>
    public MentorActionBuilder ReadOnly()
    {
        IsReadOnly = true;
        return this;
    }

    /// <summary>
    /// The roles allowed to run the action: a signed-in user who holds any one of them, checked with
    /// <see cref="System.Security.Claims.ClaimsPrincipal.IsInRole"/>, as <c>[MentorAction(RequiredRoles = …)]</c>. A
    /// second call replaces the first.
    /// </summary>
    public MentorActionBuilder RequireRoles(params string[] roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        if (roles.Length == 0 || roles.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("[MentorAgent] RequireRoles needs at least one role, and no role may be blank.", nameof(roles));
        Roles = [.. roles];
        return this;
    }

    /// <summary>
    /// The action ships off, as <c>[MentorAction(Enabled = false)]</c>: it is in the catalogue and nowhere else, and a call
    /// to it does not run, until an <c>IMentorActionGovernor</c> turns it on.
    /// </summary>
    public MentorActionBuilder Off()
    {
        Enabled = false;
        return this;
    }

    /// <summary>
    /// The page MentorAgent opens after the action succeeds, when the user is on another one, as
    /// <c>[MentorAction(NavigateTo = …)]</c>.
    /// </summary>
    public MentorActionBuilder NavigateTo(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        Page = url;
        return this;
    }

    /// <summary>
    /// Guidance for the assistant about what to suggest before or after the action, appended to its description as
    /// "Guidance: …", as <c>[MentorAction(ProactiveHint = …)]</c>.
    /// </summary>
    public MentorActionBuilder Hint(string guidance)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guidance);
        ProactiveHint = guidance;
        return this;
    }
}
