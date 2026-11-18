using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Attributes;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Novior.MentorAgent.Tests.Discovery;

/// <summary>
/// Story 1285 — an action registered in code, without attributes (<see cref="MentorOptions.Actions"/>), is one of the
/// coordinator's actions like a <c>[MentorAction]</c> method: in the catalogue, offered to the model, run when it is
/// called, with its roles and its confirmation in both HITL modes, on the MCP server by the same rules. A registration
/// that names no usable method, has no description or registers a method twice stops the application when it starts.
/// </summary>
public sealed class FluentActionTests
{
    /// <summary>The application's class, registered in code only. It lives in the test assembly, which no host scans.</summary>
    public sealed class StockActions
    {
        internal int Counted { get; private set; }
        internal List<int> Restocked { get; } = [];

        public int CountLowStock() => ++Counted;
        public string Restock(int productId)
        {
            Restocked.Add(productId);
            return $"Product {productId} restocked.";
        }
        public string PriceOf(int productId) => $"Product {productId} costs 9.90.";
        public string Overloaded(int productId) => "";
        public string Overloaded(string code) => "";
        public static string Shared() => "";
        public T Generic<T>() => default!;
        [MentorAction(Description = "Archives a product")]
        public string Archive(int productId) => "";
    }

    public abstract class AbstractActions
    {
        public string Run() => "";
    }

    private static void RegisterStock(MentorOptions o) => o.Actions
        .Add<StockActions>(nameof(StockActions.CountLowStock), a => a.Describe("Counts the products low on stock").ReadOnly().Category("Stock"))
        .Add<StockActions>(nameof(StockActions.Restock), a => a.Describe("Restocks a product").RequireRoles("Manager").RequireConfirmation())
        .Add<StockActions>(nameof(StockActions.PriceOf), a => a.Describe("Reads the price of a product").ReadOnly().Off());

    private static MentorHostFixture Host(Action<MentorOptions> configure, RecordingActionObserver? observer = null)
        => MentorHostFixture.Create(o =>
        {
            o.MemoryAutoCapture = false;
            configure(o);
        }, s =>
        {
            s.AddScoped<StockActions>();
            if (observer is not null) s.AddMentorActionObserver(observer);
        });

    private static Task FlushAsync(MentorHostFixture host) => host.Get<MentorActionDecisions>().FlushAsync();

    // ── In the catalogue and offered, as an attribute action ────────────────────────────────────────

    [Fact]
    [Trait("Rule", "fluent.coordinator-action")]
    [Trait("Rule", "fluent.declared-decides")]
    public async Task A_registered_action_is_in_the_catalogue_with_what_it_declares()
    {
        await using var host = Host(RegisterStock);
        var actions = host.Get<IMentorActionCatalog>().GetActions();

        var count = actions.Single(a => a.ToolName == "count_low_stock");
        count.AgentKind.Should().Be(MentorAgentKind.Coordinator);
        count.Description.Should().Be("Counts the products low on stock");
        count.ReadOnly.Should().BeTrue();
        count.RequiresConfirmation.Should().BeFalse();
        count.RequiredRoles.Should().BeEmpty();
        count.EnabledByDefault.Should().BeTrue();

        var restock = actions.Single(a => a.ToolName == "restock");
        restock.RequiredRoles.Should().Equal("Manager");
        restock.RequiresConfirmation.Should().BeTrue();
        restock.ReadOnly.Should().BeFalse();

        actions.Single(a => a.ToolName == "price_of").EnabledByDefault.Should().BeFalse("Off() ships it off");
        actions.Should().Contain(a => a.ToolName == "count_open_orders", "the attribute actions are still there, side by side");
        actions.Should().NotContain(a => a.ToolName == "overloaded" || a.ToolName == "archive", "only what is registered");
    }

    [Fact]
    [Trait("Rule", "fluent.coordinator-action")]
    public async Task A_registered_action_is_offered_and_runs_when_the_model_calls_it()
    {
        var observer = new RecordingActionObserver();
        await using var host = Host(RegisterStock, observer);
        host.Chat!.CallTool("count_low_stock").Say("One product is low on stock.");

        (await host.RunTurnAsync("which products are low on stock?")).Should().BeNull();
        await FlushAsync(host);

        host.FirstToolsSent.Should().Contain(["count_low_stock", "count_open_orders"])
            .And.NotContain("price_of", "an action shipped off is offered nowhere")
            .And.NotContain("restock", "nobody is signed in, and it requires Manager: roles.offered-when-allowed");
        host.Get<StockActions>().Counted.Should().Be(1);
        observer.Steps("count_low_stock").Should().Equal("Allowed/allowed", "Executed/completed");
        observer.Seen.Single(d => d.Outcome == MentorActionOutcome.Executed).DisplayName
            .Should().Be("Counts the products low on stock", "the text shown is the registered description");
    }

    // ── Roles and confirmation, in both modes ───────────────────────────────────────────────────────

    [Theory]
    [Trait("Rule", "fluent.declared-decides")]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task A_registered_action_is_refused_to_a_user_without_its_role(MentorHitlMode mode)
    {
        var observer = new RecordingActionObserver();
        await using var host = Host(o => { RegisterStock(o); o.HitlMode = mode; }, observer);
        host.Auth.SignIn("ada");
        host.Chat!.CallTool("restock", new { productId = 7 }).Say("I could not restock it.");
        // Were the role not checked, the call would ask for a confirmation: answer no, so the test fails instead of waiting.
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req => state.Cancel(req.ActionId);

        await host.RunTurnAsync("restock product 7");
        await FlushAsync(host);

        observer.Steps("restock").Should().Equal(["Denied/role-missing"], "mode {0}", mode);
        host.Get<StockActions>().Restocked.Should().BeEmpty();
    }

    [Theory]
    [Trait("Rule", "fluent.declared-decides")]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task A_registered_action_that_asks_runs_once_it_is_confirmed(MentorHitlMode mode)
    {
        var observer = new RecordingActionObserver();
        await using var host = Host(o => { RegisterStock(o); o.HitlMode = mode; }, observer);
        host.Auth.SignIn("ada", "Manager");
        host.Chat!.CallTool("restock", new { productId = 7 }).Say("Restocked.");
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req => _ = state.ConfirmAsync(req.ActionId);

        await host.RunTurnAsync("restock product 7");
        await FlushAsync(host);

        observer.Steps("restock").Should().Equal(
            ["Allowed/allowed", "ConfirmationRequested/operator", "Confirmed/operator", "Executed/completed"], "mode {0}", mode);
        host.Get<StockActions>().Restocked.Should().Equal(7);
    }

    [Theory]
    [Trait("Rule", "fluent.declared-decides")]
    [InlineData(null, true, true)]
    [InlineData(null, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    public async Task Without_a_declared_confirmation_a_write_follows_the_default_for_writes(
        bool? declared, bool defaultForWrites, bool asks)
    {
        await using var host = Host(o =>
        {
            o.RequireConfirmationForWrites = defaultForWrites;
            o.Actions.Add<StockActions>(nameof(StockActions.Restock), a =>
            {
                a.Describe("Restocks a product");
                if (declared is { } value) a.RequireConfirmation(value);
            });
        });

        host.Get<IMentorActionCatalog>().GetActions().Single(a => a.ToolName == "restock")
            .RequiresConfirmation.Should().Be(asks);
    }

    [Fact]
    [Trait("Rule", "fluent.coordinator-action")]
    public async Task A_method_with_attributes_in_an_assembly_nobody_scans_can_be_registered_with_its_own_words()
    {
        // The attribute does nothing outside ScanAssemblies: the registration is the only place the action is declared.
        await using var host = Host(o => o.Actions.Add<StockActions>(nameof(StockActions.Archive), a => a.Describe("Moves a product to the archive")));

        host.Get<IMentorActionCatalog>().GetActions().Single(a => a.ToolName == "archive")
            .Description.Should().Be("Moves a product to the archive");
    }

    // ── Mistakes stop the application when it starts ─────────────────────────────────────────────────

    [Theory]
    [Trait("Rule", "fluent.method-must-exist")]
    [InlineData("Missing", "declares no public method Missing")]
    [InlineData(nameof(StockActions.Overloaded), "declares 2 public methods named Overloaded")]
    [InlineData(nameof(StockActions.Shared), "Shared is static")]
    [InlineData(nameof(StockActions.Generic), "Generic is generic")]
    public async Task A_registration_that_names_no_usable_method_is_refused(string method, string says)
    {
        await using var host = Host(o => o.Actions.Add<StockActions>(method, a => a.Describe("Something")));
        host.Invoking(h => h.Options).Should().Throw<ArgumentException>().WithMessage($"*StockActions*{says}*");
    }

    [Fact]
    [Trait("Rule", "fluent.method-must-exist")]
    public async Task A_registration_on_an_abstract_class_is_refused()
    {
        await using var host = Host(o => o.Actions.Add<AbstractActions>(nameof(AbstractActions.Run), a => a.Describe("Runs")));
        host.Invoking(h => h.Options).Should().Throw<ArgumentException>().WithMessage("*AbstractActions is not a concrete class*");
    }

    [Fact]
    [Trait("Rule", "fluent.description-required")]
    public async Task A_registration_without_a_description_is_refused()
    {
        await using var host = Host(o => o.Actions.Add<StockActions>(nameof(StockActions.Restock), a => a.RequireConfirmation()));
        host.Invoking(h => h.Options).Should().Throw<ArgumentException>().WithMessage("*StockActions.Restock has no description*");
    }

    [Fact]
    [Trait("Rule", "fluent.no-double")]
    public async Task A_method_that_is_already_an_action_by_its_attributes_is_refused()
    {
        await using var host = Host(o => o.Actions.Add<TestOrderService>(nameof(TestOrderService.CountOpenOrders), a => a.Describe("Counts")));
        host.Invoking(h => h.Options).Should().Throw<InvalidOperationException>()
            .WithMessage("*TestOrderService.CountOpenOrders is registered twice*[MentorAction]*ScanAssemblies*");
    }

    [Fact]
    [Trait("Rule", "fluent.no-double")]
    public async Task A_method_registered_twice_is_refused()
    {
        await using var host = Host(o => o.Actions
                .Add<StockActions>(nameof(StockActions.Restock), a => a.Describe("Restocks"))
                .Add<StockActions>(nameof(StockActions.Restock), a => a.Describe("Restocks again")));
        host.Invoking(h => h.Options).Should().Throw<InvalidOperationException>()
            .WithMessage("*Actions registers StockActions.Restock 2 times*");
    }

    // ── The MCP server ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Rule", "fluent.coordinator-action")]
    [Trait("Rule", "surface.mcp-withholds-gated")]
    public async Task On_the_mcp_server_a_registered_action_is_published_by_the_same_rules()
    {
        await using var app = await ServerAppFactory.CreateAsync(
            userId: "ada",
            configure: o =>
            {
                o.McpServerEnabled = true;
                o.MemoryAutoCapture = false;
                RegisterStock(o);
            },
            configureServices: s =>
            {
                s.AddScoped<StockActions>();
                s.AddHttpContextAccessor();
                s.AddOptions<MentorOptions>().Configure<IHttpContextAccessor>(
                    (o, http) => o.McpCallerPrincipal = () => http.HttpContext?.User);
            },
            mapEndpoints: e => e.MapMentorAgentMcp());
        var ada = await McpTestSession.OpenAsync(app.Client);

        var listed = await ada.ListToolNamesAsync();
        listed.Should().Contain("count_low_stock")
            .And.NotContain("restock", "it asks for confirmation")
            .And.NotContain("price_of", "it ships off");
        var call = await ada.CallAsync("count_low_stock");
        call.IsError.Should().BeFalse(call.Text);
    }
}

