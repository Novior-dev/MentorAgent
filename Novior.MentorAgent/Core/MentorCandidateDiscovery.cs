using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Novior.MentorAgent.Core;

/// <summary>
/// Story 1569 — the AutoMentor's candidates (<see cref="MentorOptions.AutoMentor"/>): the public methods of the services
/// in the perimeter the code declares, which become actions of the coordinator of origin <c>candidate</c>, off and
/// confirmed, described by their XML documentation comment and with the roles of their <c>[Authorize]</c> (slice B). The rules are the <c>candidate</c> area of ACTION-RULES.md; each is named where it is applied.
/// </summary>
internal sealed partial class MentorDiscoveryService
{
    /// <summary>The names of the <c>candidate</c> rules, as ACTION-RULES.md and the trace's <c>Rejected</c> give them.</summary>
    internal static class CandidateRules
    {
        internal const string DiResolvable = "candidate.di-resolvable";
        internal const string Schema = "candidate.schema";
        internal const string Name = "candidate.name";
    }

    /// <summary>
    /// The assemblies of MentorAgent's own packages: their types are never candidates (<c>candidate.in-perimeter</c>).
    /// </summary>
    private static readonly HashSet<string> MentorAgentAssemblies = new(
        ["Novior.MentorAgent", "Novior.MentorAgent.Abstractions", "Novior.MentorAgent.Server", "Novior.MentorAgent.Blazor",
         "Novior.MentorAgent.Declarative"],
        StringComparer.Ordinal);

    private void AddCandidates(DiscoveryResult result)
    {
        var auto = _options.AutoMentor;
        if (!auto.Enabled) return;   // candidate.opt-in

        var namespaces = auto.Namespaces.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct().ToArray();
        if (namespaces.Length == 0)
        {
            _logger.LogWarning(
                "[MentorAgent] AutoMentor is on, but AutoMentor.Namespaces is empty: there is no perimeter, so there is no " +
                "candidate. Name the namespaces of the services it may take candidates from.");
            return;
        }

        var all = result.Level1Methods
            .Concat(result.Level2Agents.SelectMany(a => a.Methods))
            .Concat(result.Level3Teams.SelectMany(t => t.Members).SelectMany(m => m.Tools))
            .ToList();
        var actions = all.Select(m => m.Method).ToHashSet();
        var taken = new HashSet<string>(all.Select(m => m.ToolName), StringComparer.OrdinalIgnoreCase);
        taken.UnionWith(Middleware.MentorAgentUIMiddleware.LibraryTools);
        taken.UnionWith(result.Level3Teams.Select(t => MentorUtils.ToSnakeCase(t.Attribute.Name)));
        var toolboxes = result.Level3Teams.SelectMany(t => t.Members).SelectMany(m => m.Attribute.Tools).ToHashSet();
        var isService = _services?.GetService<IServiceProviderIsService>();
        var documentation = new Dictionary<Assembly, IReadOnlyDictionary<string, string>>();

        var assemblies = (auto.Scan.Length > 0 ? auto.Scan : _options.ScanAssemblies).Distinct();
        var found = 0;
        foreach (var type in assemblies.SelectMany(LoadableTypes).OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            List<MethodInfo> methods;
            try
            {
                if (!InPerimeter(type, namespaces, toolboxes)) continue;

                methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(HasCandidateShape)
                    .Where(m => m.GetCustomAttribute<MentorExcludeAttribute>(inherit: true) is null)   // candidate.excluded
                    .Where(m => !actions.Contains(m))                                                  // candidate.not-yet-an-action
                    .OrderBy(m => m.MetadataToken)
                    .ToList();
            }
            catch (Exception ex) when (IsLoadFailure(ex))
            {
                // BUG-236: a type one of its attributes, or one of its methods', names does not load.
                _logger.LogWarning(
                    "[MentorAgent] AutoMentor: the attributes of {Type} could not be read: a type they name does not load. It gives no candidate.",
                    type.FullName);
                continue;
            }
            if (methods.Count == 0) continue;

            // candidate.di-resolvable: the action runs on the instance the container gives for the class itself.
            if (isService?.IsService(type) != true)
            {
                result.CandidateRejections.AddRange(methods.Select(m => new CandidateRejection(type, m.Name, CandidateRules.DiResolvable)));
                continue;
            }

            foreach (var method in methods)
            {
                var toolName = MentorUtils.ToSnakeCase(method.Name);

                // candidate.name: a name a model calls one tool by.
                if (!taken.Add(toolName))
                {
                    result.CandidateRejections.Add(new CandidateRejection(type, method.Name, CandidateRules.Name));
                    continue;
                }

                // candidate.schema: the tool is built as every action's is; a parameter it cannot describe leaves it out.
                if (!Describable(method, toolName))
                {
                    taken.Remove(toolName);
                    result.CandidateRejections.Add(new CandidateRejection(type, method.Name, CandidateRules.Schema));
                    continue;
                }

                // candidate.described-by-xml: the summary of the method's documentation comment, empty without one.
                if (!documentation.TryGetValue(type.Assembly, out var summaries))
                    documentation[type.Assembly] = summaries =
                        MentorXmlDocumentation.Summaries(MentorXmlDocumentation.FileOf(type.Assembly), _logger);

                // BUG-236: the roles are read from [Authorize]; a method whose attributes cannot be read is no candidate,
                // rather than one without the roles it may ask for.
                string[] roles;
                try
                {
                    roles = RolesFromAuthorize(type, method);
                }
                catch (Exception ex) when (IsLoadFailure(ex))
                {
                    taken.Remove(toolName);
                    _logger.LogWarning(
                        "[MentorAgent] AutoMentor: the attributes of {Type}.{Method} could not be read: a type they name does not load. It is no candidate.",
                        type.FullName, method.Name);
                    continue;
                }

                // candidate.off-and-confirmed: off until a governor turns it on, and confirmed as a write.
                result.Level1Methods.Add(new MethodDiscoveryInfo(
                    DeclaringType: type,
                    Method: method,
                    ToolName: toolName,
                    Description: summaries.GetValueOrDefault(MentorXmlDocumentation.IdOf(method), ""),
                    Category: null,
                    RequiresConfirmation: true,
                    ProactiveHint: null,
                    RequiredRoles: roles,
                    NavigateTo: null,
                    ReadOnly: false,
                    IsMentorAction: true,
                    ConfirmationDeclared: true,
                    EnabledByDefault: false,
                    Origin: MentorActionOrigins.Candidate));
                found++;
            }
        }

        _logger.LogInformation(
            "[MentorAgent] AutoMentor: {Found} candidate(s) in {Namespaces}, off until a governor turns them on; {Rejected} left out by a rule.",
            found, string.Join(", ", namespaces), result.CandidateRejections.Count);
    }

    /// <summary>
    /// <c>candidate.roles-from-authorize</c> — the roles of <c>[Authorize(Roles = …)]</c> on the method and on its class,
    /// any one of them (<c>roles.any-of</c>); none with <c>[AllowAnonymous]</c> on either. Attributes that ask for roles
    /// together, or one that names a policy, give none, with a line in the log: the core has no policy to read, and an
    /// action's roles cannot say "all of these". <c>[Authorize]</c> does not protect a method called on a service: these
    /// roles are the ones the call checks. The attributes are read by the name of their interfaces, as ASP.NET Core
    /// reads them (<c>IAuthorizeData</c>, <c>IAllowAnonymous</c>), so a class derived from <c>[Authorize]</c> counts too:
    /// the core references no ASP.NET Core assembly but the components'.
    /// </summary>
    private string[] RolesFromAuthorize(Type type, MethodInfo method)
    {
        var attributes = AuthorizationAttributes(type).Concat(AuthorizationAttributes(method)).ToList();
        if (attributes.Any(a => a.GetType().GetInterface(AllowAnonymousInterface) is not null)) return [];

        var authorize = attributes
            .Select(a => (Attribute: a, Data: a.GetType().GetInterface(AuthorizeDataInterface)))
            .Where(a => a.Data is not null)
            .Select(a => (Roles: a.Data!.GetProperty("Roles")?.GetValue(a.Attribute) as string,
                          Policy: a.Data!.GetProperty("Policy")?.GetValue(a.Attribute) as string))
            .ToList();
        var policies = authorize.Select(a => a.Policy).OfType<string>().Where(p => p.Trim().Length > 0).Distinct().ToList();
        if (policies.Count > 0)
        {
            _logger.LogInformation(
                "[MentorAgent] AutoMentor: the candidate {Type}.{Method} names the policy {Policies} in [Authorize], which " +
                "MentorAgent does not read as roles: the candidate takes no roles from its [Authorize].",
                type.Name, method.Name, string.Join(", ", policies));
            return [];
        }

        var roleSets = authorize.Select(a => a.Roles).OfType<string>()
            .Select(roles => roles.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.Ordinal).ToArray())
            .Where(roles => roles.Length > 0)
            .DistinctBy(roles => string.Join('\n', roles.Order(StringComparer.Ordinal)))
            .ToList();
        if (roleSets.Count <= 1) return roleSets.FirstOrDefault() ?? [];

        _logger.LogInformation(
            "[MentorAgent] AutoMentor: the candidate {Type}.{Method} asks for roles together ({Roles}) in [Authorize], which " +
            "an action's roles cannot say: the candidate takes no roles from its [Authorize].",
            type.Name, method.Name, string.Join(" and ", roleSets.Select(roles => string.Join(" or ", roles))));
        return [];
    }

    /// <summary>
    /// BUG-236 — the attributes of <paramref name="member"/>, and those it inherits, that are <c>[Authorize]</c> or
    /// <c>[AllowAnonymous]</c>: their types are found in the metadata by their interfaces, and only those attributes are
    /// created. Another attribute of the application, one whose constructor throws among them, is never built.
    /// </summary>
    private static IEnumerable<object> AuthorizationAttributes(MemberInfo member)
    {
        var types = new HashSet<Type>();
        for (MemberInfo? m = member; m is not null; m = Inherited(m))
            foreach (var data in m.GetCustomAttributesData())
                if (data.AttributeType.GetInterface(AuthorizeDataInterface) is not null
                    || data.AttributeType.GetInterface(AllowAnonymousInterface) is not null)
                    types.Add(data.AttributeType);

        // One family once: asked for a type, GetCustomAttributes also gives the attributes of the types derived from it.
        return types.Where(t => !types.Any(other => other != t && other.IsAssignableFrom(t)))
            .SelectMany(t => member.GetCustomAttributes(t, inherit: true))
            .ToList();

        static MemberInfo? Inherited(MemberInfo m) => m switch
        {
            Type { BaseType: { } baseType } when baseType != typeof(object) => baseType,
            MethodInfo method when method.GetBaseDefinition() is var root && root != method => root,
            _ => null,
        };
    }

    private const string AuthorizeDataInterface = "Microsoft.AspNetCore.Authorization.IAuthorizeData";
    private const string AllowAnonymousInterface = "Microsoft.AspNetCore.Authorization.IAllowAnonymous";

    private IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            _logger.LogWarning("[MentorAgent] AutoMentor: some types from {Asm} could not be loaded.", assembly.GetName().Name);
            return ex.Types.OfType<Type>();
        }
    }

    /// <summary>
    /// <c>candidate.in-perimeter</c>: a public, concrete, non-generic class in one of <paramref name="namespaces"/> or
    /// under it, and none of MentorAgent's, of the controllers (endpoints), of the specialists, teams, skills and team
    /// toolboxes (they have their own rules), of the components, or generated by the compiler. <c>candidate.excluded</c>:
    /// not one with <c>[MentorExclude]</c>.
    /// </summary>
    private static bool InPerimeter(Type type, string[] namespaces, HashSet<Type> toolboxes)
        => type is { IsClass: true, IsAbstract: false, IsVisible: true, ContainsGenericParameters: false }
           && type.Namespace is { } ns
           && namespaces.Any(n => ns == n || ns.StartsWith(n + ".", StringComparison.Ordinal))
           && !MentorAgentAssemblies.Contains(type.Assembly.GetName().Name ?? "")
           && !IsController(type)
           && type.GetCustomAttribute<MentorAgentAttribute>() is null
           && type.GetCustomAttribute<MentorTeamAttribute>() is null
           && type.GetCustomAttribute<MentorSkillAttribute>() is null
           && !toolboxes.Contains(type)
           && !typeof(Microsoft.AspNetCore.Components.IComponent).IsAssignableFrom(type)
           && type.GetCustomAttribute<CompilerGeneratedAttribute>() is null
           && type.GetCustomAttribute<MentorExcludeAttribute>(inherit: true) is null;

    /// <summary>
    /// <c>candidate.shape</c>: a public instance method the class declares, not an accessor or an operator, not one of
    /// <see cref="object"/>'s nor generated by the compiler, not generic, without <c>ref</c>, <c>out</c> or pointers.
    /// </summary>
    private static bool HasCandidateShape(MethodInfo method)
        => !method.IsSpecialName
           && !method.ContainsGenericParameters
           && method.GetBaseDefinition().DeclaringType != typeof(object)
           && method.GetCustomAttribute<CompilerGeneratedAttribute>() is null
           && !method.ReturnType.IsByRef && !method.ReturnType.IsPointer
           && method.GetParameters().All(p => !p.ParameterType.IsByRef && !p.ParameterType.IsPointer && !p.IsOut);

    /// <summary><c>candidate.schema</c>: whether the tool of <paramref name="method"/> can be built and described.</summary>
    private static bool Describable(MethodInfo method, string toolName)
    {
        try
        {
            var function = AIFunctionFactory.Create(method, _ => throw new InvalidOperationException("Only described."),
                new AIFunctionFactoryOptions { Name = toolName });
            return function.JsonSchema.ValueKind == System.Text.Json.JsonValueKind.Object;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Story 1569 — an endpoint's action, added after discovery, that takes a candidate's name: the candidate is left
    /// out by <c>candidate.name</c>, and the endpoint keeps its name. Returns whether a candidate was left out.
    /// </summary>
    private bool YieldCandidateName(DiscoveryResult result, string toolName)
    {
        var candidate = result.Level1Methods.FirstOrDefault(m =>
            m.Origin == MentorActionOrigins.Candidate && string.Equals(m.ToolName, toolName, StringComparison.OrdinalIgnoreCase));
        if (candidate is null) return false;

        result.Level1Methods.Remove(candidate);
        result.CandidateRejections.Add(new CandidateRejection(candidate.DeclaringType, candidate.Method.Name, CandidateRules.Name));
        _logger.LogInformation(
            "[MentorAgent] AutoMentor: the candidate {Type}.{Method} is left out ({Rule}): an endpoint's action is named '{Name}'.",
            candidate.DeclaringType.Name, candidate.Method.Name, CandidateRules.Name, toolName);
        return true;
    }
}
