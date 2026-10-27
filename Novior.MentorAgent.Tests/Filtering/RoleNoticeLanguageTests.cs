using System.Text.RegularExpressions;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Gating;
using Novior.MentorAgent.Tests.Harness;
using Novior.MentorAgent.Tests.Prompt;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Filtering;

/// <summary>
/// BUG-224 — the notice of a turn that left an action out (BUG-200) was English in every language. In Italian it did
/// not hold: asked by an employee to approve a claim, the model offered the approval it had no tool for, and after
/// "Sì" it submitted a copy of the claim, or wrote that it had approved it. In English the same turns were refused.
/// The notice now follows the language of the turn, as the rules of story 191 do, for the coordinator, on path B and
/// for the agents under the coordinator; its English text is the one of before, byte for byte. Every language also
/// ends it with what the Italian turns did: no offer of such an action, a reply to an offer does not make it
/// available, and no other tool in its place.
///
/// <para>
/// In the test application <c>cancel_order</c> needs the Manager role and <c>export_ledger</c> the Admin role; the
/// specialist <c>ShippingAgent</c> holds <c>cancel_shipment</c> (Manager).
/// </para>
/// </summary>
public sealed class RoleNoticeLanguageTests
{
    private const string Heading = "## Action status for THIS request — MANDATORY, overrides the capability list above";

    // Stretches of each text, on one line: the texts wrap.
    private const string EnglishBody = "Some other actions of this application are NOT available to this user on this turn";
    private const string ItalianBody = "Altre azioni di questa applicazione NON sono disponibili per questo utente in questo turno";
    private const string EnglishSignIn = "add that signing in may make it available";
    private const string ItalianSignIn = "con l'accesso potrebbe diventare disponibile";
    private const string EnglishFollowUp = "does not make such an action available";
    private const string ItalianFollowUp = "non rende disponibile un'azione del genere";

    /// <summary>What every English notice ends with since BUG-224, after the notice of before.</summary>
    private const string EnglishFollowUpText = """
        Do not offer an action none of your tools can do, and do not ask the user whether to do it. A reply to an
        earlier message of yours — a yes, a choice, a value — does not make such an action available: if you
        offered one, say now, in one short sentence, that you cannot do it here. Never call one of your tools in
        its place: each tool does only what its description says.
        """;

    private static IReadOnlyList<FakeChatClient.RecordedCall> CoordinatorCalls(FakeChatClient chat)
        => chat.Calls.Where(c => c.ToolNames.Count > 0).ToList();

    /// <summary>The last section of the request's instructions: the notice goes at the end, after everything else.</summary>
    private static string LastSection(FakeChatClient.RecordedCall call)
    {
        var instructions = call.Instructions ?? "";
        return instructions[Math.Max(0, instructions.LastIndexOf("## ", StringComparison.Ordinal))..];
    }

    private static string OneLine(string text) => Regex.Replace(text, @"\s+", " ");

    private static string Lf(string text) => text.Replace("\r\n", "\n");

    private static MentorHostFixture Italian(Action<MentorOptions>? configure = null)
        => MentorHostFixture.Create(o =>
        {
            o.Language = MentorLanguage.Italian;
            o.MemoryAutoCapture = false;
            configure?.Invoke(o);
        });

    // ── The coordinator ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task In_italian_the_notice_is_italian_and_ends_with_the_follow_up(MentorHitlMode mode)
    {
        await using var host = Italian(o => o.HitlMode = mode);
        host.Auth.SignIn("ada", "Manager");
        host.Chat!.Say("Qui non posso farlo.");

        (await host.RunTurnAsync("esporta il registro")).Should().BeNull();

        var call = CoordinatorCalls(host.Chat).Single();
        var last = OneLine(LastSection(call));
        last.Should().StartWith(Heading, "mode {0}: the heading stays English, as every heading of the prompts", mode);
        last.Should().Contain(ItalianBody).And.Contain(ItalianFollowUp);
        last.Should().NotContain(EnglishBody).And.NotContain(EnglishFollowUp);
        last.Should().NotContain(ItalianSignIn, "ada is signed in");
        last.Should().NotContain("export_ledger").And.NotContain("Admin", "the notice names no action and no role");
        call.Messages.Should().NotContain(m => m.Text.Contains(ItalianBody), "it is never a message");
    }

    [Fact]
    public async Task The_notice_follows_the_language_of_the_page_turn_by_turn()
    {
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        host.Auth.SignIn("ada", "Manager");
        host.Chat!.Say("Qui non posso farlo.").Say("I cannot do that here.");

        await host.Orchestrator.SendMessageAsync(
            new MentorUserMessage("esporta il registro") { Language = MentorLanguage.Italian });
        (await host.RunTurnAsync("export the ledger")).Should().BeNull();

        var calls = CoordinatorCalls(host.Chat);
        calls.Should().HaveCount(2);
        OneLine(LastSection(calls[0])).Should().Contain(ItalianBody).And.Contain(ItalianFollowUp)
            .And.NotContain(EnglishBody, "the page asked for Italian");
        OneLine(LastSection(calls[1])).Should().Contain(EnglishBody).And.Contain(EnglishFollowUp)
            .And.NotContain(ItalianBody, "a turn without a language of its own is in the application's");
    }

    [Fact]
    public async Task In_english_the_notice_of_before_is_kept_byte_for_byte_and_the_follow_up_comes_after_it()
    {
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        host.Chat!.Say("I cannot do that here.").Say("Please sign in.");

        host.Auth.SignIn("ada", "Manager");
        (await host.RunTurnAsync("export the ledger")).Should().BeNull();
        host.Auth.SignOut();
        (await host.RunTurnAsync("cancel order 5")).Should().BeNull();

        var calls = CoordinatorCalls(host.Chat);
        Lf(LastSection(calls[0])).Should().Be(
            Lf(EnglishPromptGolden.RoleNotice) + "\n" + Lf(EnglishFollowUpText),
            "the measures of BUG-200 belong to the English notice: it stays as it was, and the follow-up comes after it");
        Lf(LastSection(calls[1])).Should().Be(
            Lf(EnglishPromptGolden.RoleNotice) + "\n" + Lf(EnglishPromptGolden.RoleNoticeSignIn) + "\n" + Lf(EnglishFollowUpText),
            "with the sentence for a user who is not signed in where it was");
    }

    [Fact]
    public async Task In_italian_a_user_who_is_not_signed_in_is_told_in_italian_that_signing_in_may_help()
    {
        await using var host = Italian();
        host.Chat!.Say("Accedi, per favore.");

        (await host.RunTurnAsync("annulla l'ordine 5")).Should().BeNull();

        var last = OneLine(LastSection(CoordinatorCalls(host.Chat).Single()));
        last.Should().Contain(ItalianBody).And.Contain(ItalianSignIn).And.Contain(ItalianFollowUp);
        last.Should().NotContain(EnglishSignIn);
        last.Should().NotContain("Manager").And.NotContain("cancel_order");
    }

    [Fact]
    public async Task The_log_says_in_which_language_the_model_was_told()
    {
        // For the live proof: the debug line of the actions left out names the language of their notice.
        await using var host = Italian();
        host.Auth.SignIn("ada", "Manager");
        host.Chat!.Say("Qui non posso farlo.");

        (await host.RunTurnAsync("esporta il registro")).Should().BeNull();

        host.Logs.Records.Should().Contain(r => r.Level == LogLevel.Debug
            && r.Message.Contains("action(s) not offered") && r.Message.Contains("Notice in Italian."));
    }

    // ── The answer to an offer ──────────────────────────────────────────────────

    [Fact]
    public async Task A_yes_to_an_offer_of_an_action_left_out_is_sent_the_follow_up_in_italian()
    {
        // The live case: the model offered what it had no tool for, and the user said yes.
        await using var host = Italian();
        host.Auth.SignIn("ada", "Manager");
        host.Chat!.Say("Vuoi che esporti il registro?").Say("Qui non posso farlo.");

        (await host.RunTurnAsync("esporta il registro")).Should().BeNull();
        (await host.RunTurnAsync("Sì")).Should().BeNull();

        var yes = CoordinatorCalls(host.Chat)[1];
        yes.ToolNames.Should().NotContain("export_ledger", "a yes makes nothing available");
        yes.Messages.Should().Contain(m => m.Role == ChatRole.Assistant && m.Text.Contains("Vuoi che esporti il registro?"));
        yes.Messages.Last(m => m.Role == ChatRole.User).Text.Should().Be("Sì");
        OneLine(LastSection(yes)).Should().Contain(ItalianBody).And.Contain(ItalianFollowUp);
    }

    [Fact]
    public async Task The_notice_depends_on_what_is_left_out_and_never_on_the_words_of_the_turn()
    {
        await using var host = Italian();
        host.Auth.SignIn("ada", "Manager");
        host.Chat!.SayRepeatedly("Va bene.", 3);

        (await host.RunTurnAsync("esporta il registro")).Should().BeNull();
        (await host.RunTurnAsync("Sì")).Should().BeNull();
        (await host.RunTurnAsync("ciao")).Should().BeNull();

        var notices = CoordinatorCalls(host.Chat).Select(c => LastSection(c)).ToList();
        notices.Should().HaveCount(3).And.OnlyContain(n => n == notices[0]);
    }

    [Fact]
    public async Task In_italian_a_user_who_holds_every_role_gets_no_notice()
    {
        await using var host = Italian();
        host.Auth.SignIn("ada", "Manager", "Admin");
        host.Chat!.Say("Ciao.");

        (await host.RunTurnAsync("ciao")).Should().BeNull();

        var instructions = OneLine(CoordinatorCalls(host.Chat).Single().Instructions ?? "");
        instructions.Should().NotContain(Heading).And.NotContain(ItalianBody).And.NotContain(EnglishBody)
            .And.NotContain(ItalianFollowUp, "nothing was left out, and the cached prefix stays as it is");
    }

    // ── Path B and the agents under the coordinator ─────────────────────────────

    [Fact]
    public async Task On_path_B_the_notice_is_in_the_language_of_the_turn()
    {
        var inner = new FakeChatClient();
        await using var host = MentorHostFixture.Create(
            o =>
            {
                o.ChatClient = null;
                o.Agent = inner.AsAIAgent(instructions: "You are a test agent.");
                o.Language = MentorLanguage.Italian;
                o.MemoryAutoCapture = false;
            },
            withChatClient: false);
        inner.Say("Accedi, per favore.");

        (await host.RunTurnAsync("annulla l'ordine 5")).Should().BeNull();

        var last = OneLine(LastSection(CoordinatorCalls(inner).Single()));
        last.Should().Contain(ItalianBody).And.Contain(ItalianSignIn).And.Contain(ItalianFollowUp);
        last.Should().NotContain(EnglishBody);
    }

    [Fact]
    public async Task A_specialist_is_told_in_the_language_of_the_turn()
    {
        await using var host = Italian();
        host.Chat!.Respond(ActionDecisionTests.Delegating(host, "dov'è l'ordine 7?", "shipping", "get_tracking_status",
            new() { ["orderId"] = 7 }));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        host.Auth.SignIn("bob", "Admin");
        (await host.RunTurnAsync("dov'è l'ordine 7?", deadline.Token)).Should().BeNull();

        var calls = host.Chat.Calls.Where(c => c.ToolNames.Contains("get_tracking_status")).ToList();
        calls.Should().NotBeEmpty("the specialist ran. Warnings: {0}", host.Logs.WarningText);
        calls.Should().OnlyContain(c => !c.ToolNames.Contains("cancel_shipment"), "bob is no Manager");
        calls.Should().OnlyContain(c => OneLine(LastSection(c)).Contains(ItalianBody)
                                        && OneLine(LastSection(c)).Contains(ItalianFollowUp)
                                        && !OneLine(LastSection(c)).Contains(EnglishBody));
    }
}
