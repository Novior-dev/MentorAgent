using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Attributes;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Harness;
using Novior.MentorAgent.Utility;
using Xunit;

namespace Novior.MentorAgent.Tests.Prompt;

/// <summary>
/// The instructions MentorAgent writes for the model, in the configured <c>Language</c> (story 191).
///
/// <para>
/// They were English in every language, examples included, and a local model copied one into an Italian reply:
/// "Understood, l'email è stata annullata". The rules and examples the model can echo, the grounding rules and the
/// instructions in the tool results now come in the language of the turn; the rest of the prompt stays English, and
/// so does the line that names the reply language. English itself must not move by a byte: the measures behind its
/// wording belong to that text (<see cref="EnglishPromptGolden"/>).
/// </para>
///
/// <para>
/// The builder takes the language from its caller and never reads it from the options, so a turn can be given the
/// instructions of a language other than the configured one.
/// </para>
/// </summary>
public sealed class PromptLanguageTests
{
    private sealed class GoldenActions
    {
        public void CancelOrder(int orderId) { }
        public int CountOrders() => 0;
    }

    private static MethodDiscoveryInfo Method(string name, string tool, string description, bool readOnly = false, bool confirm = false)
        => new(typeof(GoldenActions), typeof(GoldenActions).GetMethod(name)!, tool, description, "Orders",
               confirm, null, [], null, ReadOnly: readOnly);

    private static AgentDiscoveryInfo Specialist() => new(
        typeof(GoldenActions),
        new MentorAgentAttribute { Name = "ShippingAgent", Description = "Tracks shipments." },
        [Method(nameof(GoldenActions.CountOrders), "count_orders", "Counts the orders.", readOnly: true)]);

    private static TeamMemberInfo Member() => new(
        typeof(GoldenActions),
        new TeamMemberAttribute { Role = "Analyst", Instructions = "Interpret the numbers." },
        [Method(nameof(GoldenActions.CountOrders), "count_orders", "Counts the orders.", readOnly: true)]);

    /// <summary>Built by hand, so a type added to the test application does not move the golden texts.</summary>
    private static DiscoveryResult Discovery()
    {
        var discovery = new DiscoveryResult();
        discovery.Pages.Add(new PageDiscoveryInfo("/orders", "Orders", "The order list", false, 0));
        discovery.Level1Methods.Add(Method(nameof(GoldenActions.CancelOrder), "cancel_order", "Cancels an order.", confirm: true));
        discovery.Level2Agents.Add(Specialist());
        discovery.Level3Teams.Add(new TeamDiscoveryInfo(
            typeof(GoldenActions),
            new MentorTeamAttribute { Name = "Review Team", Description = "Reviews large refunds.", TriggerOn = ["refunds over 500"] },
            [Member()],
            null));
        return discovery;
    }

    private static MentorOptions Standard(MentorLanguage language) => new()
    {
        AppName = "Golden",
        ScanAssemblies = [],
        Language = language,
        MentorshipLevel = MentorshipLevel.Standard,
        EnableRichResponses = false,
        UseMemoryContext = false,
    };

    /// <summary>Every localized piece of the coordinator's prompt is in this one.</summary>
    private static MentorOptions Proactive(MentorLanguage language) => new()
    {
        AppName = "Golden",
        ScanAssemblies = [],
        Language = language,
        AppDescription = "An order back-office.",
        MentorshipLevel = MentorshipLevel.Proactive,
        EnableRichResponses = true,
        UseMemoryContext = true,
        MemoryAutoCapture = false,
    };

    private static MentorPromptBuilder Builder(MentorOptions options)
        => new(Microsoft.Extensions.Options.Options.Create(options));

    /// <summary>In the configured language, as the orchestrator asks for it.</summary>
    private static string Coordinator(MentorOptions options)
        => Builder(options).BuildCoordinatorInstructions(Discovery(), options.Language);

    private static string SpecialistPrompt(MentorOptions options)
        => Builder(options).BuildAgentInstructions(Specialist(), options.Language);

    private static string MemberPrompt(MentorOptions options)
        => Builder(options).BuildTeamMemberInstructions(Member(), options.Language);

    /// <summary>Line endings follow the checkout; the bytes between them are the claim.</summary>
    private static string Lf(string text) => text.ReplaceLineEndings("\n");

    public static TheoryData<MentorLanguage> AllLanguages() => [.. Enum.GetValues<MentorLanguage>()];

    public static TheoryData<MentorLanguage> OtherLanguages()
        => [.. Enum.GetValues<MentorLanguage>().Where(l => l != MentorLanguage.English)];

    /// <summary>The example sentences of the English instructions: what a model copies into a reply.</summary>
    private static readonly string[] EnglishExamples =
    [
        "I'll cancel order #1005 now.",
        "Understood, I won't proceed.",
        "Sure, I'll leave it as is.",
        "I'll search for your order",
        "I'll call get_order_status",
        "I can only assist with tasks within my configured capabilities.",
        "Do you want me to",
    ];

    /// <summary>The English rules that follow the language, by an unmistakable stretch of each.</summary>
    private static readonly string[] EnglishRules =
    [
        "always write one short sentence describing what you are about to do",
        "When the user cancels or rejects a confirmation dialog",
        "Values of this application's own records",
        "describe what you are doing in plain terms",
        "If asked to act outside your defined scope",
        "End with the answer.",
    ];

    private const string EnglishGrounding = "State only facts a tool returned in this conversation";

    /// <summary>The texts of <see cref="MentorPromptTexts"/>, by key.</summary>
    private static readonly PropertyInfo[] Keys = typeof(MentorPromptTexts)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.PropertyType == typeof(string))
        .ToArray();

    private static string Text(MentorLanguage language, PropertyInfo key) => (string)key.GetValue(MentorPromptTexts.For(language))!;

    /// <summary>The prompt texts, which the model reads in its instructions; the rest are written into tool results.</summary>
    private static readonly string[] PromptKeys =
    [
        nameof(MentorPromptTexts.ConfirmationPreface), nameof(MentorPromptTexts.DeclinedConfirmation),
        nameof(MentorPromptTexts.CoordinatorGrounding), nameof(MentorPromptTexts.PlainTerms),
        nameof(MentorPromptTexts.OutOfScope), nameof(MentorPromptTexts.ProactiveNoNextSteps), nameof(MentorPromptTexts.Grounding),
        nameof(MentorPromptTexts.ActionsNotAvailable), nameof(MentorPromptTexts.SignInMayHelp),
        nameof(MentorPromptTexts.NotAvailableFollowUp), nameof(MentorPromptTexts.AnnouncedActionNotRun),
        nameof(MentorPromptTexts.AnnouncedActionCall), nameof(MentorPromptTexts.RagNothingFound),
    ];

    // ── English, byte for byte ──────────────────────────────────────────────────

    [Fact]
    public void In_english_the_coordinator_prompt_is_the_one_before_byte_for_byte()
    {
        Lf(Coordinator(Standard(MentorLanguage.English))).Should().Be(Lf(EnglishPromptGolden.CoordinatorStandard));
        Lf(Coordinator(Proactive(MentorLanguage.English))).Should().Be(Lf(EnglishPromptGolden.CoordinatorProactive));
    }

    [Fact]
    public void In_english_the_specialist_and_team_member_prompts_are_the_ones_before_byte_for_byte()
    {
        Lf(SpecialistPrompt(Standard(MentorLanguage.English))).Should().Be(Lf(EnglishPromptGolden.Specialist));
        Lf(MemberPrompt(Standard(MentorLanguage.English))).Should().Be(Lf(EnglishPromptGolden.TeamMember));
    }

    [Fact]
    public void In_english_the_tool_results_are_the_ones_before_byte_for_byte()
    {
        const MentorLanguage en = MentorLanguage.English;
        MentorUtils.ToolDeclined("M", en).Should().Be(EnglishPromptGolden.DeclinedResult);
        MentorUtils.ToolStopped("M", en).Should().Be(EnglishPromptGolden.StoppedResult);
        MentorUtils.ToolConfirmationTimedOut("M", en).Should().Be(EnglishPromptGolden.TimedOutResult);
        MentorUtils.ToolRanResultWithheld("M", en).Should().Be(EnglishPromptGolden.ResultWithheldResult);
        MentorUtils.ToolLeftOpen(declined: true, en).Should().Be(EnglishPromptGolden.LeftOpenDeclinedResult);
        MentorUtils.ToolLeftOpen(declined: false, en).Should().Be(EnglishPromptGolden.LeftOpenStoppedResult);
        MentorUtils.ToolOutcomeUnknown(en).Should().Be(EnglishPromptGolden.OutcomeUnknownResult);
    }

    // ── The language is the caller's ───────────────────────────────────────────

    [Fact]
    public void The_builder_writes_the_language_it_is_given_not_the_configured_one()
    {
        // What lets a turn have its own language: the configured one is only what the orchestrator passes today.
        var configuredItalian = Proactive(MentorLanguage.Italian);

        var french = Builder(configuredItalian).BuildCoordinatorInstructions(Discovery(), MentorLanguage.French);
        french.Should().Contain("ALWAYS respond in **French**").And.NotContain("**Italian**");
        french.Should().Contain(MentorPromptTexts.French.ConfirmationPreface).And.NotContain(MentorPromptTexts.Italian.ConfirmationPreface);

        Lf(Builder(configuredItalian).BuildCoordinatorInstructions(Discovery(), MentorLanguage.English))
            .Should().Be(Lf(EnglishPromptGolden.CoordinatorProactive),
                "given English, the builder writes the English prompt whatever the options say");
        Builder(Standard(MentorLanguage.Italian)).BuildAgentInstructions(Specialist(), MentorLanguage.German)
            .Should().Contain("ALWAYS respond in **German**").And.Contain(MentorPromptTexts.German.Grounding);
        Builder(Standard(MentorLanguage.Italian)).BuildTeamMemberInstructions(Member(), MentorLanguage.Japanese)
            .Should().Contain("ALWAYS respond in **Japanese**").And.Contain(MentorPromptTexts.Japanese.Grounding);
    }

    // ── The other nine languages ────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(OtherLanguages))]
    public void The_coordinator_prompt_carries_no_english_example_or_rule_of_those_that_follow_the_language(MentorLanguage language)
    {
        // "Understood, l'email è stata annullata": the rule on a declined confirmation gave its examples in English.
        var prompt = Coordinator(Proactive(language));

        foreach (var english in EnglishExamples.Concat(EnglishRules))
            prompt.Should().NotContain(english, "in {0} the instructions the model can echo are in {0}", language);
        prompt.Should().Contain($"ALWAYS respond in **{language.ToFullName()}**",
            "the line naming the reply language is the one that stays English");
    }

    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void The_coordinator_prompt_carries_each_of_its_texts_in_the_language(MentorLanguage language)
    {
        var texts = MentorPromptTexts.For(language);
        var proactive = Coordinator(Proactive(language));

        proactive.Should().Contain(texts.ConfirmationPreface)
            .And.Contain(texts.DeclinedConfirmation)
            .And.Contain(texts.CoordinatorGrounding)
            .And.Contain(texts.PlainTerms)
            .And.Contain(texts.OutOfScope)
            .And.Contain("- " + texts.ProactiveNoNextSteps);
        Coordinator(Standard(language)).Should().Contain(texts.CoordinatorGrounding).And.NotContain(texts.ProactiveNoNextSteps,
            "the closing rule is Proactive's");
    }

    [Theory]
    [MemberData(nameof(OtherLanguages))]
    public void The_specialist_and_team_member_grounding_rule_follows_the_language(MentorLanguage language)
    {
        var grounding = MentorPromptTexts.For(language).Grounding;

        SpecialistPrompt(Standard(language)).Should().NotContain(EnglishGrounding).And.Contain(grounding);
        MemberPrompt(Standard(language)).Should().NotContain(EnglishGrounding).And.Contain(grounding);
    }

    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void Every_tool_result_carries_its_instruction_in_the_language_under_the_same_keys(MentorLanguage language)
    {
        var texts = MentorPromptTexts.For(language);
        const string message = "L'azione \"x\" non è stata eseguita.";
        (string Json, string Instruction)[] results =
        [
            (MentorUtils.ToolDeclined(message, language), $"{texts.Declined} {texts.DeclinedRule}"),
            (MentorUtils.ToolStopped(message, language), texts.Stopped),
            (MentorUtils.ToolConfirmationTimedOut(message, language), texts.TimedOut),
            (MentorUtils.ToolRanResultWithheld(message, language), texts.ResultWithheld),
            (MentorUtils.ToolLeftOpen(declined: true, language), texts.LeftOpenDeclined),
            (MentorUtils.ToolLeftOpen(declined: false, language), texts.LeftOpenStopped),
            (MentorUtils.ToolOutcomeUnknown(language), texts.OutcomeUnknown),
        ];
        string[][] englishKeys =
        [
            .. new[]
            {
                EnglishPromptGolden.DeclinedResult, EnglishPromptGolden.StoppedResult, EnglishPromptGolden.TimedOutResult,
                EnglishPromptGolden.ResultWithheldResult, EnglishPromptGolden.LeftOpenDeclinedResult,
                EnglishPromptGolden.LeftOpenStoppedResult, EnglishPromptGolden.OutcomeUnknownResult,
            }.Select(PropertyNames),
        ];

        for (var i = 0; i < results.Length; i++)
        {
            using var json = JsonDocument.Parse(results[i].Json);
            json.RootElement.GetProperty("instruction").GetString().Should().Be(results[i].Instruction, "result {0} in {1}", i, language);
            PropertyNames(results[i].Json).Should().Equal(englishKeys[i], "the keys are symbols, the same in every language");
            if (json.RootElement.TryGetProperty("message", out var said))
                said.GetString().Should().Be(message);
        }
    }

    private static string[] PropertyNames(string json)
    {
        using var document = JsonDocument.Parse(json);
        return [.. document.RootElement.EnumerateObject().Select(p => p.Name)];
    }

    // ── Completeness of the ten sets ────────────────────────────────────────────

    [Fact]
    public void Every_language_has_its_own_set_of_texts()
    {
        var sets = Enum.GetValues<MentorLanguage>().Select(MentorPromptTexts.For).ToList();

        sets.Distinct().Should().HaveCount(sets.Count, "no language falls back to another's texts");
        MentorPromptTexts.For((MentorLanguage)99).Should().BeSameAs(MentorPromptTexts.English);
    }

    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void Every_text_is_there_and_none_is_left_in_english(MentorLanguage language)
    {
        foreach (var key in Keys)
        {
            var text = Text(language, key);
            text.Should().NotBeNullOrWhiteSpace("{0} of {1}", key.Name, language);
            if (language == MentorLanguage.English) continue;

            text.Should().NotBe(Text(MentorLanguage.English, key), "{0} of {1} is the English text", key.Name, language);
            Regex.IsMatch(text, @"\b(the|you|your|user|this|not)\b", RegexOptions.IgnoreCase)
                .Should().BeFalse("{0} of {1} holds an English word: {2}", key.Name, language, text);
        }
    }

    [Theory]
    [MemberData(nameof(OtherLanguages))]
    public void The_symbols_are_the_same_in_every_language(MentorLanguage language)
    {
        // What the model or the code reads as a symbol is not translated: result markers, a tool name, the order of
        // the example, the format placeholders.
        Text(language, Key(nameof(MentorPromptTexts.DeclinedConfirmation))).Should().Contain("not_run").And.Contain("outcome_unknown");
        Text(language, Key(nameof(MentorPromptTexts.PlainTerms))).Should().Contain("get_order_status");
        Text(language, Key(nameof(MentorPromptTexts.ConfirmationPreface))).Should().Contain("#1005");

        foreach (var key in Keys)
            Placeholders(Text(language, key)).Should().Equal(Placeholders(Text(MentorLanguage.English, key)),
                "{0} of {1}", key.Name, language);
    }

    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void A_tool_result_text_can_be_written_into_json_as_it_is(MentorLanguage language)
    {
        foreach (var key in Keys.Where(k => !PromptKeys.Contains(k.Name)))
        {
            Text(language, key).Should().NotContainAny(["\"", "\\"], "{0} of {1}", key.Name, language);
            Text(language, key).Any(char.IsControl).Should().BeFalse("{0} of {1}", key.Name, language);
        }
    }

    private static PropertyInfo Key(string name) => Keys.Single(k => k.Name == name);

    private static string[] Placeholders(string text) => [.. Regex.Matches(text, @"\{\d+\}").Select(m => m.Value)];

    // ── Through the host ────────────────────────────────────────────────────────

    /// <summary>Every tool result the model was handed across the whole conversation, joined.</summary>
    private static string ToolResultsSeenBy(MentorHostFixture host)
        => string.Join(" | ", host.Chat!.Calls
            .SelectMany(c => c.Messages)
            .SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>()
            .Select(r => r.Result?.ToString() ?? string.Empty));

    [Theory]
    [InlineData(MentorLanguage.Italian)]
    [InlineData(MentorLanguage.Japanese)]
    public async Task The_coordinator_is_sent_its_instructions_in_the_configured_language(MentorLanguage language)
    {
        await using var host = MentorHostFixture.Create(o => { o.Language = language; o.MemoryAutoCapture = false; });
        host.Chat!.Say("OK.");

        await host.RunTurnAsync("hello");

        var instructions = host.Chat.Calls.First(c => c.ToolNames.Count > 0).Instructions ?? "";
        instructions.Should().Contain($"ALWAYS respond in **{language.ToFullName()}**")
            .And.Contain(MentorPromptTexts.For(language).CoordinatorGrounding)
            .And.Contain(MentorPromptTexts.For(language).DeclinedConfirmation);
    }

    [Theory]
    [InlineData(MentorLanguage.Italian)]
    [InlineData(MentorLanguage.Japanese)]
    public async Task A_specialist_of_the_handoff_is_sent_its_grounding_rule_in_the_configured_language(MentorLanguage language)
    {
        // Built by the handoff builder itself: the test application's only specialist writes its own Instructions,
        // which replace the generated ones in every language, so a turn through the host never shows them.
        await using var host = MentorHostFixture.Create(o => o.Language = language);
        host.Chat!.Respond((_, options) =>
        {
            var tools = options?.Tools?.Select(t => t.Name).ToList() ?? [];
            if ((options?.Instructions ?? "").Contains("You are **ShippingAgent**", StringComparison.Ordinal))
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Shipped.")) { ModelId = host.Chat.ModelId };
            if (tools.Count > 0 && tools.All(t => t.StartsWith("handoff_to", StringComparison.Ordinal)))
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("h1", tools[0])]))
                    { ModelId = host.Chat.ModelId };
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "SAFE")) { ModelId = host.Chat.ModelId };
        });
        var shipping = new AgentDiscoveryInfo(typeof(GoldenActions),
            new MentorAgentAttribute { Name = "ShippingAgent", Description = "Tracks shipments." }, []);
        var router = new Microsoft.Agents.AI.ChatClientAgent(host.Chat,
            new Microsoft.Agents.AI.ChatClientAgentOptions { Name = MentorPromptBuilder.HandoffRouterName });

        var workflow = host.Get<HandoffBuilder>().BuildHandoffAgent(host.Chat, router, [shipping]);
        await workflow!.RunAsync("where is my parcel?");

        var specialist = host.Chat.Calls.Where(c => (c.Instructions ?? "").Contains("You are **ShippingAgent**", StringComparison.Ordinal)).ToList();
        specialist.Should().NotBeEmpty("the specialist was reached. Warnings: {0}", host.Logs.WarningText);
        specialist.Should().OnlyContain(c => c.Instructions!.Contains(MentorPromptTexts.For(language).Grounding)
                                             && c.Instructions.Contains($"ALWAYS respond in **{language.ToFullName()}**")
                                             && !c.Instructions.Contains(EnglishGrounding));
    }

    [Theory]
    [InlineData(MentorLanguage.Italian)]
    [InlineData(MentorLanguage.Japanese)]
    public async Task A_team_member_is_sent_its_grounding_rule_in_the_configured_language(MentorLanguage language)
    {
        await using var host = MentorHostFixture.Create(o => { o.Language = language; o.MemoryAutoCapture = false; });
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

        await host.RunTurnAsync("review the quarter", deadline.Token);

        var members = host.Chat.Calls.Where(c => (c.Instructions ?? "").Contains("Your role in the team is:", StringComparison.Ordinal)).ToList();
        members.Should().NotBeEmpty("the team ran. Warnings: {0}", host.Logs.WarningText);
        members.Should().OnlyContain(c => (c.Instructions ?? "").Contains(MentorPromptTexts.For(language).Grounding)
                                          && !(c.Instructions ?? "").Contains(EnglishGrounding));
    }

    [Theory]
    [InlineData(MentorLanguage.Italian)]
    [InlineData(MentorLanguage.Japanese)]
    public async Task In_Native_mode_a_declined_approval_tells_the_model_what_to_do_in_the_language(MentorLanguage language)
    {
        // The orchestrator answers the approval request, not the gate; Microsoft.Extensions.AI puts its own English
        // sentence before the reason, which the doc of Language says.
        await using var host = MentorHostFixture.Create(o =>
        {
            o.Language = language;
            o.HitlMode = MentorHitlMode.Native;
            o.MemoryAutoCapture = false;
        });
        host.Chat!.CallTool("issue_refund", new { orderId = 1001 }).Say("OK.");
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req => state.Cancel(req.ActionId);

        await host.RunTurnAsync("refund order 1001");

        var results = ToolResultsSeenBy(host);
        results.Should().Contain("\"declined_by_user\":true").And.Contain(MentorPromptTexts.For(language).Declined);
        results.Should().NotContain("The user chose not to run this action");
    }

    [Theory]
    [MemberData(nameof(OtherLanguages))]
    public async Task A_declined_confirmation_tells_the_model_what_to_do_in_the_language(MentorLanguage language)
    {
        await using var host = MentorHostFixture.Create(o => o.Language = language);
        host.Chat!.CallTool("issue_refund", new { orderId = 1001 }).Say("OK.");
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req => state.Cancel(req.ActionId);

        await host.RunTurnAsync("refund order 1001");

        var results = ToolResultsSeenBy(host);
        results.Should().Contain("\"declined_by_user\":true");
        results.Should().NotContain("The user chose not to run this action").And.NotContain("Tell the user in one short sentence");
        results.Should().Contain(MentorPromptTexts.For(language).Declined);
    }

    [Theory]
    [MemberData(nameof(OtherLanguages))]
    public async Task An_action_left_open_by_a_Stop_is_closed_for_the_next_turn_in_the_language(MentorLanguage language)
    {
        // Native mode: Stop right after the confirmation leaves the approval request open, and the next turn answers
        // it as never run (BUG-116, BUG-204) — the orchestrator's result, not the gate's.
        await using var host = MentorHostFixture.Create(o =>
        {
            o.MemoryAutoCapture = false;
            o.HitlMode = MentorHitlMode.Native;
            o.Language = language;
        });
        host.Auth.SignIn("ada", "Manager");
        host.Chat!.Respond((_, _) => new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("call-7", "cancel_order", new Dictionary<string, object?> { ["orderId"] = 7, ["reason"] = "test" })]))
            { ModelId = host.Chat.ModelId })
            .Say("Two open orders.");
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req =>
        {
            _ = state.ConfirmAsync(req.ActionId);
            host.Orchestrator.CancelCurrentRequest();
        };

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await host.RunTurnAsync("cancel order 7", deadline.Token);
        await host.RunTurnAsync("how many open orders?", deadline.Token);

        var results = ToolResultsSeenBy(host);
        results.Should().Contain("\"not_run\":true");
        results.Should().NotContain("never ran").And.NotContain("answer the user's latest message");
        results.Should().Contain(MentorPromptTexts.For(language).LeftOpenStopped);
    }
}
