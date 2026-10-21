using System.Reflection;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Attributes;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Middleware;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Novior.MentorAgent.Tests.Gating;

/// <summary>
/// BUG-188 — the gate checks a call against the method it runs, not against the first method with the same
/// tool name.
///
/// <para>
/// Up to 1.0.0 the roles, the confirmation, the banner's text and the page to open were looked up by name,
/// taking the first copy found, level 1 before a specialist's. A level-1 method without roles therefore
/// removed the roles of a specialist's action with the same name: on the samples, <c>update_customer_plan</c>
/// is a level-1 action with no roles and a specialist's action for <c>CRMManager</c> and <c>Admin</c>, and
/// the specialist's call ran for anybody.
/// </para>
///
/// <para>
/// The classes below live in this assembly, which these hosts scan next to the test app's.
/// <c>CoordinatorShipments</c> gives level 1 a copy of the test app's specialist action <c>cancel_shipment</c>
/// with no roles, no confirmation and a page of its own; the two <c>SharedTicket</c> classes share
/// <c>review_shared_ticket</c> at level 1, one open and read-only, one for a Manager and confirmed.
/// </para>
/// </summary>
public sealed class CalledMethodGateTests
{
    private const string CoordinatorPage = "/coordinator-shipments";

    private sealed class CoordinatorShipments
    {
        [MentorAction(Description = "Says whether a shipment can still be cancelled", ReadOnly = true,
            NavigateTo = CoordinatorPage)]
        public string CancelShipment(int orderId) => $"can be cancelled: {orderId}";
    }

    private sealed class SharedTicketReader
    {
        public bool Ran { get; private set; }

        [MentorAction(Description = "Shows a ticket for review", ReadOnly = true)]
        public string ReviewSharedTicket(int ticketId)
        {
            Ran = true;
            return $"shown: {ticketId}";
        }
    }

    private sealed class SharedTicketCloser
    {
        public bool Ran { get; private set; }

        [MentorAction(Description = "Closes a ticket after its review", RequiresConfirmation = true,
            RequiredRoles = ["Manager"])]
        public string ReviewSharedTicket(int ticketId)
        {
            Ran = true;
            return $"closed: {ticketId}";
        }
    }

    private static MentorHostFixture HostScanningBoth(Action<MentorOptions>? configure = null)
        => MentorHostFixture.Create(o =>
        {
            o.MemoryAutoCapture = false;
            o.ScanAssemblies = [typeof(TestOrderService).Assembly, typeof(CoordinatorShipments).Assembly];
            configure?.Invoke(o);
        }, services =>
        {
            services.AddScoped<SharedTicketReader>();
            services.AddScoped<SharedTicketCloser>();
        });

    /// <summary>The specialist's own <c>cancel_shipment</c>, as the handoff builder hands it to the specialist.</summary>
    private static AIFunction SpecialistCancelShipment(MentorHostFixture host)
    {
        var discovery = host.Get<MentorDiscoveryService>().Discover();
        discovery.Level1Methods.Should().Contain(m => m.ToolName == "cancel_shipment",
            "the level-1 copy is what this test is about");

        var builder = host.Get<HandoffBuilder>();
        var build = typeof(HandoffBuilder).GetMethod("BuildToolsForAgent", BindingFlags.Instance | BindingFlags.NonPublic);
        build.Should().NotBeNull("HandoffBuilder.BuildToolsForAgent is where a specialist's tools are made");

        var tools = (List<AITool>)build!.Invoke(builder, [discovery.Level2Agents.Single()])!;
        return tools.OfType<AIFunction>().Single(t => t.Name == "cancel_shipment");
    }

    /// <summary>What the gate showed while a call ran: the banners it raised and the pages it asked to open.</summary>
    private sealed class Seen
    {
        public List<string> Banners { get; } = [];
        public List<string> Pages { get; } = [];

        public static Seen On(MentorHostFixture host, bool confirm)
        {
            var seen = new Seen();
            var state = host.Get<IMentorStateService>();
            state.OnConfirmationRequired += req =>
            {
                seen.Banners.Add(req.Message);
                if (confirm) _ = state.ConfirmAsync(req.ActionId);
                else state.Cancel(req.ActionId);
            };
            state.OnNavigationRequested += seen.Pages.Add;
            return seen;
        }
    }

    // ── A specialist's action and a level-1 copy of its name ────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("Viewer")]
    public async Task A_specialists_roles_stop_its_call_when_a_level_1_copy_has_none(string? role)
    {
        await using var host = HostScanningBoth();
        if (role is not null) host.Auth.SignIn("ada", role);
        var seen = Seen.On(host, confirm: true);

        var result = await SpecialistCancelShipment(host)
            .InvokeAsync(new AIFunctionArguments { ["orderId"] = 7 }, CancellationToken.None);

        host.Get<TestShippingAgent>().Invoked.Should().BeEmpty(
            "cancel_shipment of the specialist requires the Manager role, whatever the level-1 copy declares");
        seen.Banners.Should().BeEmpty("a caller without the role is refused before any banner");
        seen.Pages.Should().BeEmpty();
        (result?.ToString() ?? "").Should().NotContain("cancelled: 7");
    }

    [Fact]
    public async Task A_specialists_call_asks_with_its_own_text_and_opens_no_page_of_the_copy()
    {
        await using var host = HostScanningBoth();
        host.Auth.SignIn("ada", "Manager");
        var seen = Seen.On(host, confirm: true);

        await SpecialistCancelShipment(host)
            .InvokeAsync(new AIFunctionArguments { ["orderId"] = 7 }, CancellationToken.None);

        seen.Banners.Should().ContainSingle("the specialist's action asks for confirmation; the level-1 copy does not")
            .Which.Should().StartWith("Cancels a shipment that has not left the warehouse");
        host.Get<TestShippingAgent>().Invoked.Should().Equal(nameof(TestShippingAgent.CancelShipment));
        seen.Pages.Should().NotContain(CoordinatorPage, "the page belongs to the level-1 copy");
    }

    // ── Two level-1 methods with one name ───────────────────────────────────────

    /// <summary>One call through the coordinator's function-invocation middleware, with the function given.</summary>
    private static ValueTask<object?> CallAtLevel1(MentorHostFixture host, AIFunction function)
    {
        var context = new FunctionInvocationContext
        {
            Function = function,
            Arguments = new AIFunctionArguments { ["ticketId"] = 4 },
            CallContent = new FunctionCallContent("call-1", function.Name),
        };

        return host.Get<MentorAgentUIMiddleware>().InvokeFunctionCallingAsync(
            null!, context, (ctx, ct) => ctx.Function.InvokeAsync(ctx.Arguments, ct), CancellationToken.None);
    }

    private static AIFunction Level1Function(object target)
        => AIFunctionFactory.Create(
            target.GetType().GetMethod("ReviewSharedTicket")!, target, name: "review_shared_ticket", description: "x");

    [Fact]
    public async Task At_level_1_each_copy_is_checked_against_its_own_roles()
    {
        // Whichever copy discovery finds first, one of the two calls was judged by the other's attributes.
        await using var host = HostScanningBoth();
        var seen = Seen.On(host, confirm: true);
        var reader = new SharedTicketReader();
        var closer = new SharedTicketCloser();

        var shown = await CallAtLevel1(host, Level1Function(reader));
        var closed = await CallAtLevel1(host, Level1Function(closer));

        reader.Ran.Should().BeTrue("the read-only copy has no roles: an anonymous caller may run it");
        (shown?.ToString() ?? "").Should().Contain("shown: 4");
        closer.Ran.Should().BeFalse("the closing copy requires the Manager role");
        (closed?.ToString() ?? "").Should().NotContain("closed: 4");
        seen.Banners.Should().BeEmpty("the read-only copy asks nothing, and the other is refused before its banner");
    }

    [Fact]
    public async Task At_level_1_each_copy_asks_as_its_own_method_declares()
    {
        await using var host = HostScanningBoth();
        host.Auth.SignIn("ada", "Manager");
        var seen = Seen.On(host, confirm: true);
        var reader = new SharedTicketReader();
        var closer = new SharedTicketCloser();

        await CallAtLevel1(host, Level1Function(reader));
        seen.Banners.Should().BeEmpty("the read-only copy declares no confirmation");

        await CallAtLevel1(host, Level1Function(closer));
        seen.Banners.Should().ContainSingle("the closing copy asks, once")
            .Which.Should().StartWith("Closes a ticket after its review");
        reader.Ran.Should().BeTrue();
        closer.Ran.Should().BeTrue("the Manager confirmed");
    }

    [Fact]
    public async Task In_native_mode_only_the_copy_that_asks_is_marked_for_approval()
    {
        await using var host = HostScanningBoth(o => o.HitlMode = MentorHitlMode.Native);
        host.Chat!.Say("hello");

        (await host.RunTurnAsync()).Should().BeNull();

        // The Agent Framework wraps every function it is given; the marker is found as the framework finds it.
        var copies = host.Chat.Calls[0].Tools.Where(t => t.Name == "review_shared_ticket").ToList();
        copies.Should().HaveCount(2, "both level-1 copies are declared to the model");
        copies.Single(t => t.UnderlyingMethod?.DeclaringType == typeof(SharedTicketCloser))
            .GetService<ApprovalRequiredAIFunction>().Should().NotBeNull("the closing copy requires confirmation");
        copies.Single(t => t.UnderlyingMethod?.DeclaringType == typeof(SharedTicketReader))
            .GetService<ApprovalRequiredAIFunction>().Should().BeNull("the read-only copy does not");
    }
}
