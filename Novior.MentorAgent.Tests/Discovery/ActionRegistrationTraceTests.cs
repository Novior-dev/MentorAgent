using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Novior.MentorAgent.Tests.Discovery;

/// <summary>
/// Story 1285, slice B — the one public trace of where the actions come from: <see cref="IMentorActionCatalog.GetRegistrations"/>.
/// One registration per origin and class, with the actions it gives; every action of the catalogue names its origin and
/// its registration, so the bridge joins the two without guessing. The origins are the bridge contract's codes.
/// </summary>
public sealed class ActionRegistrationTraceTests
{
    private static readonly string Stock = typeof(FluentActionTests.StockActions).FullName!;
    private static readonly string Orders = typeof(TestOrderService).FullName!;

    private static MentorHostFixture Host(Action<MentorOptions>? configure = null)
        => MentorHostFixture.Create(options =>
        {
            options.MemoryAutoCapture = false;
            options.Actions
                .Add<FluentActionTests.StockActions>(nameof(FluentActionTests.StockActions.CountLowStock), a => a.Describe("Counts the products low on stock").ReadOnly())
                .Add<FluentActionTests.StockActions>(nameof(FluentActionTests.StockActions.Restock), a => a.Describe("Restocks a product").RequireConfirmation())
                .Add<FluentActionTests.StockActions>(nameof(FluentActionTests.StockActions.PriceOf), a => a.Describe("Reads the price of a product").Off())
                // A class of ScanAssemblies with attribute actions gives one more, registered in code: two registrations.
                .Add<TestOrderService>(nameof(TestOrderService.NotATool), a => a.Describe("Says nothing useful").ReadOnly());
            configure?.Invoke(options);
        }, s => s.AddScoped<FluentActionTests.StockActions>());

    [Fact]
    public async Task The_trace_lists_the_actions_registered_in_code_one_registration_per_class()
    {
        await using var host = Host();
        var registrations = host.Get<IMentorActionCatalog>().GetRegistrations();

        var stock = registrations.Single(r => r.Source == Stock);
        stock.Id.Should().Be("fluent:" + Stock);
        stock.Origin.Should().Be(MentorActionOrigins.Fluent);
        stock.Actions.Should().Equal(["count_low_stock", "restock", "price_of"], "in order, the one shipped off included");
        stock.Enabled.Should().BeTrue();
        stock.Page.Should().BeNull();
        stock.Rejected.Should().BeEmpty();
    }

    [Fact]
    public async Task The_trace_lists_the_attribute_classes_and_a_class_of_both_origins_twice()
    {
        await using var host = Host();
        var registrations = host.Get<IMentorActionCatalog>().GetRegistrations();

        var byAttribute = registrations.Single(r => r.Id == "attribute:" + Orders);
        byAttribute.Origin.Should().Be(MentorActionOrigins.Attribute);
        byAttribute.Source.Should().Be(Orders);
        byAttribute.Actions.Should().Contain(["cancel_order", "count_open_orders", "archive_order"])
            .And.NotContain("not_a_tool");

        registrations.Single(r => r.Id == "fluent:" + Orders).Actions.Should().Equal("not_a_tool");
        registrations.Select(r => r.Id).Should().OnlyHaveUniqueItems();
        registrations.Should().Contain(r => r.Actions.Contains("get_weather"), "every class with actions is a registration");
    }

    [Fact]
    public async Task Every_action_names_its_origin_and_the_registration_that_gives_it()
    {
        await using var host = Host();
        var catalog = host.Get<IMentorActionCatalog>();
        var registrations = catalog.GetRegistrations().ToDictionary(r => r.Id);
        var actions = catalog.GetActions();

        actions.Should().NotBeEmpty().And.AllSatisfy(a =>
        {
            registrations.Should().ContainKey(a.RegistrationId!, a.ToolName);
            registrations[a.RegistrationId!].Actions.Should().Contain(a.ToolName);
            registrations[a.RegistrationId!].Origin.Should().Be(a.Origin);
        });
        actions.Single(a => a.ToolName == "restock").Origin.Should().Be(MentorActionOrigins.Fluent);
        actions.Single(a => a.ToolName == "cancel_order").Origin.Should().Be(MentorActionOrigins.Attribute);
        actions.Single(a => a.ToolName == "not_a_tool").RegistrationId.Should().Be("fluent:" + Orders);

        // A specialist's and a team member's actions are in the trace too, under their class.
        var specialist = actions.First(a => a.AgentKind == MentorAgentKind.Specialist);
        registrations[specialist.RegistrationId!].Actions.Should().Contain(specialist.ToolName);
        actions.Select(a => a.ToolName).Distinct().Should().BeEquivalentTo(registrations.Values.SelectMany(r => r.Actions).Distinct(),
            "the trace and the catalogue describe the same actions");
    }

    [Fact]
    public async Task An_action_written_as_a_method_gives_its_method_and_JSON_leaves_it_out()
    {
        await using var host = Host();
        var actions = host.Get<IMentorActionCatalog>().GetActions();

        // Registered in code, and by the attribute: the application's own method, with its parameters.
        var restock = actions.Single(a => a.ToolName == "restock").Method;
        restock.Should().BeSameAs(typeof(FluentActionTests.StockActions).GetMethod(nameof(FluentActionTests.StockActions.Restock)));
        restock!.GetParameters().Select(p => p.Name).Should().Equal("productId");
        actions.Single(a => a.ToolName == "cancel_order").Method
            .Should().BeSameAs(typeof(TestOrderService).GetMethod(nameof(TestOrderService.CancelOrderAsync)));
        actions.Single(a => a.ToolName == "not_a_tool").Method!.Name.Should().Be(nameof(TestOrderService.NotATool));

        // Shipped off, a specialist's, a team member's: each of them is a method too.
        actions.Single(a => a.ToolName == "price_of").Method!.Name.Should().Be(nameof(FluentActionTests.StockActions.PriceOf));
        actions.Where(a => a.AgentKind != MentorAgentKind.Coordinator).Should().NotBeEmpty()
            .And.OnlyContain(a => a.Method != null && a.Method.DeclaringType != null);

        // A reflection object has no JSON form: the actions serialize, without it.
        var json = System.Text.Json.JsonSerializer.Serialize(actions);
        json.Should().Contain("\"ToolName\":\"restock\"").And.NotContain("\"Method\"");
        new MentorActionInfo { ToolName = "x", AgentKind = MentorAgentKind.Coordinator, McpExposure = MentorMcpExposure.NotServed }
            .Method.Should().BeNull("a catalogue of the application's own gives no method unless it sets one");
    }

    [Fact]
    public void The_origins_are_the_codes_of_the_bridge_contract()
    {
        // contracts/bridge/v1/schemas/inventory.schema.json, actions[].origin: "attribute", "fluent", … — written the same.
        MentorActionOrigins.Attribute.Should().Be("attribute");
        MentorActionOrigins.Fluent.Should().Be("fluent");
    }

    [Fact]
    public void A_catalogue_written_before_the_trace_still_compiles_and_has_none()
    {
        IMentorActionCatalog older = new OlderCatalog();
        older.GetRegistrations().Should().BeEmpty();
        new MentorActionInfo { ToolName = "x", AgentKind = MentorAgentKind.Coordinator, McpExposure = MentorMcpExposure.NotServed }
            .Origin.Should().Be(MentorActionOrigins.Attribute, "an action built before the trace keeps the origin it had");
    }

    /// <summary>An application's own catalogue, written against 1.3.0: it implements only <c>GetActions</c>.</summary>
    private sealed class OlderCatalog : IMentorActionCatalog
    {
        public IReadOnlyList<MentorActionInfo> GetActions() => [];
    }
}
