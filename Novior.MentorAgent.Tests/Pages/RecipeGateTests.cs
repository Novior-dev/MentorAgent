using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Xunit;

namespace Novior.MentorAgent.Tests.Pages;

/// <summary>
/// Story 1638 (L4), slice A — a recipe of the page is one of its actions: offered on its page only, with its parameters
/// and never its selectors, gated with its own roles and its own confirmation, and turned off when the page changed
/// under it. The browser is played by a runner that records the steps; <see cref="RecipeBrowserTests"/> runs them in one.
/// </summary>
public sealed class RecipeGateTests
{
    private static MentorRecipe CreateOrder(string[]? roles = null, bool confirm = false) => new()
    {
        Name = "create_order",
        Description = "Creates an order for a customer",
        Route = "/orders/new",
        Parameters =
        [
            new("customer", "The customer's name"),
            new("quantity", "How many", MentorRecipeParameterType.Number),
            new("express", "Express shipping", MentorRecipeParameterType.Boolean),
        ],
        Steps =
        [
            new(MentorRecipeStepKind.Set, "#customer", "{customer}"),
            new(MentorRecipeStepKind.Set, "#quantity", "{quantity}"),
            new(MentorRecipeStepKind.Choose, "#country", "Italy"),
            new(MentorRecipeStepKind.Check, "#express", "{express}"),
            new(MentorRecipeStepKind.Click, "#save"),
        ],
        Roles = roles ?? [],
        RequiresConfirmation = confirm,
    };

    private static readonly object Order = new { customer = "Anna", quantity = 3, express = true };

    private sealed class FakeRunner : IMentorRecipeRunner
    {
        internal List<IReadOnlyList<MentorRecipeStepCall>> Runs { get; } = [];
        internal MentorRecipeOutcome Answer { get; set; } = new(true, 5, null);

        public Task<MentorRecipeOutcome> RunAsync(IReadOnlyList<MentorRecipeStepCall> steps, CancellationToken ct)
        {
            Runs.Add(steps);
            return Task.FromResult(Answer);
        }
    }

    private static (MentorHostFixture Host, FakeRunner Runner, RecordingActionObserver Observer) Host(
        MentorRecipe recipe, Action<MentorOptions>? configure = null, string page = "orders/new")
    {
        var runner = new FakeRunner();
        var observer = new RecordingActionObserver();
        var host = MentorHostFixture.Create(o =>
        {
            o.MemoryAutoCapture = false;
            configure?.Invoke(o);
        }, s =>
        {
            s.AddScoped<IMentorRecipeRunner>(_ => runner);
            s.AddMentorActionObserver(observer);
        });
        host.Get<MentorRecipeRegistry>().Add(recipe);
        host.Navigation.StartAt(page);
        return (host, runner, observer);
    }

    private static Task FlushAsync(MentorHostFixture host) => host.Get<MentorActionDecisions>().FlushAsync();

    /// <summary>What the model read back from the recipe's call.</summary>
    private static string? ToolResult(MentorHostFixture host)
        => host.Chat!.Calls.Skip(1).SelectMany(c => c.Messages).SelectMany(m => m.Contents).OfType<FunctionResultContent>()
            .LastOrDefault()?.Result?.ToString();

    [Fact]
    [Trait("Rule", "recipe.offer")]
    public async Task A_recipe_is_offered_on_its_page_only_with_its_parameters_and_never_its_selectors()
    {
        var (host, _, _) = Host(CreateOrder());
        await using var _host = host;
        host.Chat!.Say("Hello.").Say("Hello again.");

        (await host.RunTurnAsync("hello")).Should().BeNull();
        var offered = host.Chat.Calls[0].Tools.Single(t => t.Name == "create_order");
        offered.Description.Should().Be("Creates an order for a customer");
        var schema = offered.JsonSchema.GetRawText();
        schema.Should().Contain("\"customer\"").And.Contain("\"number\"").And.Contain("\"boolean\"");
        schema.Should().NotContain("#customer").And.NotContain("#save").And.NotContain("Italy", "the steps never reach the model");

        host.Navigation.StartAt("orders");
        (await host.RunTurnAsync("hello again")).Should().BeNull();
        host.Chat.Calls[1].ToolNames.Should().NotContain("create_order", "the recipe runs on its own page only");
    }

    [Fact]
    [Trait("Rule", "recipe.run")]
    [Trait("Rule", "recipe.gated")]
    public async Task A_recipe_runs_its_steps_with_the_models_values_and_the_gate_decides_it_as_a_UI_action()
    {
        var (host, runner, observer) = Host(CreateOrder());
        await using var _host = host;
        host.Chat!.CallTool("create_order", Order).Say("Done.");

        (await host.RunTurnAsync("order 3 for Anna, express")).Should().BeNull();
        await FlushAsync(host);

        runner.Runs.Should().ContainSingle().Which.Should().Equal(
            new MentorRecipeStepCall("set", "#customer", "Anna"),
            new MentorRecipeStepCall("set", "#quantity", "3"),
            new MentorRecipeStepCall("choose", "#country", "Italy"),
            new MentorRecipeStepCall("check", "#express", "true"),
            new MentorRecipeStepCall("click", "#save", null));
        observer.Steps("create_order").Should().Equal(["Allowed/allowed", "Executed/completed"]);
        observer.Seen.Should().OnlyContain(d => d.ToolName != "create_order" || d.Kind == MentorActionKind.UI);
    }

    [Fact]
    [Trait("Rule", "recipe.row")]
    public void A_row_step_takes_its_key_from_the_model_and_its_cell_target_and_filter_from_the_recipe()
    {
        var recipe = CreateOrder() with
        {
            Steps = [new(MentorRecipeStepKind.Row, "tbody tr", "{quantity}") { Cell = "td:nth-child(1)", Target = "button.open", Filter = "#filter" }],
        };

        MentorRecipeRegistry.Steps(recipe, new Dictionary<string, object?> { ["customer"] = "Anna", ["quantity"] = 42, ["express"] = false })
            .Should().Equal(new MentorRecipeStepCall("row", "tbody tr", "42", "td:nth-child(1)", "button.open", "#filter"));
    }

    [Fact]
    [Trait("Rule", "recipe.gated")]
    public async Task A_recipe_a_user_has_no_role_for_is_denied_and_nothing_runs()
    {
        var (host, runner, observer) = Host(CreateOrder(roles: ["Manager"]));
        await using var _host = host;
        host.Auth.SignIn("ada");
        host.Chat!.CallTool("create_order", Order).Say("I could not.").CallTool("create_order", Order).Say("Done.");

        (await host.RunTurnAsync("order 3 for Anna")).Should().BeNull();
        await FlushAsync(host);
        runner.Runs.Should().BeEmpty("a UI action has no role of its own, a recipe has its own");
        observer.Steps("create_order").Should().Equal(["Denied/role-missing"]);

        host.Auth.SignIn("ada", "Manager");
        (await host.RunTurnAsync("order 3 for Anna")).Should().BeNull();
        runner.Runs.Should().ContainSingle();
    }

    [Theory]
    [Trait("Rule", "recipe.gated")]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task A_recipe_that_asks_for_a_confirmation_waits_for_it_with_the_values_and_runs_on_the_yes(MentorHitlMode mode)
    {
        var (host, runner, observer) = Host(CreateOrder(confirm: true), o => o.HitlMode = mode);
        await using var _host = host;
        var state = host.Get<IMentorStateService>();
        string? message = null;
        var runsWhileAsked = -1;
        state.OnConfirmationRequired += req =>
        {
            message = req.Message;
            runsWhileAsked = runner.Runs.Count;
            _ = state.ConfirmAsync(req.ActionId);
        };
        host.Chat!.CallTool("create_order", Order).Say("Done.");

        (await host.RunTurnAsync("order 3 for Anna")).Should().BeNull();
        await FlushAsync(host);

        runsWhileAsked.Should().Be(0, "mode {0}: nothing runs before the yes", mode);
        message.Should().Contain("Creates an order for a customer").And.Contain("Anna").And.Contain("3");
        runner.Runs.Should().ContainSingle();
        observer.Steps("create_order").Should().Equal(
            ["Allowed/allowed", "ConfirmationRequested/operator", "Confirmed/operator", "Executed/completed"], "mode {0}", mode);
    }

    [Fact]
    [Trait("Rule", "recipe.gated")]
    public async Task A_recipe_the_user_does_not_confirm_does_not_run()
    {
        var (host, runner, observer) = Host(CreateOrder(confirm: true));
        await using var _host = host;
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req => state.Cancel(req.ActionId);
        host.Chat!.CallTool("create_order", Order).Say("Not done.");

        (await host.RunTurnAsync("order 3 for Anna")).Should().BeNull();
        await FlushAsync(host);

        runner.Runs.Should().BeEmpty();
        observer.Steps("create_order").Should().Equal(["Allowed/allowed", "ConfirmationRequested/operator", "Rejected/operator"]);
    }

    [Fact]
    [Trait("Rule", "recipe.gated")]
    public async Task A_role_taken_away_while_the_confirmation_waits_stops_the_recipe()
    {
        var (host, runner, observer) = Host(CreateOrder(roles: ["Manager"], confirm: true));
        await using var _host = host;
        host.Auth.SignIn("ada", "Manager");
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req =>
        {
            host.Auth.SignIn("ada");
            _ = state.ConfirmAsync(req.ActionId);
        };
        host.Chat!.CallTool("create_order", Order).Say("I could not.");

        (await host.RunTurnAsync("order 3 for Anna")).Should().BeNull();
        await FlushAsync(host);

        runner.Runs.Should().BeEmpty("the recipe's roles are checked again after the yes");
        observer.Steps("create_order").Should().Equal(
            ["Allowed/allowed", "ConfirmationRequested/operator", "Confirmed/operator", "Denied/role-missing"]);
    }

    [Theory]
    [Trait("Rule", "recipe.run")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_recipe_turned_off_or_left_after_it_was_offered_does_not_run(bool turnedOff)
    {
        var (host, runner, _) = Host(CreateOrder());
        await using var _host = host;
        var once = 0;
        host.Chat!.BeforeRespond = () =>
        {
            // Between the offer and the call: the recipe turned off elsewhere, or the user on another page.
            if (Interlocked.Increment(ref once) == 1)
            {
                if (turnedOff) host.Get<MentorRecipeRegistry>().TurnOff("create_order", "missing");
                else host.Navigation.StartAt("orders");
            }
            return Task.CompletedTask;
        };
        host.Chat.CallTool("create_order", Order).Say("I could not.");

        (await host.RunTurnAsync("order 3 for Anna")).Should().BeNull();

        runner.Runs.Should().BeEmpty();
        ToolResult(host).Should().Contain(turnedOff ? "is off" : "no longer open");
    }

    [Fact]
    [Trait("Rule", "recipe.element-missing")]
    public async Task A_recipe_whose_element_is_gone_is_not_done_turns_off_and_the_observers_hear_it()
    {
        var (host, runner, observer) = Host(CreateOrder());
        await using var _host = host;
        runner.Answer = new(false, 2, "missing");
        host.Chat!.CallTool("create_order", Order).Say("It did not work.").Say("Hello.");

        (await host.RunTurnAsync("order 3 for Anna")).Should().BeNull();
        await FlushAsync(host);

        observer.Steps("create_order").Should().Equal(["Allowed/allowed", "Failed/element-missing"]);
        observer.Seen.Single(d => d.Outcome == MentorActionOutcome.Failed).Detail.Should().Be("step 3: missing");
        ToolResult(host).Should().Contain("could not be done").And.Contain("step 3");
        host.Get<MentorRecipeRegistry>().IsOff("create_order").Should().BeTrue();
        host.Logs.HasWarning("recipe.element-missing").Should().BeTrue();

        (await host.RunTurnAsync("hello")).Should().BeNull();
        host.Chat.LastCall!.ToolNames.Should().NotContain("create_order", "a recipe that could not be done is not offered again");
    }

    [Fact]
    [Trait("Rule", "recipe.run")]
    public async Task A_value_the_model_did_not_give_runs_nothing_and_the_recipe_stays_on()
    {
        var (host, runner, _) = Host(CreateOrder());
        await using var _host = host;
        host.Chat!.CallTool("create_order", new { customer = "Anna", quantity = "many", express = true }).Say("I need a number.");

        (await host.RunTurnAsync("order many for Anna")).Should().BeNull();

        runner.Runs.Should().BeEmpty();
        ToolResult(host).Should().Contain("quantity");
        host.Get<MentorRecipeRegistry>().IsOff("create_order").Should().BeFalse();
    }

    [Fact]
    [Trait("Rule", "recipe.shape")]
    public void A_recipe_is_checked_when_it_is_added()
    {
        var registry = new MentorRecipeRegistry();
        var good = CreateOrder();

        FluentActions.Invoking(() => registry.Add(good with { Route = "orders" })).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => registry.Add(good with { Name = "create order" })).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => registry.Add(good with { Steps = [] })).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => registry.Add(good with
        {
            Steps = [.. Enumerable.Repeat(new MentorRecipeStep(MentorRecipeStepKind.Click, "#save"), MentorRecipe.MaxSteps + 1)],
        })).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => registry.Add(good with { Steps = [new(MentorRecipeStepKind.Set, "#x", "{nobody}")] }))
            .Should().Throw<ArgumentException>().WithMessage("*{nobody}*");
        FluentActions.Invoking(() => registry.Add(good with { Steps = [new(MentorRecipeStepKind.Set, " ", "x")] }))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => registry.Add(good with { Steps = [new(MentorRecipeStepKind.Row, "tbody tr", "{customer}")] }))
            .Should().Throw<ArgumentException>().WithMessage("*key cell*");
        FluentActions.Invoking(() => registry.Add(good with { Steps = [new(MentorRecipeStepKind.Row, "tbody tr") { Cell = "td" }] }))
            .Should().Throw<ArgumentException>().WithMessage("*key cell*");

        registry.Add(good with { Steps = [.. Enumerable.Repeat(new MentorRecipeStep(MentorRecipeStepKind.Click, "#save"), MentorRecipe.MaxSteps)] });
        registry.For("/orders/new").Should().ContainSingle();
        registry.For("/orders").Should().BeEmpty();
    }
}
