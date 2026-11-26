using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Server.Extensions;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Novior.MentorAgent.Tests.Server;
using Xunit;

namespace Novior.MentorAgent.Tests.Discovery;

/// <summary>
/// Story 1773 — <see cref="MentorActionInfo.ParametersSchema"/>: the JSON schema of an action's arguments, the one the
/// model reads in the action's tool, for every origin; the bridge reads it from the catalogue instead of working it out.
/// </summary>
public sealed class ActionSchemaTests
{
    private static void Register(MentorOptions o)
    {
        o.MemoryAutoCapture = false;
        o.Actions
            .Add<FluentActionTests.StockActions>(nameof(FluentActionTests.StockActions.Restock),
                a => a.Describe("Restocks a product").Hint("Ask how many first").RequireConfirmation(false))
            .Add<FluentActionTests.StockActions>(nameof(FluentActionTests.StockActions.PriceOf),
                a => a.Describe("Reads the price of a product").Hint("Quote it in euros").ReadOnly())
            .AddCommand<CommandQueryTests.WithdrawOrder, CommandQueryTests.WithdrawOrderHandler>(a => a.Describe("Withdraws an order"))
            .AddQuery<CommandQueryTests.FindOrder, CommandQueryTests.FindOrderHandler>(a => a.Describe("Reads one order"));
    }

    private static void Services(IServiceCollection s)
    {
        s.AddScoped<FluentActionTests.StockActions>();
        s.AddSingleton<CommandQueryTests.OrderBook>();
        s.AddScoped<CommandQueryTests.WithdrawOrderHandler>();
        s.AddScoped<CommandQueryTests.FindOrderHandler>();
    }

    private static MentorHostFixture Host(Action<MentorOptions>? configure = null)
        => MentorHostFixture.Create(o =>
        {
            Register(o);
            configure?.Invoke(o);
        }, Services);

    /// <summary>
    /// Compares the catalogue's schema of every coordinator's action the model was offered with its tool's, and returns
    /// the origins compared.
    /// </summary>
    private static HashSet<string> SameAsTheModel(IMentorActionCatalog catalog, IReadOnlyList<AIFunction> offered)
    {
        var origins = new HashSet<string>();
        foreach (var action in catalog.GetActions().Where(a => a.AgentKind == MentorAgentKind.Coordinator))
        {
            if (offered.FirstOrDefault(t => t.Name == action.ToolName) is not { } tool)
                continue;
            action.ParametersSchema.Should().NotBeNull(action.ToolName);
            JsonElement.DeepEquals(action.ParametersSchema!.Value, tool.JsonSchema).Should().BeTrue(
                $"{action.ToolName}: the catalogue says {action.ParametersSchema.Value.GetRawText()}, the model reads {tool.JsonSchema.GetRawText()}");
            origins.Add(action.Origin);
        }
        return origins;
    }

    private static MentorActionInfo Action(IMentorActionCatalog catalog, string name)
        => catalog.GetActions().Should().ContainSingle(a => a.ToolName == name).Which;

    private static IEnumerable<string> Properties(JsonElement? schema)
        => schema!.Value.GetProperty("properties").EnumerateObject().Select(p => p.Name);

    [Fact]
    [Trait("Rule", "describe.parameters-schema")]
    public async Task An_attribute_a_fluent_a_command_and_a_query_action_have_the_schema_of_the_tool_the_model_is_given()
    {
        await using var host = Host();
        host.Auth.SignIn("ada", "Manager");
        host.Chat!.Say("ok");

        await host.RunTurnAsync("hello");

        var catalog = host.Get<IMentorActionCatalog>();
        SameAsTheModel(catalog, host.Chat.Calls[0].Tools).Should().Contain(
            [MentorActionOrigins.Attribute, MentorActionOrigins.Fluent, MentorActionOrigins.Command, MentorActionOrigins.Query]);
        Properties(Action(catalog, "restock").ParametersSchema).Should().Equal(["productId"], "the method's parameters");
        Properties(Action(catalog, "withdraw_order").ParametersSchema).Select(p => p.ToLowerInvariant())
            .Should().Equal(["orderid", "reason"], "the command's properties");
        Action(catalog, "cancel_order").ParametersSchema.Should().NotBeNull("an attribute action with a hint has one too");
    }

    [Fact]
    [Trait("Rule", "describe.parameters-schema")]
    public async Task A_specialists_and_a_team_members_actions_have_a_schema()
    {
        await using var host = Host();

        host.Get<IMentorActionCatalog>().GetActions().Where(a => a.AgentKind != MentorAgentKind.Coordinator)
            .Should().OnlyContain(a => a.ParametersSchema != null, "their tools are built by the same code");
    }

    [Fact]
    [Trait("Rule", "describe.parameters-schema")]
    public async Task A_candidate_has_the_schema_of_the_tool_the_model_is_given()
    {
        await using var host = MentorHostFixture.Create(o =>
        {
            o.MemoryAutoCapture = false;
            o.AutoMentor.Enabled = true;
            o.AutoMentor.Scan = [typeof(CandidateTests).Assembly];
            o.AutoMentor.Namespaces = ["CandidateShop.Services"];
        }, s =>
        {
            s.AddScoped<CandidateShop.Services.InvoiceService>();
            s.AddScoped<CandidateShop.Services.NamingService>();
            s.AddScoped<CandidateShop.Services.PaymentGateway>();
            s.AddScoped<CandidateShop.Services.Orders.ShipmentService>();
            // A candidate is off until a governor turns it on (candidate.off-and-confirmed).
            s.AddSingleton<IMentorActionGovernor>(new TestActionGovernor { Enabled = _ => true });
        });
        host.Auth.SignIn("ada", "Manager");
        host.Chat!.Say("ok");

        await host.RunTurnAsync("hello");

        SameAsTheModel(host.Get<IMentorActionCatalog>(), host.Chat.Calls[0].Tools).Should().Contain(MentorActionOrigins.Candidate);
    }

    [Fact]
    [Trait("Rule", "describe.parameters-schema")]
    public async Task An_endpoint_has_the_schema_of_its_route_query_and_body_as_the_model_reads_it()
    {
        await using var app = await ServerAppFactory.CreateAsync(
            configure: o => o.MemoryAutoCapture = false,
            userId: "u1",
            configureServices: s => s.AddSingleton<EndpointActionTests.Ledger>(),
            mapEndpoints: e => e.MapPost("/orders/{id:int}/close",
                    (int id, EndpointActionTests.CancelRequest body, string? note, EndpointActionTests.Ledger ledger, HttpContext http)
                        => Results.Ok(new { closed = true }))
                .WithMentorAction("close_order_endpoint", a => a.Describe("Closes an order").RequireConfirmation(false)));
        app.Chat.Say("ok");

        await using (var hub = app.CreateHubConnection())
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            hub.On("StreamingCompleted", () => done.TrySetResult());
            hub.On<string>("Error", _ => done.TrySetResult());
            await hub.StartAsync();
            await hub.InvokeAsync("SendMessage", "please", null);
            await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }

        var catalog = app.Services.GetRequiredService<IMentorActionCatalog>();
        SameAsTheModel(catalog, app.Chat.Calls[0].Tools).Should().Contain(MentorActionOrigins.Endpoint);
        Properties(Action(catalog, "close_order_endpoint").ParametersSchema).Should().BeEquivalentTo(["id", "body", "note"],
            "the route's, the body's and the query's; a service and the request are not the model's");
        Action(catalog, "close_order_endpoint").ParametersSchema.Should().Be(Action(catalog, "close_order_endpoint").ParametersSchema,
            "the same element at every read: an endpoint's tool is not described again");
    }

    [Fact]
    [Trait("Rule", "describe.parameters-schema")]
    public async Task An_endpoint_without_the_server_package_has_no_schema()
    {
        await using var host = Host();
        var handler = typeof(FluentActionTests.StockActions).GetMethod(nameof(FluentActionTests.StockActions.Restock))!;
        host.Get<MentorDiscoveryService>().AddEndpointActions([new MethodDiscoveryInfo(typeof(FluentActionTests.StockActions),
            handler, "restock_endpoint", "Restocks through an endpoint", null, false, null, [], null,
            Origin: MentorActionOrigins.Endpoint, Endpoint: new OpaqueEndpoint())]);

        Action(host.Get<IMentorActionCatalog>(), "restock_endpoint").ParametersSchema
            .Should().BeNull("no Novior.MentorAgent.Server, no tool for the endpoint, and nothing to read the schema from");
    }

    [Fact]
    [Trait("Rule", "describe.parameters-schema")]
    public async Task Each_actions_schema_is_built_once()
    {
        await using var host = Host();
        var catalog = host.Get<IMentorActionCatalog>();

        var first = catalog.GetActions();
        var second = catalog.GetActions();

        second.Select(a => a.ParametersSchema).Should().Equal(first.Select(a => a.ParametersSchema),
            "the same element at every read: the schema is not built again");
    }

    [Fact]
    [Trait("Rule", "describe.parameters-schema")]
    public async Task The_MCP_server_publishes_the_same_schema_although_its_description_has_no_hint()
    {
        await using var host = MentorHostFixture.Create(o =>
        {
            Register(o);
            o.McpServerEnabled = true;
        }, Services);
        var collection = new MentorAgentMcpToolCollection(
            host.Get<MentorDiscoveryService>(),
            host.Scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MentorAgentMcpToolCollection>.Instance,
            host.Scope.ServiceProvider.GetRequiredService<IOptions<MentorOptions>>());

        var published = collection.FindTool("price_of")!.ProtocolTool;
        published.Description.Should().NotContain("Guidance:", "the MCP server leaves the hint out");
        JsonElement.DeepEquals(Action(host.Get<IMentorActionCatalog>(), "price_of").ParametersSchema!.Value, published.InputSchema)
            .Should().BeTrue($"the description is not in the schema: {published.InputSchema.GetRawText()}");
    }

    private sealed class OpaqueEndpoint : IMentorEndpoint
    {
        public string Display => "POST /restock";
    }
}
