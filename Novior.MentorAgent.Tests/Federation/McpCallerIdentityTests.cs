using System.Security.Claims;
using FluentAssertions;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Novior.MentorAgent.Tests.Federation;

/// <summary>
/// <c>MentorOptions.McpCallerPrincipal</c> — ROADMAP #32.
///
/// <para>
/// BUG-021 and BUG-035 were the same S1 twice: a gated action published over MCP to a caller with
/// no principal, on an endpoint whose default CORS is <c>AllowAnyOrigin()</c>. Both were fixed by
/// failing closed — the action is simply not published — and an opt-in to publish them anyway was
/// <b>deliberately not added</b>, because a flag whose only effect is to switch protection off is
/// worse than the gap it closes.
/// </para>
///
/// <para>
/// What was missing was not a flag but an <i>identity</i>. This resolves that: a host that can say
/// who is calling supplies a <see cref="ClaimsPrincipal"/>, and role-gated tools are then published
/// <b>and enforced against that principal</b> — the same gate the widget applies, on the same
/// terms. A host that cannot say gets exactly today's behaviour.
/// </para>
///
/// <para>
/// <b>Confirmation-gated tools are never published, resolver or not.</b> A role can be checked
/// against a principal; a confirmation cannot be answered by one. There is no dialog on the far end
/// of an MCP call and no one watching it, so a "confirmation" would either block until timeout or
/// be waived — and waiving it is precisely the thing BUG-021 was about.
/// </para>
/// </summary>
public sealed class McpCallerIdentityTests
{
    private static MentorAgentMcpToolCollection Build(Action<MentorOptions>? configure = null)
    {
        // Same construction as McpServerSurfaceTests: the fixture owns the discovery data and the
        // DI registrations, and this file only varies the identity options.
        var host = MentorHostFixture.Create(o =>
        {
            o.McpServerEnabled = true;
            configure?.Invoke(o);
        });

        return new MentorAgentMcpToolCollection(
            host.Get<MentorDiscoveryService>(),
            host.Scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MentorAgentMcpToolCollection>.Instance,
            host.Scope.ServiceProvider.GetRequiredService<IOptions<MentorOptions>>());
    }

    private static ClaimsPrincipal Principal(params string[] roles)
        => new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "mcp-caller"),
             .. roles.Select(r => new Claim(ClaimTypes.Role, r))],
            authenticationType: "Test"));

    [Fact]
    public void With_no_resolver_a_role_gated_tool_is_still_not_published()
    {
        // The default, and the property BUG-021 and BUG-035 cost. It must not change.
        var names = Build().GetTools().Select(t => t.ProtocolTool.Name).ToList();

        names.Should().NotContain("export_ledger",
            "with nobody to check the roles against, a role-gated action stays unpublished");
        names.Should().Contain("count_open_orders", "ungated actions are unaffected");
    }

    [Fact]
    public void A_resolver_publishes_role_gated_tools_the_caller_is_entitled_to()
    {
        var names = Build(o => o.McpCallerPrincipal = () => Principal("Admin"))
            .GetTools().Select(t => t.ProtocolTool.Name).ToList();

        names.Should().Contain("export_ledger",
            "an Admin caller is exactly who export_ledger is for");
    }

    [Fact]
    public void A_resolver_does_not_publish_tools_the_caller_is_not_entitled_to()
    {
        // The half that makes this an identity story rather than an off switch.
        var names = Build(o => o.McpCallerPrincipal = () => Principal("Support"))
            .GetTools().Select(t => t.ProtocolTool.Name).ToList();

        names.Should().NotContain("export_ledger",
            "Support is not Admin, and the gate is enforced rather than bypassed");
    }

    [Fact]
    public void A_confirmation_gated_tool_is_never_published_however_privileged_the_caller()
    {
        // No dialog, no one to answer it. A principal cannot stand in for consent.
        var names = Build(o => o.McpCallerPrincipal = () => Principal("Admin", "Manager", "Finance"))
            .GetTools().Select(t => t.ProtocolTool.Name).ToList();

        names.Should().NotContain("issue_refund",
            "a confirmation cannot be answered over MCP, so it cannot be satisfied by a role");
        names.Should().NotContain("cancel_order",
            "roles AND confirmation — the confirmation half still withholds it");
    }

    [Fact]
    public void A_predicate_gated_tool_is_never_published_either()
    {
        // BUG-035's gate. options.RequiresApproval expresses "ask before doing this", which is the
        // same unanswerable thing as RequiresConfirmation — a principal does not resolve it.
        var names = Build(o =>
        {
            o.McpCallerPrincipal = () => Principal("Admin");
            o.RequiresApproval = tool => tool == "count_open_orders";
        }).GetTools().Select(t => t.ProtocolTool.Name).ToList();

        names.Should().NotContain("count_open_orders",
            "however the host expressed the gate, an MCP caller cannot answer it");
    }

    [Fact]
    public void A_resolver_that_returns_null_or_an_unauthenticated_principal_fails_closed()
    {
        // The failure mode that matters: a host wires a resolver, it cannot identify this
        // particular caller, and returns null or an anonymous principal. That must land on the
        // strict default rather than on "no roles required".
        var nullResolver = Build(o => o.McpCallerPrincipal = () => null)
            .GetTools().Select(t => t.ProtocolTool.Name).ToList();
        var anonymous = Build(o => o.McpCallerPrincipal = () => new ClaimsPrincipal(new ClaimsIdentity()))
            .GetTools().Select(t => t.ProtocolTool.Name).ToList();

        nullResolver.Should().NotContain("export_ledger");
        anonymous.Should().NotContain("export_ledger");
    }

    [Fact]
    public void The_tool_list_is_recomputed_for_each_caller_rather_than_cached_from_the_first_one()
    {
        // The defect this file's other tests could not see, because each of them builds a FRESH
        // collection and calls GetTools() once — so the cache is never asked a second question.
        //
        // MentorAgentMcpToolCollection is a SINGLETON whose tool list was built once, in a Lazy,
        // for the lifetime of the application. With McpCallerPrincipal configured that meant the
        // first caller's identity decided what every later caller could see: one Admin request and
        // export_ledger stayed published for everybody, anonymous included — which is exactly the
        // S1 that BUG-021 and BUG-035 were.
        var caller = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity());
        var collection = Build(o => o.McpCallerPrincipal = () => caller);

        caller = Principal("Admin");
        var asAdmin = collection.GetTools().Select(t => t.ProtocolTool.Name).ToList();

        caller = Principal("Support");
        var asSupport = collection.GetTools().Select(t => t.ProtocolTool.Name).ToList();

        caller = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity());
        var asAnonymous = collection.GetTools().Select(t => t.ProtocolTool.Name).ToList();

        asAdmin.Should().Contain("export_ledger", "an Admin is entitled to it");
        asSupport.Should().NotContain("export_ledger",
            "the answer must follow the caller, not the first caller the process happened to serve");
        asAnonymous.Should().NotContain("export_ledger");

        asAdmin.Should().Contain("count_open_orders");
        asAnonymous.Should().Contain("count_open_orders", "ungated tools are unaffected by identity");
    }

    // ── BUG-103: one source for the role decision ──────────────────────────────

    /// <summary>
    /// A principal whose roles are in a <c>roles</c> claim — what an Entra ID token carries, and what
    /// JwtBearer gives with <c>MapInboundClaims = false</c>. <c>IsInRole</c> reads them through the
    /// identity's <c>RoleClaimType</c>; <c>FindAll(ClaimTypes.Role)</c> finds nothing.
    /// </summary>
    private static ClaimsPrincipal EntraPrincipal(string subject, params string[] roles)
        => new(new ClaimsIdentity(
            [new Claim("sub", subject), .. roles.Select(r => new Claim("roles", r))],
            authenticationType: "Bearer", nameType: "name", roleType: "roles"));

    [Fact]
    public void A_roleless_caller_does_not_inherit_an_admins_list_when_roles_use_a_custom_claim_type()
    {
        // The live repro: an Admin lists first, then a signed-in caller with no roles lists — and calls.
        // Both used to get the cache key "auth:" (no ClaimTypes.Role claim on either), so the second
        // was served the first one's list.
        var caller = EntraPrincipal("ada", "Admin");
        var collection = Build(o => o.McpCallerPrincipal = () => caller);

        collection.GetTools().Select(t => t.ProtocolTool.Name).Should().Contain("export_ledger",
            "IsInRole finds Admin through the identity's own RoleClaimType");

        caller = EntraPrincipal("eve");
        collection.GetTools().Select(t => t.ProtocolTool.Name).Should().NotContain("export_ledger",
            "eve holds no role; what an Admin was shown before her is not hers to see");
    }

    [Fact]
    public void An_admin_is_not_denied_because_a_roleless_caller_asked_first()
    {
        // The other order, from a fresh process: the first caller's (empty) entitlement decided for
        // the Admin after her.
        var caller = EntraPrincipal("eve");
        var collection = Build(o => o.McpCallerPrincipal = () => caller);
        collection.GetTools().Select(t => t.ProtocolTool.Name).Should().NotContain("export_ledger");

        caller = EntraPrincipal("ada", "Admin");

        collection.GetTools().Select(t => t.ProtocolTool.Name).Should().Contain("export_ledger");
    }

    [Fact]
    public void A_role_held_by_a_second_identity_counts_as_IsInRole_says()
    {
        // IsInRole looks at every identity of the principal, each with its own RoleClaimType — the same
        // answer [Authorize(Roles)] and the widget's role gate give.
        var caller = EntraPrincipal("eve");
        var collection = Build(o => o.McpCallerPrincipal = () => caller);
        collection.GetTools().Select(t => t.ProtocolTool.Name).Should().NotContain("export_ledger");

        var twoIdentities = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "ada")], "Bearer"));
        twoIdentities.AddIdentity(new ClaimsIdentity([new Claim("roles", "Admin")], "Gateway", "name", "roles"));
        caller = twoIdentities;

        collection.GetTools().Select(t => t.ProtocolTool.Name).Should().Contain("export_ledger");
    }

    [Fact]
    public void An_unauthenticated_primary_identity_is_not_rescued_by_a_second_one()
    {
        // Authentication is judged on the primary identity, as the widget's role gate judges it: one
        // semantics on every surface.
        var principal = new ClaimsPrincipal(new ClaimsIdentity());
        principal.AddIdentity(new ClaimsIdentity([new Claim("roles", "Admin")], "Gateway", "name", "roles"));

        Build(o => o.McpCallerPrincipal = () => principal)
            .GetTools().Select(t => t.ProtocolTool.Name).Should().NotContain("export_ledger");
    }

    [Fact]
    public void The_resolver_is_asked_once_per_request_so_one_answer_decides()
    {
        // The key and the decision used to come from two separate resolver calls. A resolver that threw
        // on the first and answered on the second put an Admin's list under the key "resolver-threw" —
        // served to every later caller whose resolver threw.
        var calls = 0;
        var collection = Build(o => o.McpCallerPrincipal = () =>
            ++calls == 2 ? Principal("Admin") : throw new InvalidOperationException("token store down"));

        var first = collection.GetTools().Select(t => t.ProtocolTool.Name).ToList();
        var second = collection.GetTools().Select(t => t.ProtocolTool.Name).ToList();
        var third = collection.GetTools().Select(t => t.ProtocolTool.Name).ToList();

        first.Should().NotContain("export_ledger", "that request's resolver threw: it withholds, whatever came next");
        second.Should().Contain("export_ledger", "that request's resolver answered Admin");
        third.Should().NotContain("export_ledger", "and a later failure is not served what the Admin was");
    }

    [Fact]
    public void A_call_is_decided_for_the_caller_who_makes_it()
    {
        // tools/call looks the name up with the caller of that request: an Admin having listed first
        // gives eve nothing to call.
        var collection = Build(o => o.McpCallerPrincipal = () => EntraPrincipal("ada", "Admin"));
        collection.GetTools().Select(t => t.ProtocolTool.Name).Should().Contain("export_ledger");

        collection.FindTool("export_ledger", EntraPrincipal("eve")).Should().BeNull();
        collection.FindTool("export_ledger", EntraPrincipal("ada", "Admin")).Should().NotBeNull();
        collection.FindTool("no_such_tool", EntraPrincipal("ada", "Admin")).Should().BeNull();
    }

    [Fact]
    public void Two_actions_sharing_a_tool_name_do_not_stop_the_endpoint()
    {
        // The orchestrator warns about duplicate names; hosts have shipped them (a read-only copy of a
        // business tool for a team member). The first one is called, as the lookup always did — a
        // dictionary built with Add would have thrown, and the lazily built catalogue with it, for good.
        var host = MentorHostFixture.Create(o =>
        {
            o.McpServerEnabled = true;
            o.McpCallerPrincipal = () => Principal("Admin");
        });
        var discovery = host.Get<MentorDiscoveryService>();
        var original = discovery.Discover().Level1Methods.Single(m => m.ToolName == "count_open_orders");
        discovery.Discover().Level1Methods.Add(original with { Description = "A second action with the same name" });

        var collection = new MentorAgentMcpToolCollection(
            discovery,
            host.Scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MentorAgentMcpToolCollection>.Instance,
            host.Scope.ServiceProvider.GetRequiredService<IOptions<MentorOptions>>());

        var act = () => collection.GetTools().Select(t => t.ProtocolTool.Name).ToList();

        act.Should().NotThrow().Subject.Should().Contain("count_open_orders");
        collection.FindTool("count_open_orders")!.ProtocolTool.Description.Should().Be(original.Description,
            "the first action registered under the name is the one called");
    }

    [Fact]
    public void Role_names_are_matched_as_IsInRole_matches_them()
    {
        // Ordinal, as IsInRole and [Authorize(Roles)] compare. A later "case-insensitive" shortcut in
        // this collection would open a gap between the surfaces.
        Build(o => o.McpCallerPrincipal = () => Principal("admin"))
            .GetTools().Select(t => t.ProtocolTool.Name).Should().NotContain("export_ledger");
    }

    [Fact]
    public void A_resolver_that_throws_fails_closed_rather_than_taking_the_server_down()
    {
        // A resolver reaches into the host's own auth stack, which can fail. Neither outcome may be
        // "publish everything", and neither may be "the MCP endpoint stops working".
        var act = () => Build(o => o.McpCallerPrincipal = () => throw new InvalidOperationException("token store down"))
            .GetTools().Select(t => t.ProtocolTool.Name).ToList();

        var names = act.Should().NotThrow().Subject;
        names.Should().NotContain("export_ledger");
        names.Should().Contain("count_open_orders", "ungated tools survive a broken resolver");
    }

    [Fact]
    public void A_call_is_served_by_the_copy_of_a_duplicated_name_the_caller_may_see()
    {
        // Review of the rc.14 fix: with two actions under one name — a role-gated one first — the lookup took
        // the first by name and refused a caller its own tools/list showed.
        var host = MentorHostFixture.Create(o =>
        {
            o.McpServerEnabled = true;
            o.McpCallerPrincipal = () => EntraPrincipal("eve");
        });
        var discovery = host.Get<MentorDiscoveryService>();
        var original = discovery.Discover().Level1Methods.Single(m => m.ToolName == "count_open_orders");
        discovery.Discover().Level1Methods.Insert(0, original with { RequiredRoles = ["Admin"], Description = "Admin copy" });

        var collection = new MentorAgentMcpToolCollection(
            discovery,
            host.Scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MentorAgentMcpToolCollection>.Instance,
            host.Scope.ServiceProvider.GetRequiredService<IOptions<MentorOptions>>());

        collection.GetTools().Select(t => t.ProtocolTool.Name).Should().Contain("count_open_orders");
        collection.FindTool("count_open_orders", EntraPrincipal("eve"))!.ProtocolTool.Description
            .Should().Be(original.Description, "eve is served the copy her list shows, not refused for the Admin one");
    }
}
