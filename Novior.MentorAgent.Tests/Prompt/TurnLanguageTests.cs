using System.Reflection;
using FluentAssertions;
using FluentAssertions.Execution;
using Novior.MentorAgent.Abstractions.Core;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Attributes;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Rag;
using Novior.MentorAgent.Utility;
using Novior.MentorAgent.Middleware;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Prompt;

/// <summary>
/// A turn in the language of its page. <see cref="MentorUserMessage.Language"/> travels with the turn, and the server
/// writes everything for that turn in it: the instructions (the builder's own variant for the language), the tool
/// results, the errors, the documents' citation rule and the confirmation's context. Without it, or with the
/// application's language, nothing changes: the instructions are the ones of before, byte for byte.
/// </summary>
public sealed class TurnLanguageTests
{
    private static async Task<Exception?> TurnAsync(MentorHostFixture host, string text, MentorLanguage? language,
        CancellationToken ct = default)
    {
        try
        {
            await host.Orchestrator.SendMessageAsync(new MentorUserMessage(text) { Language = language }, ct);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static IReadOnlyList<FakeChatClient.RecordedCall> CoordinatorCalls(MentorHostFixture host)
        => host.Chat!.Calls.Where(c => c.ToolNames.Count > 0).ToList();

    private static string Instructions(FakeChatClient.RecordedCall call) => call.Instructions ?? "";

    private static string Line(MentorLanguage language) => $"ALWAYS respond in **{language.ToFullName()}**";

    /// <summary>The texts of the coordinator's prompt that follow the language (story 191).</summary>
    private static readonly string[] CoordinatorKeys =
        ["ConfirmationPreface", "DeclinedConfirmation", "CoordinatorGrounding", "PlainTerms", "OutOfScope"];

    private static string Text(MentorLanguage language, string key)
        => (string)typeof(MentorPromptTexts).GetProperty(key)!.GetValue(MentorPromptTexts.For(language))!;

    /// <summary>The prompt without the line of the clock, which moves with the minute.</summary>
    private static string[] Lines(string instructions)
        => [.. instructions.ReplaceLineEndings("\n").Split('\n').Where(l => !l.StartsWith("- Current date/time:", StringComparison.Ordinal))];

    private static string ToolResultsSeenBy(MentorHostFixture host)
        => string.Join(" | ", host.Chat!.Calls
            .SelectMany(c => c.Messages)
            .SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>()
            .Select(r => r.Result?.ToString() ?? string.Empty));

    // ── The coordinator ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Two_sessions_at_once_are_each_sent_the_instructions_of_their_own_language()
    {
        await using var italianPage = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        await using var englishPage = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        italianPage.Chat!.Say("OK.");
        englishPage.Chat!.Say("OK.");

        await Task.WhenAll(TurnAsync(italianPage, "ciao", MentorLanguage.Italian), TurnAsync(englishPage, "hello", null));

        using (new AssertionScope())
        {
            Instructions(CoordinatorCalls(italianPage)[0]).Should().Contain(Line(MentorLanguage.Italian))
                .And.Contain(Text(MentorLanguage.Italian, "CoordinatorGrounding"))
                .And.NotContain(Line(MentorLanguage.English));
            Instructions(CoordinatorCalls(englishPage)[0]).Should().Contain(Line(MentorLanguage.English))
                .And.NotContain(Line(MentorLanguage.Italian));
        }
    }

    [Fact]
    public async Task A_turn_without_a_language_or_in_the_application_one_is_sent_the_instructions_of_before()
    {
        async Task<string[]> SentAsync(Func<MentorHostFixture, Task> turn)
        {
            await using var host = MentorHostFixture.Create(o => { o.Language = MentorLanguage.Italian; o.MemoryAutoCapture = false; });
            host.Chat!.Say("OK.");
            await turn(host);
            return Lines(Instructions(CoordinatorCalls(host)[0]));
        }

        var before = await SentAsync(h => h.RunTurnAsync("ciao"));
        var none = await SentAsync(h => TurnAsync(h, "ciao", null));
        var same = await SentAsync(h => TurnAsync(h, "ciao", MentorLanguage.Italian));

        none.Should().Equal(before);
        same.Should().Equal(before, "the application's language is the language the instructions were built in");
    }

    [Fact]
    public async Task A_French_turn_on_an_Italian_application_is_sent_the_French_instructions()
    {
        await using var host = MentorHostFixture.Create(o => { o.Language = MentorLanguage.Italian; o.MemoryAutoCapture = false; });
        host.Chat!.Say("OK.");

        await TurnAsync(host, "bonjour", MentorLanguage.French);

        var sent = Instructions(CoordinatorCalls(host)[0]);
        using (new AssertionScope())
        {
            sent.Should().Contain(Line(MentorLanguage.French)).And.NotContain(Line(MentorLanguage.Italian));
            foreach (var key in CoordinatorKeys.Where(k => sent.Contains(Text(MentorLanguage.Italian, k)) || sent.Contains(Text(MentorLanguage.French, k))))
                sent.Should().Contain(Text(MentorLanguage.French, key), key).And.NotContain(Text(MentorLanguage.Italian, key), key);
            sent.Should().Contain(Text(MentorLanguage.French, "CoordinatorGrounding"));
            sent.Should().NotContain(TurnLanguageChatClient.FallbackLine(MentorLanguage.French),
                "the builder's variant is sent, not the line kept for instructions it did not build");
            host.Logs.WarningText.Should().NotContain("for this turn");
        }
    }

    [Fact]
    public async Task Two_turns_in_one_language_are_sent_the_same_instructions_and_another_language_changes_only_its_texts()
    {
        await using var host = MentorHostFixture.Create(o => { o.Language = MentorLanguage.Italian; o.MemoryAutoCapture = false; });
        host.Chat!.SayRepeatedly("OK.", 3);

        await TurnAsync(host, "bonjour", MentorLanguage.French);
        await TurnAsync(host, "encore", MentorLanguage.French);
        await TurnAsync(host, "ciao", MentorLanguage.Italian);

        var calls = CoordinatorCalls(host);
        calls.Should().HaveCount(3);
        Lines(Instructions(calls[1])).Should().Equal(Lines(Instructions(calls[0])), "a session keeps one variant per language");

        var french = Lines(Instructions(calls[0]));
        var italian = Lines(Instructions(calls[2]));
        var languageTexts = Enum.GetValues<MentorLanguage>()
            .SelectMany(l => typeof(MentorPromptTexts).GetProperties().Where(p => p.PropertyType == typeof(string))
                .Select(p => (string)p.GetValue(MentorPromptTexts.For(l))!)
                .Append(Line(l)))
            .ToList();
        french.Except(italian).Should().NotBeEmpty()
            .And.OnlyContain(l => languageTexts.Any(t => l.Contains(t, StringComparison.Ordinal) || t.Contains(l.Trim(), StringComparison.Ordinal)),
                "only the texts that follow the language differ between the two variants");
    }

    [Fact]
    public async Task In_a_French_turn_the_count_of_the_actions_left_to_the_user_stays_rewritten()
    {
        // Not signed in: the two actions that need a role are left out, and the role filter rewrites the count. The
        // variant is swapped in before it, so the rewritten count is the one sent.
        await using var host = MentorHostFixture.Create(o => { o.Language = MentorLanguage.Italian; o.MemoryAutoCapture = false; });
        host.Chat!.Say("OK.");

        await TurnAsync(host, "bonjour", MentorLanguage.French);

        // The actions the session is built with: not one shipped off (story 1549).
        var all = host.Get<MentorDiscoveryService>().Discover().Level1Methods.Where(m => m.EnabledByDefault).ToList();
        var mayRun = all.Count(m => m.ToolName is not ("cancel_order" or "export_ledger"));
        var sent = Instructions(CoordinatorCalls(host)[0]);
        sent.Should().Contain(Line(MentorLanguage.French))
            .And.Contain($"**{mayRun} direct actions**")
            .And.NotContain($"**{all.Count} direct actions**");
    }

    // ── Specialists and team members ────────────────────────────────────────────

    private sealed class ParcelActions { }

    private static async Task<List<FakeChatClient.RecordedCall>> SpecialistCallsAsync(
        MentorLanguage application, MentorLanguage? turn, string? ownInstructions = null)
    {
        await using var host = MentorHostFixture.Create(o => o.Language = application);
        host.Chat!.Respond((_, options) =>
        {
            var tools = options?.Tools?.Select(t => t.Name).ToList() ?? [];
            if ((options?.Instructions ?? "").Contains(ownInstructions ?? "You are **ParcelAgent**", StringComparison.Ordinal))
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Shipped.")) { ModelId = host.Chat.ModelId };
            if (tools.Count > 0 && tools.All(t => t.StartsWith("handoff_to", StringComparison.Ordinal)))
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("h1", tools[0])]))
                    { ModelId = host.Chat.ModelId };
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "SAFE")) { ModelId = host.Chat.ModelId };
        });
        var parcel = new AgentDiscoveryInfo(typeof(ParcelActions),
            new MentorAgentAttribute { Name = "ParcelAgent", Description = "Tracks parcels.", Instructions = ownInstructions }, []);
        var router = new Microsoft.Agents.AI.ChatClientAgent(host.Chat,
            new Microsoft.Agents.AI.ChatClientAgentOptions { Name = MentorPromptBuilder.HandoffRouterName });

        var workflow = host.Get<HandoffBuilder>().BuildHandoffAgent(host.Chat, router, [parcel]);
        host.Get<MentorTurnLanguage>().Requested = turn;
        await workflow!.RunAsync("where is my parcel?");

        var calls = host.Chat.Calls
            .Where(c => (c.Instructions ?? "").Contains(ownInstructions ?? "You are **ParcelAgent**", StringComparison.Ordinal)).ToList();
        calls.Should().NotBeEmpty("the specialist was reached. Warnings: {0}", host.Logs.WarningText);
        return calls;
    }

    [Fact]
    public async Task A_specialist_of_the_handoff_is_sent_its_instructions_in_the_language_of_the_turn()
    {
        var calls = await SpecialistCallsAsync(MentorLanguage.Italian, MentorLanguage.French);

        calls.Should().OnlyContain(c => c.Instructions!.Contains(MentorPromptTexts.French.Grounding)
                                        && c.Instructions.Contains(Line(MentorLanguage.French))
                                        && !c.Instructions.Contains(MentorPromptTexts.Italian.Grounding)
                                        && !c.Instructions.Contains(Line(MentorLanguage.Italian)));
    }

    [Fact]
    public async Task A_specialist_with_instructions_of_its_own_is_told_the_language_of_the_turn_only_when_it_differs()
    {
        const string own = "You track parcels for the shop.";

        var french = await SpecialistCallsAsync(MentorLanguage.Italian, MentorLanguage.French, own);
        var italian = await SpecialistCallsAsync(MentorLanguage.Italian, MentorLanguage.Italian, own);

        french.Should().OnlyContain(c => c.Instructions!.Contains(TurnLanguageChatClient.FallbackLine(MentorLanguage.French)));
        // The handoff workflow adds its own paragraph after the agent's instructions.
        italian.Should().OnlyContain(c => c.Instructions!.StartsWith(own, StringComparison.Ordinal) && !c.Instructions.Contains("for this turn"),
            "a host that writes its own prompt owns all of it");
    }

    [Fact]
    public async Task A_team_member_is_sent_its_grounding_rule_in_the_language_of_the_turn()
    {
        await using var host = MentorHostFixture.Create(o => { o.Language = MentorLanguage.Italian; o.MemoryAutoCapture = false; });
        host.Chat!.Respond((messages, options) =>
        {
            var instructions = options?.Instructions ?? "";
            var answered = messages.LastOrDefault()?.Contents.OfType<FunctionResultContent>().Any() == true;
            if (instructions.Contains("Your role in the team is: **Data Analyst**", StringComparison.Ordinal))
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Steady.")) { ModelId = host.Chat.ModelId };
            if (instructions.Contains("Your role in the team is: **Strategist**", StringComparison.Ordinal))
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Keep it. [TEAM:APPROVED]")) { ModelId = host.Chat.ModelId };
            if (options?.Tools?.Any(t => t.Name == "business_analysis_team") == true && !answered)
                return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("t1", "business_analysis_team", new Dictionary<string, object?> { ["task"] = "review the quarter" })]))
                    { ModelId = host.Chat.ModelId };
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "SAFE")) { ModelId = host.Chat.ModelId };
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await TurnAsync(host, "review the quarter", MentorLanguage.French, deadline.Token);

        var members = host.Chat.Calls.Where(c => (c.Instructions ?? "").Contains("Your role in the team is:", StringComparison.Ordinal)).ToList();
        members.Should().NotBeEmpty("the team ran. Warnings: {0}", host.Logs.WarningText);
        members.Should().OnlyContain(c => c.Instructions!.Contains(MentorPromptTexts.French.Grounding)
                                          && c.Instructions.Contains(Line(MentorLanguage.French))
                                          && !c.Instructions.Contains(MentorPromptTexts.Italian.Grounding));
    }

    // ── What the server writes for the turn ─────────────────────────────────────

    [Fact]
    public async Task A_declined_confirmation_in_a_French_turn_tells_the_model_what_to_do_in_French()
    {
        await using var host = MentorHostFixture.Create(o => o.Language = MentorLanguage.Italian);
        host.Chat!.CallTool("issue_refund", new { orderId = 1001 }).Say("OK.");
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req => state.Cancel(req.ActionId);

        await TurnAsync(host, "remboursez la commande 1001", MentorLanguage.French);

        var results = ToolResultsSeenBy(host);
        results.Should().Contain("\"declined_by_user\":true")
            .And.Contain(MentorPromptTexts.French.Declined)
            .And.NotContain(MentorPromptTexts.Italian.Declined);

        // The message the gate writes with it is the widget's text, in the language of the turn too.
        var declined = host.Chat.Calls.SelectMany(c => c.Messages).SelectMany(m => m.Contents).OfType<FunctionResultContent>()
            .Select(r => System.Text.Json.JsonDocument.Parse(r.Result?.ToString() ?? "{}").RootElement)
            .First(r => r.TryGetProperty("declined_by_user", out _));
        declined.GetProperty("message").GetString().Should().Be(new MentorLocalizer(MentorLanguage.French).Get("confirm.denied"));
    }

    [Fact]
    public async Task A_message_too_long_is_refused_in_the_language_of_the_turn()
    {
        await using var host = MentorHostFixture.Create(o => { o.Language = MentorLanguage.Italian; o.MaxMessageLength = 5; },
            withRecorder: true);

        await TurnAsync(host, "beaucoup trop long", MentorLanguage.French);

        host.Recorder!.Log.Where(e => e.Event == "NotifyError").Select(e => (string?)e.Args[0]).Should().ContainSingle()
            .Which.Should().Be(string.Format(new MentorLocalizer(MentorLanguage.French).Get("message.toolong"), 5));
    }

    [Fact]
    public async Task The_rule_for_citing_the_documents_is_in_the_language_of_the_turn()
    {
        var source = new ScriptedRagSource().Returns("La livraison prend 3 jours.", 9f, "Livraison");
        await using var host = MentorHostFixture.Create(
            o => { o.Language = MentorLanguage.Italian; o.UseRag = true; o.MemoryAutoCapture = false; },
            services => services.AddSingleton<IMentorRagSource>(source));
        host.Chat!.Say("OK.");

        await TurnAsync(host, "combien de temps pour la livraison ?", MentorLanguage.French);

        var prompt = Instructions(host.Chat.Calls[0]) + "\n" + host.Chat.Calls[0].SystemText;
        prompt.Should().Contain(new MentorLocalizer(MentorLanguage.French).Get("rag.cite"))
            .And.NotContain(new MentorLocalizer(MentorLanguage.Italian).Get("rag.cite"));
    }

    [Fact]
    public async Task The_confirmation_is_written_for_the_language_of_the_turn()
    {
        var seen = new List<MentorLanguage>();
        await using var host = MentorHostFixture.Create(o =>
        {
            o.Language = MentorLanguage.Italian;
            o.ConfirmationMessage = context => { seen.Add(context.Language); return null; };
        });
        host.Chat!.CallTool("issue_refund", new { orderId = 1001 }).Say("OK.");
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req => state.Cancel(req.ActionId);

        await TurnAsync(host, "remboursez la commande 1001", MentorLanguage.French);

        seen.Should().NotBeEmpty().And.OnlyContain(l => l == MentorLanguage.French);
    }

    [Fact]
    [Trait("RuntimeOption", nameof(MentorOptions.Language))]
    public async Task A_change_of_the_application_language_applies_to_the_next_turn_and_to_the_widget_texts()
    {
        await using var host = MentorHostFixture.Create(o => { o.Language = MentorLanguage.Italian; o.MemoryAutoCapture = false; });
        host.Chat!.SayRepeatedly("OK.", 2);
        await host.RunTurnAsync("ciao");

        host.Options.Language = MentorLanguage.German;
        await host.RunTurnAsync("hallo");

        Instructions(CoordinatorCalls(host)[^1]).Should().Contain(Line(MentorLanguage.German)).And.NotContain(Line(MentorLanguage.Italian));
        host.Get<MentorLocalizer>().Get("input.placeholder").Should().Be(new MentorLocalizer(MentorLanguage.German).Get("input.placeholder"));
    }

    // ── The swap, on its own ────────────────────────────────────────────────────

    private const string Built = "You are the assistant.\nALWAYS respond in **Italian**.";

    private static (TurnLanguageChatClient Client, FakeChatClient Inner, TestLogCollector Logs) Swap(MentorLanguage? turn)
    {
        var inner = new FakeChatClient();
        inner.SayRepeatedly("ok", 4);
        var logs = new TestLogCollector();
        var holder = new MentorTurnLanguage(Microsoft.Extensions.Options.Options.Create(new MentorOptions
        {
            AppName = "Test",
            ScanAssemblies = [typeof(TurnLanguageTests).Assembly],
            Language = MentorLanguage.Italian,
        })) { Requested = turn };
        var client = new TurnLanguageChatClient(inner, holder, MentorLanguage.Italian, Built,
            language => $"You are the assistant.\nALWAYS respond in **{language.ToFullName()}**.", "main_coordinator",
            new TestLoggerProvider(logs).CreateLogger("test"));
        return (client, inner, logs);
    }

    [Fact]
    public async Task Instructions_that_start_with_the_built_ones_are_sent_as_the_variant_with_what_follows_kept()
    {
        var (client, inner, logs) = Swap(MentorLanguage.French);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "bonjour")],
            new ChatOptions { Instructions = Built + "\n- Current page: Orders" });

        inner.Calls.Single().Instructions.Should().Be("You are the assistant.\nALWAYS respond in **French**.\n- Current page: Orders");
        logs.WarningText.Should().BeEmpty();
    }

    [Fact]
    public async Task Instructions_that_do_not_start_with_the_built_ones_get_the_line_of_the_turn_and_one_warning_per_session()
    {
        var (client, inner, logs) = Swap(MentorLanguage.French);
        var options = new ChatOptions { Instructions = "A preface of the host.\n" + Built };

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "bonjour")], options);
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "encore")], options);

        inner.Calls.Should().HaveCount(2).And.OnlyContain(c =>
            c.Instructions!.StartsWith("A preface of the host.\n" + Built, StringComparison.Ordinal)
            && c.Instructions.EndsWith(TurnLanguageChatClient.FallbackLine(MentorLanguage.French), StringComparison.Ordinal));
        logs.Records.Count(r => r.Level == LogLevel.Warning).Should().Be(1, "one warning per session, not one per request");
        options.Instructions.Should().Be("A preface of the host.\n" + Built, "the caller's options are not changed");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(MentorLanguage.Italian)]
    public async Task In_the_language_they_were_built_in_the_instructions_are_left_alone(MentorLanguage? turn)
    {
        var (client, inner, _) = Swap(turn);
        var options = new ChatOptions { Instructions = "A preface of the host.\n" + Built };

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "ciao")], options);

        inner.Calls.Single().Instructions.Should().Be("A preface of the host.\n" + Built);
    }

    // ── The language on the wire ────────────────────────────────────────────────

    [Theory]
    [InlineData("Italian", MentorLanguage.Italian)]
    [InlineData("italian", MentorLanguage.Italian)]
    [InlineData("it", MentorLanguage.Italian)]
    [InlineData("IT", MentorLanguage.Italian)]
    [InlineData(" fr ", MentorLanguage.French)]
    [InlineData("Portuguese", MentorLanguage.Portuguese)]
    [InlineData("zh", MentorLanguage.Chinese)]
    [InlineData("klingon", null)]
    [InlineData("5", null)]
    [InlineData("-1", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_language_from_the_wire_is_a_name_or_an_ISO_code_and_nothing_else(string? value, MentorLanguage? expected)
        => MentorTurnLanguage.Parse(value).Should().Be(expected);
}
