using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.JSInterop;
using Novior.MentorAgent.Abstractions.Core;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Xunit;

namespace Novior.MentorAgent.Tests.Pages;

/// <summary>
/// Story 1319, slice A — a page's form registered with <c>RegisterForm&lt;TModel&gt;</c> is the UI action
/// <c>fill_&lt;form&gt;</c>: its fields come from the model, sensitive ones left out unless the application lets them in,
/// the model's values are checked against their types and become the steps of the recipes' runner, in the browser through
/// the page's <see cref="IJSRuntime"/>. The action goes through the gate and its events like any UI action, fills and never
/// submits. The browser itself is <c>FormBrowserTests</c>.
/// </summary>
[Trait("Rule", "form.fill")]
public sealed class FormActionTests
{
    public enum Priority { Low, Normal, High }

    public sealed class Ticket
    {
        public string? Title { get; set; }
        public int Quantity { get; set; }
        public decimal? Budget { get; set; }
        public Priority Priority { get; set; }
        public bool Urgent { get; set; }
        public DateOnly? Due { get; set; }
        public string? ShippingNote { get; set; }
        public string Computed => Title ?? "";                 // no setter: not a field
        public List<string> Tags { get; set; } = [];           // not a simple type: not a field
        public string? Pin { get; set; }
        public string? NewPassword { get; set; }
        [DataType(DataType.Password)] public string? Code { get; set; }
        [PasswordPropertyText(true)] public string? Phrase { get; set; }
        public string? ApiKey { get; set; }
        public string? AccessToken { get; set; }
        public string? Iban { get; set; }
        public string? Cvv { get; set; }
        public string? CardNumber { get; set; }
        public string? SecretAnswer { get; set; }
        public string? OneTimeCode { get; set; }
    }

    private static MentorForm Form(Action<MentorFormOptions>? configure = null)
    {
        var options = new MentorFormOptions();
        configure?.Invoke(options);
        return MentorForm.Create(typeof(Ticket), "ticket", options);
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    // -- The fields ------------------------------------------------------------------

    [Fact]
    public void The_fields_are_the_models_simple_properties_with_a_setter_and_the_hint_says_the_form_is_never_submitted()
    {
        var form = Form();

        form.ActionName.Should().Be("fill_ticket");
        form.Fields.Select(f => f.Name).Should().Equal("title", "quantity", "budget", "priority", "urgent", "due", "shippingNote");
        form.Fields.Select(f => f.Kind).Should().Equal(
            MentorFormFieldKind.Set, MentorFormFieldKind.Set, MentorFormFieldKind.Set, MentorFormFieldKind.Choose,
            MentorFormFieldKind.Check, MentorFormFieldKind.Set, MentorFormFieldKind.Set);
        form.Fields.Select(f => f.Selector).Should().Contain("[data-mentor-id=\"ticket.shippingNote\"]");
        form.Hint.Should().Be("{ title: string, quantity: integer, budget: number?, priority: string (Low|Normal|High), "
            + "urgent: boolean, due: string (date, yyyy-MM-dd)?, shippingNote: string } - only the fields to fill; the form is filled, never submitted");
    }

    [Theory]
    [InlineData("Pin")]
    [InlineData("NewPassword")]
    [InlineData("Code")]          // [DataType(DataType.Password)]
    [InlineData("Phrase")]        // [PasswordPropertyText(true)]
    [InlineData("ApiKey")]
    [InlineData("AccessToken")]
    [InlineData("Iban")]
    [InlineData("Cvv")]
    [InlineData("CardNumber")]
    [InlineData("SecretAnswer")]
    [InlineData("OneTimeCode")]
    [Trait("Rule", "form.sensitive")]
    public void A_sensitive_field_is_left_out_unless_the_application_lets_it_in(string property)
    {
        var camel = char.ToLowerInvariant(property[0]) + property[1..];
        Form().Fields.Select(f => f.Name).Should().NotContain(camel, "{0} is sensitive", property);
        Form().Hint.Should().NotContain(camel + ":");

        var included = Form(o => o.IncludeSensitive(property)).Fields.Single(f => f.Name == camel);
        included.AllowSensitive.Should().BeTrue("a field the application let in may write a password field of the page");
        Form().Fields.Should().OnlyContain(f => !f.AllowSensitive);
    }

    [Theory]
    [InlineData("Pin", true)]
    [InlineData("SimPin", true)]
    [InlineData("PIN_code", true)]
    [InlineData("newpassword", true)]
    [InlineData("Shipping", false)]
    [InlineData("Opinion", false)]
    [InlineData("Spinner", false)]
    [InlineData("Pinned", false)]
    [InlineData("Description", false)]
    [InlineData("Customer", false)]
    [Trait("Rule", "form.sensitive")]
    public void A_short_sensitive_word_counts_only_as_a_word_of_the_name(string name, bool sensitive)
    {
        var property = typeof(Names).GetProperty(name)!;
        MentorForm.IsSensitive(property).Should().Be(sensitive, "'{0}'", name);
    }

    private sealed class Names
    {
        public string? Pin { get; set; }
        public string? SimPin { get; set; }
        public string? PIN_code { get; set; }
        public string? newpassword { get; set; }
        public string? Shipping { get; set; }
        public string? Opinion { get; set; }
        public string? Spinner { get; set; }
        public string? Pinned { get; set; }
        public string? Description { get; set; }
        public string? Customer { get; set; }
    }

    [Fact]
    public void Excluded_fields_and_a_selector_of_the_pages_own_are_the_applications_and_a_wrong_name_throws_at_once()
    {
        var form = Form(o => o.Exclude(nameof(Ticket.Budget), "due").Selector("title", "#title"));

        form.Fields.Select(f => f.Name).Should().NotContain(["budget", "due"]);
        form.Fields.Single(f => f.Name == "title").Selector.Should().Be("#title");

        FluentActions.Invoking(() => Form(o => o.Exclude("Nope"))).Should().Throw<ArgumentException>().WithMessage("*'Nope'*");
        FluentActions.Invoking(() => Form(o => o.IncludeSensitive("Colour"))).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => MentorForm.Create(typeof(Ticket), "the ticket", new MentorFormOptions()))
            .Should().Throw<ArgumentException>("a form's name is part of a tool's name");
        FluentActions.Invoking(() => MentorForm.Create(typeof(Ticket), new string('t', 60), new MentorFormOptions()))
            .Should().Throw<ArgumentException>("fill_ and the name make at most 64 characters");
        FluentActions.Invoking(() => MentorForm.Create(typeof(Names), "names", new MentorFormOptions().Exclude(
                "SimPin", "Shipping", "Opinion", "Spinner", "Pinned", "Description", "Customer")))
            .Should().Throw<ArgumentException>("a form with no field the model can fill").WithMessage("*no field*");
    }

    // -- The steps -------------------------------------------------------------------

    [Fact]
    public void The_steps_follow_the_forms_order_with_each_value_written_as_the_page_reads_it()
    {
        var steps = Form().Steps(Json(new
        {
            urgent = "true", priority = "high", title = "Printer jam", quantity = 3, budget = 12.50m, due = "2026-12-01",
        }));

        steps.Select(s => s with { Field = "" }).Should().Equal(
            new MentorFormStep("set", "[data-mentor-id=\"ticket.title\"]", "Printer jam", false),
            new MentorFormStep("set", "[data-mentor-id=\"ticket.quantity\"]", "3", false),
            new MentorFormStep("set", "[data-mentor-id=\"ticket.budget\"]", "12.50", false),
            new MentorFormStep("choose", "[data-mentor-id=\"ticket.priority\"]", "High", false),
            new MentorFormStep("check", "[data-mentor-id=\"ticket.urgent\"]", "true", false),
            new MentorFormStep("set", "[data-mentor-id=\"ticket.due\"]", "2026-12-01", false));
        steps.Select(s => s.Field).Should().Equal("title", "quantity", "budget", "priority", "urgent", "due");
        Form().Steps("{\"Title\":\"As a text\"}").Single().Value.Should().Be("As a text", "an object written as a text, any case");
        Form().Steps(Json(new { budget = (decimal?)null })).Single().Value.Should().Be("", "null clears a field that takes none");
    }

    [Theory]
    [InlineData("{\"quantity\":2.5}", "quantity")]
    [InlineData("{\"quantity\":\"many\"}", "quantity")]
    [InlineData("{\"quantity\":null}", "quantity")]
    [InlineData("{\"urgent\":\"maybe\"}", "urgent")]
    [InlineData("{\"priority\":\"Urgent\"}", "priority")]
    [InlineData("{\"due\":\"next week\"}", "due")]
    [InlineData("{\"title\":{\"a\":1}}", "title")]
    public void A_value_the_field_does_not_take_fills_nothing_and_says_which_field(string parameter, string field)
    {
        FluentActions.Invoking(() => Form().Steps(JsonDocument.Parse(parameter).RootElement.Clone()))
            .Should().Throw<ArgumentException>().WithMessage($"*'{field}'*");
    }

    [Fact]
    [Trait("Rule", "form.sensitive")]
    public void A_field_the_form_does_not_offer_fills_nothing_and_a_sensitive_one_is_as_unknown()
    {
        FluentActions.Invoking(() => Form().Steps(Json(new { title = "x", colour = "red" })))
            .Should().Throw<ArgumentException>().WithMessage("*'colour' is not a field*title, quantity*");
        FluentActions.Invoking(() => Form().Steps(Json(new { pin = "1234" })))
            .Should().Throw<ArgumentException>().WithMessage("*'pin' is not a field*")
            .Which.Message.Should().NotContain("pin,", "the fields listed are the ones offered");
        FluentActions.Invoking(() => Form().Steps(Json(new { }))).Should().Throw<ArgumentException>().WithMessage("No field*");
        FluentActions.Invoking(() => Form().Steps(Json("Printer jam"))).Should().Throw<ArgumentException>().WithMessage("*one object*");
        FluentActions.Invoking(() => Form().Steps(null)).Should().Throw<ArgumentException>().WithMessage("*one object*");
    }

    // -- Through the gate -----------------------------------------------------------------

    /// <summary>The page's JavaScript runtime: the runner's module, recording the steps it is given, answering as told.</summary>
    private sealed class RecordingJs : IJSRuntime, IJSObjectReference
    {
        internal List<string> Imports { get; } = [];
        internal List<JsonElement> Runs { get; } = [];
        internal object Answer { get; set; } = new { done = true, step = 0, reason = (string?)null };
        internal bool Disposed { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "import")
            {
                Imports.Add((string)args![0]!);
                return ValueTask.FromResult((TValue)(object)this);
            }
            identifier.Should().Be("run");
            // As the browser receives it: serialized with the web defaults of JS interop (camelCase).
            Runs.Add(JsonSerializer.SerializeToElement(args![0], JsonSerializerOptions.Web));
            return ValueTask.FromResult(JsonSerializer.Deserialize<TValue>(
                JsonSerializer.Serialize(Answer), JsonSerializerOptions.Web)!);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private static MentorHostFixture Host(RecordingActionObserver observer, TestActionPolicy? policy = null)
        => MentorHostFixture.Create(o => o.MemoryAutoCapture = false, s =>
        {
            s.AddMentorActionObserver(observer);
            if (policy is not null) s.AddMentorActionPolicy(policy);
        }, withRecorder: true);

    [Fact]
    public async Task The_model_fills_a_registered_form_through_the_gate_and_the_page_runs_the_steps()
    {
        var observer = new RecordingActionObserver();
        await using var host = Host(observer);
        var js = new RecordingJs();
        host.Get<IMentorPageContext>().SetPageName("Tickets")
            .RegisterForm<Ticket>("ticket", "The form of a new ticket", js)
            .SignalReady();
        host.Chat!.CallTool("fill_ticket", new { parameter = new { title = "Printer jam", urgent = true } })
            .Say("I filled the form: check it and send it.");

        (await host.RunTurnAsync("open a ticket for the printer jam, urgent")).Should().BeNull();
        await host.Get<MentorActionDecisions>().FlushAsync();

        js.Imports.Should().Equal(MentorForm.Module);
        js.Runs.Should().ContainSingle().Which.GetRawText().Should().Be(
            "[{\"kind\":\"set\",\"selector\":\"[data-mentor-id=\\u0022ticket.title\\u0022]\",\"value\":\"Printer jam\",\"allowSensitive\":false},"
            + "{\"kind\":\"check\",\"selector\":\"[data-mentor-id=\\u0022ticket.urgent\\u0022]\",\"value\":\"true\",\"allowSensitive\":false}]");
        js.Disposed.Should().BeTrue("the module is let go after the fill");
        observer.Steps("fill_ticket").Should().Equal(["Allowed/allowed", "Executed/completed"]);
        observer.Seen.First(d => d.ToolName == "fill_ticket").DisplayName.Should().StartWith("The form of a new ticket");
        host.Recorder!.Sequence.Where(e => e.Contains("Action", StringComparison.Ordinal)).Should().Equal(
            nameof(RecordingStateService.NotifyUIActionExecuting), nameof(RecordingStateService.NotifyUIActionCompleted));

        var tool = host.Chat!.Calls[0].Tool("fill_ticket")!;
        tool.Description.Should().Contain("The form of a new ticket").And.Contain("never submitted").And.Contain("title: string")
            .And.NotContain("pin:").And.NotContain("newPassword");
    }

    [Fact]
    public async Task A_field_the_page_does_not_have_fails_the_fill_and_the_model_is_told_which()
    {
        var observer = new RecordingActionObserver();
        await using var host = Host(observer);
        var js = new RecordingJs { Answer = new { done = false, step = 1, reason = "missing" } };
        host.Get<IMentorPageContext>().RegisterForm<Ticket>("ticket", "The form of a new ticket", js).SignalReady();
        host.Chat!.CallTool("fill_ticket", new { parameter = new { title = "Printer jam", quantity = 2 } }).Say("Sorry.");

        await host.RunTurnAsync("two printers");
        await host.Get<MentorActionDecisions>().FlushAsync();

        observer.Steps("fill_ticket").Should().Equal(["Allowed/allowed", "Failed/exception"], "a fill not done failed (BUG-241)");
        Results(host.Chat!).Should().Contain("the field 'quantity' is missing in the page");
    }

    [Fact]
    [Trait("Rule", "form.sensitive")]
    public async Task A_password_field_of_the_page_is_never_written_and_the_model_is_told_the_user_fills_it()
    {
        var observer = new RecordingActionObserver();
        await using var host = Host(observer);
        var js = new RecordingJs { Answer = new { done = false, step = 0, reason = "sensitive" } };
        host.Get<IMentorPageContext>().RegisterForm<Ticket>("ticket", "The form of a new ticket", js).SignalReady();
        host.Chat!.CallTool("fill_ticket", new { parameter = new { title = "x" } }).Say("Sorry.");

        await host.RunTurnAsync("fill it");
        await host.Get<MentorActionDecisions>().FlushAsync();

        observer.Steps("fill_ticket").Should().Equal(["Allowed/allowed", "Failed/exception"]);
        Results(host.Chat!).Should().Contain("'title' is a password field, which the assistant never fills");
    }

    [Fact]
    public async Task A_policy_that_stops_the_fill_runs_nothing_in_the_page()
    {
        var observer = new RecordingActionObserver();
        var policy = new TestActionPolicy
        {
            Verdict = r => r.ToolName == "fill_ticket" ? MentorActionVerdict.Stop("not here") : MentorActionVerdict.Proceed,
        };
        await using var host = Host(observer, policy);
        var js = new RecordingJs();
        host.Get<IMentorPageContext>().RegisterForm<Ticket>("ticket", "The form of a new ticket", js).SignalReady();
        host.Chat!.CallTool("fill_ticket", new { parameter = new { title = "x" } }).Say("I may not.");

        await host.RunTurnAsync("fill it");
        await host.Get<MentorActionDecisions>().FlushAsync();

        js.Runs.Should().BeEmpty();
        observer.Steps("fill_ticket").Should().ContainSingle().Which.Should().EndWith("/policy", "nothing was executed");
    }

    [Fact]
    public async Task A_value_the_field_does_not_take_reaches_neither_the_page_nor_an_executed_decision()
    {
        var observer = new RecordingActionObserver();
        await using var host = Host(observer);
        var js = new RecordingJs();
        host.Get<IMentorPageContext>().RegisterForm<Ticket>("ticket", "The form of a new ticket", js).SignalReady();
        host.Chat!.CallTool("fill_ticket", new { parameter = new { quantity = "many" } }).Say("Sorry.");

        await host.RunTurnAsync("fill it");
        await host.Get<MentorActionDecisions>().FlushAsync();

        js.Imports.Should().BeEmpty("the values are checked before anything runs in the page");
        observer.Steps("fill_ticket").Should().Equal(["Allowed/allowed", "Failed/exception"]);
        Results(host.Chat!).Should().Contain("'quantity'");
    }

    private static string Results(FakeChatClient chat) => string.Join("\n", chat.Calls
        .SelectMany(c => c.Messages).SelectMany(m => m.Contents).OfType<FunctionResultContent>()
        .Select(r => System.Text.RegularExpressions.Regex.Unescape(r.Result?.ToString() ?? "")));
}
