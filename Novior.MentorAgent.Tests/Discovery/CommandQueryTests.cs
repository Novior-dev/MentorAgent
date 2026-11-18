using System.ComponentModel;
using System.Text.Json;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Novior.MentorAgent.Tests.Discovery;

/// <summary>
/// Story 1286 — commands and queries as a way of declaring actions: a type of its own and its handler, registered with
/// <see cref="MentorActionRegistrations.AddCommand{TCommand, THandler}"/> and
/// <see cref="MentorActionRegistrations.AddQuery{TQuery, THandler}"/>. The tool's parameters are the type's properties;
/// a query is read-only, a command a write; the same roles, confirmations, policies, MCP server, A2A card and teams apply
/// as to a method registered in code; the trace lists one registration per type. Mistakes stop the application when it
/// starts.
/// </summary>
public sealed class CommandQueryTests
{
    public sealed record WithdrawOrder(int OrderId, [property: Description("Why the order is withdrawn")] string Reason);
    public sealed record PlaceOrder(string Product, int Quantity);
    public sealed record FindOrder(int OrderId);
    public sealed record OrderView(int OrderId, string Status);
    public sealed record ShipOrder(int OrderId);
    public sealed record TrackOrder(int OrderId);

    /// <summary>What the handlers did, shared by the scopes of one host.</summary>
    public sealed class OrderBook
    {
        public List<string> Log { get; } = [];
    }

    public sealed class WithdrawOrderHandler(OrderBook book) : IMentorCommandHandler<WithdrawOrder>
    {
        public Task HandleAsync(WithdrawOrder command, CancellationToken cancellationToken)
        {
            book.Log.Add($"withdraw {command.OrderId} {command.Reason}");
            return Task.CompletedTask;
        }
    }

    public sealed class PlaceOrderHandler(OrderBook book) : IMentorCommandHandler<PlaceOrder, int>
    {
        public Task<int> HandleAsync(PlaceOrder command, CancellationToken cancellationToken)
        {
            book.Log.Add($"place {command.Quantity} {command.Product}");
            return Task.FromResult(4242);
        }
    }

    public sealed class FindOrderHandler(OrderBook book) : IMentorQueryHandler<FindOrder, OrderView>
    {
        public Task<OrderView> HandleAsync(FindOrder query, CancellationToken cancellationToken)
        {
            book.Log.Add($"find {query.OrderId}");
            return Task.FromResult(new OrderView(query.OrderId, "shipped-on-monday"));
        }
    }

    /// <summary>One class for a command and a query, both written as explicit implementations.</summary>
    public sealed class ShippingHandlers(OrderBook book) : IMentorCommandHandler<ShipOrder>, IMentorQueryHandler<TrackOrder, string>
    {
        Task IMentorCommandHandler<ShipOrder>.HandleAsync(ShipOrder command, CancellationToken cancellationToken)
        {
            book.Log.Add($"ship {command.OrderId}");
            return Task.CompletedTask;
        }

        Task<string> IMentorQueryHandler<TrackOrder, string>.HandleAsync(TrackOrder query, CancellationToken cancellationToken)
        {
            book.Log.Add($"track {query.OrderId}");
            return Task.FromResult("in-transit-near-turin");
        }
    }

    public sealed class TwiceHandler : IMentorCommandHandler<WithdrawOrder>, IMentorCommandHandler<WithdrawOrder, int>
    {
        Task IMentorCommandHandler<WithdrawOrder>.HandleAsync(WithdrawOrder command, CancellationToken cancellationToken) => Task.CompletedTask;
        Task<int> IMentorCommandHandler<WithdrawOrder, int>.HandleAsync(WithdrawOrder command, CancellationToken cancellationToken) => Task.FromResult(0);
    }

    public abstract class AbstractHandler : IMentorCommandHandler<WithdrawOrder>
    {
        public abstract Task HandleAsync(WithdrawOrder command, CancellationToken cancellationToken);
    }

    private static void Register(MentorOptions o) => o.Actions
        .AddCommand<WithdrawOrder, WithdrawOrderHandler>(a => a.Describe("Withdraws an order that has not shipped").RequireRoles("Manager"))
        .AddCommand<PlaceOrder, PlaceOrderHandler>(a => a.Describe("Places an order").RequireConfirmation(false))
        .AddQuery<FindOrder, FindOrderHandler>(a => a.Describe("Reads one order").Category("Orders"));

    private static void Services(IServiceCollection s)
    {
        s.AddSingleton<OrderBook>();
        s.AddScoped<WithdrawOrderHandler>();
        s.AddScoped<PlaceOrderHandler>();
        s.AddScoped<FindOrderHandler>();
        s.AddScoped<ShippingHandlers>();
        s.AddScoped<FluentActionTests.StockActions>();
    }

    private static MentorHostFixture Host(Action<MentorOptions> configure, Action<IServiceCollection>? services = null)
        => MentorHostFixture.Create(o =>
        {
            o.MemoryAutoCapture = false;
            configure(o);
        }, s =>
        {
            Services(s);
            services?.Invoke(s);
        });

    private static Task FlushAsync(MentorHostFixture host) => host.Get<MentorActionDecisions>().FlushAsync();

    private static IEnumerable<FunctionResultContent> Results(MentorHostFixture host)
        => host.Chat!.Calls.SelectMany(c => c.Messages).SelectMany(m => m.Contents).OfType<FunctionResultContent>();

    // ── fluent.command, fluent.query: in the catalogue, with the type's properties as parameters ─────────────

    [Fact]
    [Trait("Rule", "fluent.command")]
    [Trait("Rule", "fluent.query")]
    [Trait("Rule", "fluent.message-trace")]
    public async Task A_command_and_a_query_are_actions_of_the_coordinator_named_after_their_type()
    {
        await using var host = Host(o => Register(o));
        var catalog = host.Get<IMentorActionCatalog>();
        var actions = catalog.GetActions();

        var cancel = actions.Single(a => a.ToolName == "withdraw_order");
        cancel.AgentKind.Should().Be(MentorAgentKind.Coordinator);
        cancel.Origin.Should().Be(MentorActionOrigins.Command);
        cancel.RegistrationId.Should().Be("command:" + typeof(WithdrawOrder).FullName);
        cancel.ReadOnly.Should().BeFalse("a command is a write");
        cancel.RequiresConfirmation.Should().BeTrue("confirm.default-for-writes applies to a command");
        cancel.RequiredRoles.Should().Equal("Manager");
        cancel.Method.Should().NotBeNull();
        cancel.Method!.DeclaringType.Should().Be(typeof(WithdrawOrderHandler));
        cancel.Method.Name.Should().Be("HandleAsync");

        var find = actions.Single(a => a.ToolName == "find_order");
        find.Origin.Should().Be(MentorActionOrigins.Query);
        find.ReadOnly.Should().BeTrue("a query is always read-only");
        find.RequiresConfirmation.Should().BeFalse();
        find.Method!.DeclaringType.Should().Be(typeof(FindOrderHandler));

        var registrations = catalog.GetRegistrations();
        registrations.Single(r => r.Id == "query:" + typeof(FindOrder).FullName)
            .Should().Match<MentorActionRegistration>(r => r.Origin == MentorActionOrigins.Query
                && r.Source == typeof(FindOrder).FullName && r.Actions.Count == 1 && r.Actions[0] == "find_order");
        registrations.Single(r => r.Id == "command:" + typeof(PlaceOrder).FullName).Actions.Should().Equal("place_order");
    }

    [Fact]
    [Trait("Rule", "fluent.command")]
    public async Task The_tool_reads_the_types_properties_as_its_parameters()
    {
        await using var host = Host(o => Register(o));
        host.Auth.SignIn("ada", "Manager");
        host.Chat!.Say("Nothing to do.");

        (await host.RunTurnAsync("hello")).Should().BeNull();

        var tool = host.Chat.Calls[0].Tools.Single(t => t.Name == "withdraw_order");
        tool.Description.Should().Be("Withdraws an order that has not shipped");
        var schema = tool.JsonSchema;
        schema.GetProperty("type").GetString().Should().Be("object");
        schema.GetProperty("properties").EnumerateObject().Select(p => p.Name.ToLowerInvariant())
            .Should().Equal(["orderid", "reason"], "the command's properties, not one parameter named after the command");
        schema.GetProperty("properties").EnumerateObject().First().Value.GetProperty("type").GetString().Should().Be("integer");
        schema.GetProperty("properties").EnumerateObject().Last().Value.GetProperty("description").GetString()
            .Should().Be("Why the order is withdrawn", "a property's [Description] is the parameter's");
    }

    [Fact]
    [Trait("Rule", "fluent.query")]
    public async Task A_query_runs_without_a_question_and_the_model_reads_its_result()
    {
        var observer = new RecordingActionObserver();
        await using var host = Host(o => Register(o), s => s.AddMentorActionObserver(observer));
        host.Chat!.CallTool("find_order", new { orderId = 7 }).Say("Order 7 has shipped.");
        var asked = 0;
        host.Get<IMentorStateService>().OnConfirmationRequired += _ => Interlocked.Increment(ref asked);

        (await host.RunTurnAsync("where is order 7?")).Should().BeNull();
        await FlushAsync(host);

        host.Get<OrderBook>().Log.Should().Equal("find 7");
        asked.Should().Be(0);
        observer.Steps("find_order").Should().Equal("Allowed/allowed", "Executed/completed");
        Results(host).Single().Result?.ToString().Should().Contain("shipped-on-monday");
    }

    [Fact]
    [Trait("Rule", "fluent.command")]
    public async Task A_command_with_a_result_gives_it_to_the_model()
    {
        await using var host = Host(o => Register(o));
        host.Chat!.CallTool("place_order", new { product = "lamp", quantity = 2 }).Say("Placed.");

        (await host.RunTurnAsync("order two lamps")).Should().BeNull();

        host.Get<OrderBook>().Log.Should().Equal("place 2 lamp");
        Results(host).Single().Result?.ToString().Should().Contain("4242");
    }

    // ── Roles and confirmation, in both modes ────────────────────────────────────────────────────────────

    [Theory]
    [Trait("Rule", "fluent.command")]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task A_command_asks_for_its_confirmation_with_its_arguments_in_the_types_order_and_runs(MentorHitlMode mode)
    {
        var observer = new RecordingActionObserver();
        await using var host = Host(o => { Register(o); o.HitlMode = mode; }, s => s.AddMentorActionObserver(observer));
        host.Auth.SignIn("ada", "Manager");
        // The model gives the arguments in another order than the command declares them.
        host.Chat!.CallTool("withdraw_order", new { reason = "late", orderId = 7 }).Say("Cancelled.");
        var state = host.Get<IMentorStateService>();
        var banners = new List<string>();
        state.OnConfirmationRequired += req =>
        {
            lock (banners) banners.Add(req.Message);
            _ = state.ConfirmAsync(req.ActionId);
        };

        await host.RunTurnAsync("withdraw order 7, it is late");
        await FlushAsync(host);

        host.Get<OrderBook>().Log.Should().Equal("withdraw 7 late");
        observer.Steps("withdraw_order").Should().Equal(
            ["Allowed/allowed", "ConfirmationRequested/operator", "Confirmed/operator", "Executed/completed"], "mode {0}", mode);
        var banner = banners.Should().ContainSingle().Which;
        banner.IndexOf("Order Id", StringComparison.Ordinal).Should().BeGreaterThan(-1)
            .And.BeLessThan(banner.IndexOf("Reason", StringComparison.Ordinal), "the banner follows the command's properties");
    }

    [Theory]
    [Trait("Rule", "fluent.command")]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task A_command_is_refused_to_a_user_without_its_role(MentorHitlMode mode)
    {
        var observer = new RecordingActionObserver();
        await using var host = Host(o => { Register(o); o.HitlMode = mode; }, s => s.AddMentorActionObserver(observer));
        host.Auth.SignIn("bob");
        host.Chat!.CallTool("withdraw_order", new { orderId = 7, reason = "late" }).Say("I cannot.");
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req => state.Cancel(req.ActionId);

        await host.RunTurnAsync("withdraw order 7");
        await FlushAsync(host);

        observer.Steps("withdraw_order").Should().Equal(["Denied/role-missing"], "mode {0}", mode);
        host.Get<OrderBook>().Log.Should().BeEmpty();
    }

    [Fact]
    [Trait("Rule", "fluent.command")]
    public async Task Arguments_that_do_not_make_the_command_fail_the_call_and_the_handler_does_not_run()
    {
        await using var host = Host(o => Register(o));
        host.Chat!.CallTool("place_order", new { product = "lamp", quantity = "many" }).Say("It failed.");

        (await host.RunTurnAsync("order many lamps")).Should().BeNull();

        host.Get<OrderBook>().Log.Should().BeEmpty();
        Results(host).Should().ContainSingle();
    }

    [Fact]
    [Trait("Rule", "fluent.command")]
    [Trait("Rule", "fluent.query")]
    public async Task A_handler_written_as_explicit_implementations_runs_each_of_its_messages()
    {
        await using var host = Host(o => o.Actions
            .AddCommand<ShipOrder, ShippingHandlers>(a => a.Describe("Ships an order").RequireConfirmation(false))
            .AddQuery<TrackOrder, ShippingHandlers>(a => a.Describe("Tracks an order").Name("where_is")));
        host.Chat!.CallTool("ship_order", new { orderId = 3 }).CallTool("where_is", new { orderId = 3 }).Say("Done.");

        (await host.RunTurnAsync("ship order 3 and tell me where it is")).Should().BeNull();

        host.Get<OrderBook>().Log.Should().Equal("ship 3", "track 3");
        Results(host).Select(r => r.Result?.ToString() ?? "").Should().Contain(r => r.Contains("in-transit-near-turin"));
        host.Get<IMentorActionCatalog>().GetActions().Should().Contain(a => a.ToolName == "where_is" && a.ReadOnly);
    }

    // ── Policies, the MCP server, the A2A card, a team ──────────────────────────────────────────────────

    [Fact]
    [Trait("Rule", "fluent.command")]
    public async Task The_policies_decide_a_commands_offer_as_any_actions()
    {
        var policy = new TestActionPolicy { Allowed = r => r.ToolName != "place_order" };
        await using var host = Host(o => Register(o), s => s.AddMentorActionPolicy(policy));
        host.Chat!.Say("Nothing.");

        (await host.RunTurnAsync("hello")).Should().BeNull();

        host.FirstToolsSent.Should().Contain("find_order").And.NotContain("place_order");
        policy.Seen.Should().Contain(s => s.Request.ToolName == "place_order" && s.Request.Stage == MentorActionStage.Offer
                                          && s.Request.Kind == MentorActionKind.Server);
    }

    [Fact]
    [Trait("Rule", "fluent.query")]
    [Trait("Rule", "surface.mcp-withholds-gated")]
    public async Task On_the_mcp_server_a_query_is_published_and_answers_a_command_that_asks_is_not()
    {
        await using var app = await ServerAppFactory.CreateAsync(
            userId: "ada",
            configure: o =>
            {
                o.McpServerEnabled = true;
                o.MemoryAutoCapture = false;
                Register(o);
            },
            configureServices: s =>
            {
                Services(s);
                s.AddHttpContextAccessor();
                s.AddOptions<MentorOptions>().Configure<IHttpContextAccessor>(
                    (o, http) => o.McpCallerPrincipal = () => http.HttpContext?.User);
            },
            mapEndpoints: e => e.MapMentorAgentMcp());
        var ada = await McpTestSession.OpenAsync(app.Client);

        var listed = await ada.ListToolNamesAsync();
        listed.Should().Contain(["find_order", "place_order"]).And.NotContain("withdraw_order", "it requires a role and asks");
        var call = await ada.CallAsync("find_order", new { orderId = 9 });
        call.IsError.Should().BeFalse(call.Text);
        call.Text.Should().Contain("shipped-on-monday");
    }

    [Fact]
    [Trait("Rule", "fluent.query")]
    public async Task On_the_agent_card_a_query_is_a_skill_as_an_action_registered_in_code_is()
    {
        await using var app = await ServerAppFactory.CreateAsync(
            configure: o =>
            {
                o.A2AServerEnabled = true;
                o.MemoryAutoCapture = false;
                Register(o);
            },
            configureServices: Services,
            configureApp: a => a.UseEndpoints(e => e.MapMentorAgentA2A()));

        using var card = JsonDocument.Parse(await app.Client.GetStringAsync("/.well-known/agent-card.json"));
        var skills = card.RootElement.GetProperty("skills").EnumerateArray().Select(s => s.GetProperty("id").GetString()).ToList();
        skills.Should().Contain(["find_order", "place_order"]).And.NotContain("withdraw_order", "a gated action is not on the card");
    }

    [Fact]
    [Trait("Rule", "fluent.query")]
    [Trait("Rule", "team.member-tools")]
    public async Task A_teams_member_may_call_a_query_through_the_gate()
    {
        var observer = new RecordingActionObserver();
        var team = new MentorTeamDefinition
        {
            Name = "Order Review",
            Description = "Reviews an order before anything is done with it.",
            MaxIterations = 4,
            Approver = "Approver",
            Members =
            [
                new MentorTeamMemberDefinition { Role = "Clerk", Instructions = "Look at the order.", Tools = ["find_order"] },
                new MentorTeamMemberDefinition { Role = "Approver", Instructions = "Decide. Reply with the verdict." },
            ],
        };
        await using var host = Host(o => Register(o), s =>
        {
            s.AddSingleton<IMentorTeamSource>(new TeamSource(team));
            s.AddMentorActionObserver(observer);
        });
        host.Chat!.Respond((messages, options) =>
        {
            var tools = options?.Tools?.Select(t => t.Name).ToList() ?? [];
            var answered = messages.LastOrDefault()?.Contents.OfType<FunctionResultContent>().Any() == true;
            var instructions = options?.Instructions ?? "";
            if (instructions.Contains("Your role in the team is: **Clerk**", StringComparison.Ordinal))
                return answered ? Reply(host, "The order has shipped.") : Call(host, "find_order", new() { ["orderId"] = 5 });
            if (instructions.Contains("Your role in the team is: **Approver**", StringComparison.Ordinal))
                return Reply(host, "Fine. [TEAM:APPROVED]");
            if (tools.Contains("order_review"))
                return answered ? Reply(host, "relayed") : Call(host, "order_review", new() { ["task"] = "review order 5" });
            return Reply(host, "SAFE");
        });

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        (await host.RunTurnAsync("review order 5", deadline.Token)).Should().BeNull("the turn ran. Warnings: {0}", host.Logs.WarningText);
        await FlushAsync(host);

        host.Get<OrderBook>().Log.Should().Equal("find 5");
        observer.Seen.Where(d => d.ToolName == "find_order").Should().NotBeEmpty().And.OnlyContain(d => d.AgentName == "Clerk");
    }

    private sealed class TeamSource(params MentorTeamDefinition[] teams) : IMentorTeamSource
    {
        public Task<MentorTeamSourceResult> LoadAsync(MentorTeamSourceContext context, CancellationToken ct = default)
            => Task.FromResult(new MentorTeamSourceResult(teams, []));
    }

    private static ChatResponse Reply(MentorHostFixture host, string text)
        => new(new ChatMessage(ChatRole.Assistant, text)) { ModelId = host.Chat!.ModelId };

    private static ChatResponse Call(MentorHostFixture host, string name, Dictionary<string, object?> args)
        => new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString("N"), name, args)]))
        {
            ModelId = host.Chat!.ModelId,
        };

    // ── Name, on a method too ────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Rule", "fluent.coordinator-action")]
    public async Task Name_gives_a_method_registered_in_code_the_name_the_model_calls_it_by()
    {
        await using var host = Host(o => o.Actions.Add<FluentActionTests.StockActions>(
            nameof(FluentActionTests.StockActions.CountLowStock), a => a.Describe("Counts").ReadOnly().Name("low_stock")));

        host.Get<IMentorActionCatalog>().GetActions().Should().Contain(a => a.ToolName == "low_stock")
            .And.NotContain(a => a.ToolName == "count_low_stock");
    }

    // ── fluent.message-checked: mistakes stop the application when it starts ───────────────────────────────

    public static TheoryData<string, string> Mistakes => new()
    {
        { "string", "*the command String is not a concrete class or struct*" },
        { "collection", "*the command List`1 is a collection*" },
        { "no-handler", "*the command WithdrawOrder has no handler: FindOrderHandler does not implement IMentorCommandHandler<WithdrawOrder>*" },
        { "query-of-a-command", "*the query WithdrawOrder has no handler: WithdrawOrderHandler does not implement IMentorQueryHandler<WithdrawOrder, TResult>*" },
        { "twice", "*TwiceHandler implements*2 times*" },
        { "abstract", "*AbstractHandler, the handler of the command WithdrawOrder, is not a concrete class*" },
        { "no-description", "*the command WithdrawOrder has no description*" },
        { "read-only", "*the command WithdrawOrder is declared ReadOnly*AddQuery*" },
        { "bad-name", "*the query FindOrder would be called 'find order', which is not a tool name*" },
    };

    [Theory]
    [Trait("Rule", "fluent.message-checked")]
    [MemberData(nameof(Mistakes))]
    public async Task A_command_or_a_query_that_cannot_be_sent_is_refused(string mistake, string says)
    {
        await using var host = Host(o =>
        {
            _ = mistake switch
            {
                "string" => o.Actions.AddCommand<string, WithdrawOrderHandler>(a => a.Describe("x")),
                "collection" => o.Actions.AddCommand<List<int>, WithdrawOrderHandler>(a => a.Describe("x")),
                "no-handler" => o.Actions.AddCommand<WithdrawOrder, FindOrderHandler>(a => a.Describe("x")),
                "query-of-a-command" => o.Actions.AddQuery<WithdrawOrder, WithdrawOrderHandler>(a => a.Describe("x")),
                "twice" => o.Actions.AddCommand<WithdrawOrder, TwiceHandler>(a => a.Describe("x")),
                "abstract" => o.Actions.AddCommand<WithdrawOrder, AbstractHandler>(a => a.Describe("x")),
                "no-description" => o.Actions.AddCommand<WithdrawOrder, WithdrawOrderHandler>(a => a.RequireRoles("Manager")),
                "read-only" => o.Actions.AddCommand<WithdrawOrder, WithdrawOrderHandler>(a => a.Describe("x").ReadOnly()),
                "bad-name" => o.Actions.AddQuery<FindOrder, FindOrderHandler>(a => a.Describe("x").Name("find order")),
                _ => throw new ArgumentOutOfRangeException(nameof(mistake)),
            };
        });

        host.Invoking(h => h.Options).Should().Throw<ArgumentException>().WithMessage(says);
    }

    [Fact]
    [Trait("Rule", "fluent.message-checked")]
    public async Task A_type_registered_twice_is_refused()
    {
        await using var host = Host(o => o.Actions
            .AddCommand<WithdrawOrder, WithdrawOrderHandler>(a => a.Describe("Cancels"))
            .AddCommand<WithdrawOrder, WithdrawOrderHandler>(a => a.Describe("Cancels again")));

        host.Invoking(h => h.Options).Should().Throw<InvalidOperationException>()
            .WithMessage("*Actions registers WithdrawOrder 2 times, as a command or a query*");
    }

    [Fact]
    [Trait("Rule", "fluent.message-checked")]
    public async Task A_name_another_registration_has_is_refused()
    {
        await using var host = Host(o => o.Actions
            .Add<FluentActionTests.StockActions>(nameof(FluentActionTests.StockActions.Restock), a => a.Describe("Restocks"))
            .AddQuery<FindOrder, FindOrderHandler>(a => a.Describe("Reads").Name("restock")));

        host.Invoking(h => h.Options).Should().Throw<InvalidOperationException>()
            .WithMessage("*Actions registers 2 actions named restock: StockActions.Restock, the query FindOrder*");
    }
}
