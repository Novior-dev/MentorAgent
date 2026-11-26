using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Xunit;

namespace Novior.MentorAgent.Tests.Pages;

/// <summary>
/// Story 1639 (L5), slice A — the recipes of an <see cref="IMentorRecipeSource"/>, as a bridge gives those a console
/// approved in its signed document: asked once for a session, each a tool on its page only, checked as one added in code,
/// and what does not hold left out with its reason, never a tool. A recipe is governed as a UI action of its page: the
/// policies answer for it by page and name when it is offered and when it is called, so a policy that refuses everything
/// (the AI switched off, every action off) keeps it from being offered and from running. Whether the document was signed
/// is the bridge's to check: the library sees the source only.
/// </summary>
public sealed class RecipeSourceTests
{
    private static MentorRecipeDefinition CreateOrder(string route = "/orders/new", string name = "create_order") => new()
    {
        Name = name,
        Description = "Creates an order for a customer",
        Route = route,
        Parameters =
        [
            new() { Name = "customer", Description = "The customer's name" },
            new() { Name = "quantity", Description = "How many", Type = MentorRecipeParameterType.Number },
        ],
        Steps =
        [
            new() { Kind = MentorRecipeStepKind.Set, Selector = "#customer", Value = "{customer}" },
            new() { Kind = MentorRecipeStepKind.Set, Selector = "#quantity", Value = "{quantity}" },
            new() { Kind = MentorRecipeStepKind.Set, Selector = "#note", Value = "{{draft}} for {{customer}}" },
            new() { Kind = MentorRecipeStepKind.Click, Selector = "#save" },
        ],
    };

    private static readonly object Order = new { customer = "Anna", quantity = 3 };

    /// <summary>A source the test fills; it keeps every context it is asked with.</summary>
    internal class Source : IMentorRecipeSource
    {
        internal List<MentorRecipeDefinition> Recipes { get; set; } = [];
        internal List<MentorRecipeRejection> Rejected { get; } = [];
        internal ConcurrentQueue<MentorRecipeSourceContext> Asked { get; } = new();
        internal Exception? Throws { get; set; }
        internal MentorRecipePrecedence Precedence { get; set; }

        public Task<MentorRecipeSourceResult> LoadAsync(MentorRecipeSourceContext context, CancellationToken ct = default)
        {
            Asked.Enqueue(context);
            if (Throws is not null) throw Throws;
            return Task.FromResult(new MentorRecipeSourceResult([.. Recipes], [.. Rejected]) { Precedence = Precedence });
        }
    }

    /// <summary>A second source, of another type: a source is registered once per type.</summary>
    internal sealed class OtherSource : Source;

    /// <summary>Keeps the recipe reports.</summary>
    internal sealed class ReportObserver : IMentorAgentSourceObserver
    {
        internal ConcurrentQueue<MentorRecipeSourceReport> Reports { get; } = new();
        public void OnAgentsLoaded(MentorAgentSourceReport report) { }
        public void OnRecipesLoaded(MentorRecipeSourceReport report) => Reports.Enqueue(report);
    }

    private sealed class FakeRunner : IMentorRecipeRunner
    {
        internal List<IReadOnlyList<MentorRecipeStepCall>> Runs { get; } = [];

        public Task<MentorRecipeOutcome> RunAsync(IReadOnlyList<MentorRecipeStepCall> steps, CancellationToken ct)
        {
            Runs.Add(steps);
            return Task.FromResult(new MentorRecipeOutcome(true, steps.Count, null));
        }
    }

    private sealed record Setup(MentorHostFixture Host, FakeRunner Runner, RecordingActionObserver Decisions, ReportObserver Reports);

    private static Setup Host(Source source, TestActionPolicy? policy = null, Source? other = null, string page = "orders/new")
    {
        var runner = new FakeRunner();
        var decisions = new RecordingActionObserver();
        var reports = new ReportObserver();
        var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false, s =>
        {
            s.AddScoped<IMentorRecipeRunner>(_ => runner);
            s.AddMentorActionObserver(decisions);
            s.AddSingleton<IMentorAgentSourceObserver>(reports);
            s.TryAddEnumerable(ServiceDescriptor.Singleton<IMentorRecipeSource>(source));
            if (other is not null) s.TryAddEnumerable(ServiceDescriptor.Singleton<IMentorRecipeSource>(other));
            if (policy is not null) s.AddMentorActionPolicy(policy);
        });
        host.Get<IMentorPageContext>().SetPageName("Orders");
        host.Navigation.StartAt(page);
        return new(host, runner, decisions, reports);
    }

    private static Task FlushAsync(MentorHostFixture host) => host.Get<MentorActionDecisions>().FlushAsync();

    [Fact]
    [Trait("Rule", "recipe.from-source")]
    public async Task A_recipe_of_a_source_is_a_tool_on_its_page_only_and_runs_its_steps()
    {
        var source = new Source { Recipes = [CreateOrder()] };
        var (host, runner, decisions, reports) = Host(source);
        await using var _host = host;
        host.Chat!.CallTool("create_order", Order).Say("Done.").Say("Hello.");

        (await host.RunTurnAsync("order 3 for Anna")).Should().BeNull();
        await FlushAsync(host);

        host.Chat.Calls[0].ToolNames.Should().Contain("create_order");
        runner.Runs.Should().ContainSingle().Which.Should().Equal(
            new MentorRecipeStepCall("set", "#customer", "Anna"),
            new MentorRecipeStepCall("set", "#quantity", "3"),
            new MentorRecipeStepCall("set", "#note", "{draft} for {customer}", null, null, null),
            new MentorRecipeStepCall("click", "#save", null));
        decisions.Steps("create_order").Should().Equal(["Allowed/allowed", "Executed/completed"]);
        decisions.Seen.Should().OnlyContain(d => d.ToolName != "create_order" || (d.Kind == MentorActionKind.UI && d.PageName == "Orders"));
        reports.Reports.Should().ContainSingle().Which.Loaded.Should().ContainSingle().Which.Name.Should().Be("create_order");

        host.Navigation.StartAt("orders");
        (await host.RunTurnAsync("hello")).Should().BeNull();
        host.Chat.LastCall!.ToolNames.Should().NotContain("create_order", "a recipe is a tool on the page of its route only");
    }

    [Fact]
    [Trait("Rule", "recipe.session")]
    public async Task A_session_asks_its_sources_once_and_keeps_the_recipes_it_began_with()
    {
        var source = new Source { Recipes = [CreateOrder()] };
        var (host, _, _, _) = Host(source);
        await using var _host = host;
        host.Chat!.Say("Hello.").Say("Hello again.");

        (await host.RunTurnAsync("hello")).Should().BeNull();
        source.Recipes = [CreateOrder(name: "repeat_order")];
        (await host.RunTurnAsync("hello again")).Should().BeNull();

        source.Asked.Should().ContainSingle("a source is asked when the session begins");
        host.Chat.Calls[1].ToolNames.Should().Contain("create_order").And.NotContain("repeat_order",
            "an open conversation keeps the recipes it began with");
    }

    [Fact]
    [Trait("Rule", "recipe.from-source")]
    public async Task Two_pages_may_each_have_a_recipe_of_one_name()
    {
        var source = new Source
        {
            Recipes = [CreateOrder(route: "/orders/new"), CreateOrder(route: "/customers/{id:int}") with { Description = "Orders for this customer" }],
        };
        var (host, _, _, reports) = Host(source, page: "customers/7");
        await using var _host = host;
        host.Chat!.Say("Hello.");

        (await host.RunTurnAsync("hello")).Should().BeNull();

        reports.Reports.Single().Rejected.Should().BeEmpty("a recipe is known by its route and its name");
        host.Chat.Calls[0].Tools.Single(t => t.Name == "create_order").Description.Should().Be("Orders for this customer");
    }

    public static TheoryData<string, MentorRecipeDefinition> Broken() => new()
    {
        { MentorRecipeRejectionReasons.InvalidName, CreateOrder(name: "create order") },
        { MentorRecipeRejectionReasons.InvalidName, CreateOrder(name: new string('a', MentorRecipeLimits.MaxNameLength + 1)) },
        { MentorRecipeRejectionReasons.InvalidDefinition, CreateOrder() with { Description = " " } },
        { MentorRecipeRejectionReasons.TooLong, CreateOrder() with { Description = new string('d', MentorRecipeLimits.MaxDescriptionLength + 1) } },
        { MentorRecipeRejectionReasons.InvalidRoute, CreateOrder(route: "orders/new") },
        { MentorRecipeRejectionReasons.InvalidRoute, CreateOrder(route: "/orders/new?x=1") },
        { MentorRecipeRejectionReasons.InvalidSteps, CreateOrder() with { Steps = [] } },
        { MentorRecipeRejectionReasons.InvalidSteps, CreateOrder() with
            {
                Steps = [.. Enumerable.Repeat(new MentorRecipeStepDefinition { Kind = MentorRecipeStepKind.Click, Selector = "#save" },
                    MentorRecipeLimits.MaxSteps + 1)],
            } },
        { MentorRecipeRejectionReasons.InvalidSteps, CreateOrder() with
            { Steps = [new() { Kind = (MentorRecipeStepKind)42, Selector = "#save" }] } },
        { MentorRecipeRejectionReasons.InvalidDefinition, CreateOrder() with
            { Steps = [new() { Kind = MentorRecipeStepKind.Click, Selector = "" }] } },
        { MentorRecipeRejectionReasons.InvalidDefinition, CreateOrder() with
            { Steps = [new() { Kind = MentorRecipeStepKind.Row, Selector = "tbody tr", Value = "{customer}" }] } },
        { MentorRecipeRejectionReasons.TooLong, CreateOrder() with
            { Steps = [new() { Kind = MentorRecipeStepKind.Click, Selector = new string('s', MentorRecipeLimits.MaxSelectorLength + 1) }] } },
        { MentorRecipeRejectionReasons.TooLong, CreateOrder() with
            { Steps = [new() { Kind = MentorRecipeStepKind.Set, Selector = "#note", Value = new string('v', MentorRecipeLimits.MaxValueLength + 1) }] } },
        { MentorRecipeRejectionReasons.UnknownParameter, CreateOrder() with
            { Steps = [new() { Kind = MentorRecipeStepKind.Set, Selector = "#x", Value = "{nobody}" }] } },
        { MentorRecipeRejectionReasons.InvalidValue, CreateOrder() with
            { Steps = [new() { Kind = MentorRecipeStepKind.Set, Selector = "#x", Value = "a {b} c" }] } },
        { MentorRecipeRejectionReasons.InvalidValue, CreateOrder() with
            { Steps = [new() { Kind = MentorRecipeStepKind.Set, Selector = "#x", Value = "50%}" }] } },
        { MentorRecipeRejectionReasons.InvalidValue, CreateOrder() with
            { Steps = [new() { Kind = MentorRecipeStepKind.Set, Selector = "#x", Value = "{a customer}" }] } },
        { MentorRecipeRejectionReasons.InvalidParameters, CreateOrder() with
            {
                Parameters = [.. Enumerable.Range(0, MentorRecipeLimits.MaxParameters + 1)
                    .Select(i => new MentorRecipeParameterDefinition { Name = $"p{i}", Description = "A value" })],
            } },
        { MentorRecipeRejectionReasons.InvalidParameters, CreateOrder() with
            {
                Parameters = [new() { Name = "customer", Description = "One" }, new() { Name = "customer", Description = "Two" }],
            } },
        { MentorRecipeRejectionReasons.InvalidDefinition, CreateOrder() with
            { Parameters = [new() { Name = "customer", Description = "" }, new() { Name = "quantity", Description = "How many" }] } },
        { MentorRecipeRejectionReasons.TooLong, CreateOrder() with
            { Roles = [.. Enumerable.Range(0, MentorRecipeLimits.MaxRoles + 1).Select(i => $"Role{i}")] } },
        { MentorRecipeRejectionReasons.InvalidDefinition, CreateOrder() with { Roles = [""] } },
    };

    [Theory]
    [Trait("Rule", "recipe.rejected-with-reason")]
    [MemberData(nameof(Broken))]
    public async Task A_recipe_that_does_not_hold_is_left_out_with_its_reason_and_is_never_a_tool(string reason, MentorRecipeDefinition broken)
    {
        var source = new Source { Recipes = [broken, CreateOrder(name: "repeat_order")] };
        var (host, _, _, reports) = Host(source);
        await using var _host = host;
        host.Chat!.Say("Hello.");

        (await host.RunTurnAsync("hello")).Should().BeNull();

        var report = reports.Reports.Should().ContainSingle().Subject;
        report.Rejected.Should().ContainSingle().Which.ReasonCode.Should().Be(reason);
        report.Loaded.Select(d => d.Name).Should().Equal("repeat_order");
        host.Chat.Calls[0].ToolNames.Should().Contain("repeat_order", host.Logs.WarningText).And.NotContain("create_order")
            .And.NotContain("create order");
        host.Logs.HasWarning("recipe.rejected-with-reason").Should().BeTrue();
    }

    [Fact]
    [Trait("Rule", "recipe.rejected-with-reason")]
    public async Task The_first_source_keeps_a_route_and_a_name_and_one_that_throws_gives_nothing()
    {
        var first = new Source { Recipes = [CreateOrder()], Rejected = { new("/orders/new", "refund", "unknown_step", "A step of kind 'drag'.") } };
        var second = new OtherSource { Recipes = [CreateOrder() with { Description = "Another" }, CreateOrder(name: "repeat_order")] };
        var (host, _, _, reports) = Host(first, other: second);
        await using var _host = host;
        host.Chat!.Say("Hello.");

        (await host.RunTurnAsync("hello")).Should().BeNull();

        var all = reports.Reports.ToList();
        all.Should().HaveCount(2);
        all[0].Rejected.Should().ContainSingle().Which.ReasonCode.Should().Be("unknown_step", "what the source left out itself is reported");
        all[1].Rejected.Should().ContainSingle().Which.ReasonCode.Should().Be(MentorRecipeRejectionReasons.Duplicate);
        host.Chat.Calls[0].Tools.Single(t => t.Name == "create_order").Description.Should().Be("Creates an order for a customer");
        host.Chat.Calls[0].ToolNames.Should().Contain("repeat_order");

        var failing = new Source { Throws = new InvalidOperationException("secret detail") };
        var other = new OtherSource { Recipes = [CreateOrder(name: "repeat_order")] };
        var (host2, _, _, reports2) = Host(failing, other: other);
        await using var _host2 = host2;
        host2.Chat!.Say("Hello.");

        (await host2.RunTurnAsync("hello")).Should().BeNull();

        reports2.Reports.First().Rejected.Should().ContainSingle().Which.Should().Be(
            new MentorRecipeRejection("", typeof(Source).FullName!, MentorRecipeRejectionReasons.SourceFailed, nameof(InvalidOperationException)));
        host2.Chat.Calls[0].ToolNames.Should().Contain("repeat_order", "the other sources are still asked");
        host2.Logs.Records.Should().NotContain(e => e.Message.Contains("secret detail"), "only the exception's type is logged");
    }

    [Fact]
    [Trait("Rule", "recipe.governed")]
    public async Task A_policy_that_refuses_every_action_keeps_a_recipe_from_being_offered_or_run()
    {
        // The AI switched off, or every action off: the policies refuse everything, recipes too.
        var policy = new TestActionPolicy { Allowed = _ => false };
        var (host, runner, decisions, _) = Host(new Source { Recipes = [CreateOrder()] }, policy);
        await using var _host = host;
        host.Chat!.CallTool("create_order", Order).Say("I cannot.");

        (await host.RunTurnAsync("order 3 for Anna")).Should().BeNull();
        await FlushAsync(host);

        host.Chat.Calls[0].ToolNames.Should().NotContain("create_order");
        runner.Runs.Should().BeEmpty();
        decisions.Steps("create_order").Should().Equal(["Denied/not-offered"]);
    }

    [Fact]
    [Trait("Rule", "recipe.governed")]
    public async Task The_policies_answer_for_a_recipe_by_its_page_and_name_at_the_offer_and_at_the_call()
    {
        // As ui_rules do: the pair (page, name) switched off.
        bool Off(MentorActionRequest r) => r.Kind == MentorActionKind.UI && r.PageName == "Orders" && r.ToolName == "create_order";
        var offer = new TestActionPolicy { Allowed = r => !Off(r) };
        var (host, runner, _, _) = Host(new Source { Recipes = [CreateOrder(), CreateOrder(name: "repeat_order")] }, offer);
        await using (host)
        {
            host.Chat!.Say("Hello.");
            (await host.RunTurnAsync("hello")).Should().BeNull();
            host.Chat.Calls[0].ToolNames.Should().NotContain("create_order").And.Contain("repeat_order");
            offer.Seen.Where(s => s.Request.Stage == MentorActionStage.Offer && Off(s.Request)).Should().NotBeEmpty();
        }

        // Switched off between the offer and the call: the call does not run.
        var call = new TestActionPolicy { Allowed = r => r.Stage == MentorActionStage.Offer || !Off(r) };
        var (host2, runner2, decisions2, _) = Host(new Source { Recipes = [CreateOrder()] }, call);
        await using var _host2 = host2;
        host2.Chat!.CallTool("create_order", Order).Say("I cannot.");

        (await host2.RunTurnAsync("order 3 for Anna")).Should().BeNull();
        await FlushAsync(host2);

        host2.Chat.Calls[0].ToolNames.Should().Contain("create_order");
        runner.Runs.Should().BeEmpty();
        runner2.Runs.Should().BeEmpty();
        decisions2.Steps("create_order").Should().Equal(["Denied/not-authorized"]);
    }

    [Fact]
    [Trait("Rule", "recipe.governed")]
    public async Task A_confirmation_a_policy_adds_makes_a_recipe_wait_for_the_yes()
    {
        var policy = new TestActionPolicy
        {
            Verdict = r => r.ToolName == "create_order" ? MentorActionVerdict.Confirm() : MentorActionVerdict.Proceed,
        };
        var (host, runner, decisions, _) = Host(new Source { Recipes = [CreateOrder()] }, policy);
        await using var _host = host;
        var state = host.Get<IMentorStateService>();
        var runsWhileAsked = -1;
        state.OnConfirmationRequired += req =>
        {
            runsWhileAsked = runner.Runs.Count;
            _ = state.ConfirmAsync(req.ActionId);
        };
        host.Chat!.CallTool("create_order", Order).Say("Done.");

        (await host.RunTurnAsync("order 3 for Anna")).Should().BeNull();
        await FlushAsync(host);

        runsWhileAsked.Should().Be(0, "nothing runs before the yes");
        runner.Runs.Should().ContainSingle();
        decisions.Steps("create_order").Should().HaveCount(4).And.EndWith("Executed/completed");
    }

    private static int Said(MentorHostFixture host, string fragment)
        => host.Logs.Records.Count(r => r.Message.Contains(fragment, StringComparison.Ordinal));

    [Theory]
    [Trait("Rule", "recipe.precedence")]
    [InlineData(MentorRecipePrecedence.Code)]
    [InlineData(MentorRecipePrecedence.Source)]
    public async Task A_recipe_and_a_UI_action_of_one_name_on_a_page_one_wins_as_the_source_says(MentorRecipePrecedence precedence)
    {
        var (host, runner, _, _) = Host(new Source { Recipes = [CreateOrder()], Precedence = precedence });
        await using var _host = host;
        var handled = 0;
        host.Get<IMentorPageContext>().RegisterUIAction("create_order", "Opens the order form", _ => handled++);
        host.Chat!.CallTool("create_order", Order).Say("Done.").Say("Hello.");

        (await host.RunTurnAsync("order 3 for Anna")).Should().BeNull();
        (await host.RunTurnAsync("hello")).Should().BeNull();

        var offered = host.Chat.Calls[0].Tools.Where(t => t.Name == "create_order").Should().ContainSingle(
            "the model is never offered two tools of one name").Subject;
        if (precedence == MentorRecipePrecedence.Code)
        {
            offered.Description.Should().Be("Opens the order form", "the code wins by default");
            handled.Should().Be(1);
            runner.Runs.Should().BeEmpty();
            Said(host, "recipe.precedence: the recipe 'create_order' of '/orders/new' is not offered").Should().Be(1,
                "once for the session, not at every turn: " + host.Logs.WarningText);
        }
        else
        {
            offered.Description.Should().Be("Creates an order for a customer", "the source wins");
            handled.Should().Be(0);
            runner.Runs.Should().ContainSingle();
            Said(host, "recipe.precedence: the UI action 'create_order' of the page 'Orders' is not offered").Should().Be(1,
                "once for the session, not at every turn: " + host.Logs.WarningText);
        }
    }

    [Fact]
    [Trait("Rule", "recipe.precedence")]
    public async Task Any_other_tool_wins_over_a_recipe_even_when_the_source_wins_over_the_code()
    {
        // get_weather is an action of the test application.
        var (host, runner, _, _) = Host(new Source
        {
            Recipes = [CreateOrder(name: "get_weather")], Precedence = MentorRecipePrecedence.Source,
        });
        await using var _host = host;
        host.Chat!.Say("Hello.").Say("Hello again.");

        (await host.RunTurnAsync("hello")).Should().BeNull();
        (await host.RunTurnAsync("hello again")).Should().BeNull();

        host.Chat.Calls[0].Tools.Where(t => t.Name == "get_weather").Should().ContainSingle()
            .Which.Description.Should().NotBe("Creates an order for a customer");
        Said(host, "Recipe 'get_weather' conflicts with an existing tool").Should().Be(1, "once for the session");
        runner.Runs.Should().BeEmpty();
    }

    [Fact]
    [Trait("Rule", "recipe.element-missing")]
    public void A_recipe_turned_off_stays_off_in_its_version_and_a_new_version_is_on()
    {
        var registry = new MentorRecipeRegistry();
        var recipe = MentorRecipe.From(CreateOrder(), "console");
        registry.TurnOff(recipe, "missing");

        registry.For("/orders/new", [recipe]).Should().BeEmpty();
        registry.For("/orders/new", [MentorRecipe.From(CreateOrder(), "console")]).Should().BeEmpty(
            "the same recipe given again by a new session is the same version");
        registry.For("/orders/new", [MentorRecipe.From(CreateOrder(), "console", MentorRecipePrecedence.Source)]).Should()
            .BeEmpty("who wins over a UI action is not what the recipe does");
        registry.For("/orders/new", [MentorRecipe.From(CreateOrder() with
        {
            Steps = [new() { Kind = MentorRecipeStepKind.Click, Selector = "#save-order" }],
        }, "console")]).Should().ContainSingle("a new version, approved again, is on");
        registry.For("/orders/new", [MentorRecipe.From(CreateOrder() with { Description = "Creates an order" }, "console")])
            .Should().ContainSingle();
    }

    [Fact]
    [Trait("Rule", "recipe.from-source")]
    public void AddMentorRecipeSource_registers_a_source_once()
    {
        var services = new ServiceCollection();
        services.AddMentorRecipeSource<Source>().AddMentorRecipeSource<Source>().AddMentorRecipeSource<OtherSource>();

        services.Where(d => d.ServiceType == typeof(IMentorRecipeSource)).Select(d => d.ImplementationType)
            .Should().Equal(typeof(Source), typeof(OtherSource));
    }

    [Theory]
    [Trait("Rule", "recipe.shape")]
    [InlineData("plain", "plain")]
    [InlineData("{{", "{")]
    [InlineData("a}}b{{c", "a}b{c")]
    [InlineData("{{customer}}", "{customer}")]
    [InlineData("{", null)]
    [InlineData("a}b", null)]
    [InlineData("{{{", null)]
    public void A_text_writes_a_brace_doubled_and_refuses_a_single_one(string value, string? text)
        => MentorRecipeRegistry.Literal(value).Should().Be(text);
}
