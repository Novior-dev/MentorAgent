using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Xunit;

namespace Novior.MentorAgent.Tests.Gating;

/// <summary>
/// What the model is told when the user says no — and whether it can tell that apart from a tool
/// that broke.
///
/// <para>
/// Found in Part D, driving the confirmation banner on the live sample. The user pressed
/// <em>Annulla</em>; the assistant acknowledged and then offered three alternatives — *"Scegliere un
/// prodotto diverso… Selezionare un'altra priorità… Come preferisci continuare?"* — which the
/// coordinator prompt forbids in as many words: <em>"Do NOT explain why the action was not taken.
/// Do NOT reference previous requests. Do NOT call tools or suggest alternatives."</em>
/// </para>
///
/// <para>
/// That is not the model being disobedient. A refusal is delivered as
/// <c>{"success":false,"error":"Operazione annullata dall'utente."}</c> — <b>byte-identical in shape
/// to a genuine tool failure</b> — and the behavioural rule immediately above the one it broke says
/// the opposite for failures: <em>"When an action fails, explain clearly what went wrong and suggest
/// an alternative when possible."</em> The model was handed two contradictory rules and no way to
/// tell which case it was in, since the only distinguishing signal is a localised sentence that
/// reads differently in each of the ten supported languages.
/// </para>
///
/// <para>
/// So the property under test is not "does the model behave" — that is not a property a test can own.
/// It is <b>can the model tell</b>: a decision the user made must be structurally distinguishable
/// from a thing that went wrong, in the payload itself, without parsing prose.
/// </para>
/// </summary>
public sealed class RefusalIsNotAnErrorTests
{
    /// <summary>Every tool result the model was handed across the whole turn, joined.</summary>
    private static string ToolResultsSeenBy(FakeChatClient chat)
        => string.Join(" | ", chat.Calls
            .SelectMany(c => c.Messages)
            .SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>()
            .Select(r => r.Result?.ToString() ?? string.Empty));

    private static async Task<string> RefuseAndReadResultAsync(Novior.MentorAgent.Options.MentorHitlMode mode = Novior.MentorAgent.Options.MentorHitlMode.Blocking)
    {
        // Not withRecorder: the recorder is a fake state service, not a decorator, so its Cancel
        // never reaches the real approval store and the turn parks forever (standing rule).
        await using var host = MentorHostFixture.Create(o => o.HitlMode = mode);
        host.Chat!.CallTool("issue_refund", new { orderId = 1001 }).Say("Understood.");

        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req => state.Cancel(req.ActionId);

        await host.RunTurnAsync("refund order 1001");
        return ToolResultsSeenBy(host.Chat);
    }

    [Fact]
    public async Task A_refusal_is_marked_as_the_users_decision_not_as_a_failure()
    {
        // The discriminator has to be a field, not a sentence. `error` alone means the model reaches
        // for the rule about things going wrong, which is the rule that tells it to suggest
        // alternatives — exactly what it did on the live run.
        var results = await RefuseAndReadResultAsync();

        results.Should().Contain("declined_by_user",
            "a decision the user made is not a malfunction, and the model has to be able to tell");
    }

    [Fact]
    public async Task A_refusal_carries_the_instruction_that_belongs_to_it()
    {
        // MentorAgent already steers the model from inside a tool result — the card substitution
        // notice does exactly this ("comment briefly, do not repeat every field"). The same technique
        // applies here, and it puts the rule next to the case it governs instead of leaving it in a
        // prompt block competing with a contradictory neighbour.
        var results = await RefuseAndReadResultAsync();

        results.Should().Contain("do not suggest alternatives",
            "the prompt says so already; saying it where the case actually occurs is what makes it stick");
        results.Should().Contain("not retry",
            "an assistant that immediately re-proposes the action it was just refused is worse than one that argues");
    }

    [Fact]
    public async Task A_refusal_still_says_plainly_that_the_action_did_not_run()
    {
        // The marker must not cost the plain fact. A model reading only `declined_by_user` and no
        // `success` flag could reasonably conclude the action went through and the user merely
        // commented on it.
        var results = await RefuseAndReadResultAsync();

        results.Should().Contain("\"success\":false",
            "the action did not run, and that is the first thing the model needs to know");
    }

    [Theory]
    [InlineData(Novior.MentorAgent.Options.MentorHitlMode.Blocking)]
    [InlineData(Novior.MentorAgent.Options.MentorHitlMode.Native)]
    public async Task Both_modes_tell_the_model_the_same_thing_about_a_refusal(Novior.MentorAgent.Options.MentorHitlMode mode)
    {
        // rc.14 review: the Native flow handed the model only the localized sentence — no marker, no
        // instruction — while the Blocking flow had both since BUG-031. The ApiServer sample runs Native.
        var results = await RefuseAndReadResultAsync(mode);

        results.Should().Contain("declined_by_user");
        results.Should().Contain("do not suggest alternatives");
    }

    [Fact]
    public async Task A_refusal_tells_the_model_that_nothing_was_checked_or_changed()
    {
        // BUG-115, measured on MAUI rc.14 (Blocking, the instruction already there): after the user pressed
        // Annulla on "update_order_status · Cancelled", the reply said the order "is probably no longer
        // cancellable because it is already being processed" — 3 of 3, the order was Pending. The model
        // filled the gap with a reason. The result now says there is no reason to give.
        var results = await RefuseAndReadResultAsync();

        results.Should().Contain("nothing was checked and nothing changed");
        results.Should().Contain("do not give or guess any other reason");
        results.Should().Contain("do not describe the state of any record");
    }

    [Fact]
    public void The_refusal_text_does_not_use_the_verb_for_cancelling()
    {
        // "Operazione annullata dall'utente" for a request to "annullare" an order reads as "the cancellation
        // was cancelled" — and the model went looking for why (BUG-115). The sentence says the user declined.
        var it = new Novior.MentorAgent.Abstractions.Core.MentorLocalizer(Novior.MentorAgent.Abstractions.Models.MentorLanguage.Italian).Get("confirm.denied");
        var en = new Novior.MentorAgent.Abstractions.Core.MentorLocalizer(Novior.MentorAgent.Abstractions.Models.MentorLanguage.English).Get("confirm.denied");

        it.Should().NotContainEquivalentOf("annull");
        it.Should().ContainEquivalentOf("rifiutat");
        en.Should().NotContainEquivalentOf("cancel");
        en.Should().ContainEquivalentOf("declined");
    }

    [Fact]
    public async Task A_genuine_tool_failure_is_not_dressed_up_as_a_refusal()
    {
        // The control, and the reason the marker is a separate field rather than a reworded error.
        // A tool that threw must keep reading as a tool that threw — the "suggest an alternative"
        // rule is right for that case, and blurring the two would only move the bug.
        await using var host = MentorHostFixture.Create();
        host.Chat!.CallTool("failing_action").Say("done");

        await host.RunTurnAsync("run the failing action");

        var results = ToolResultsSeenBy(host.Chat);
        results.Should().Contain("\"error\"", "a failure is still a failure");
        results.Should().NotContain("declined_by_user",
            "nobody declined anything — the tool broke");
    }
}
