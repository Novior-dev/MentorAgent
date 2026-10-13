using FluentAssertions;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Tests.Harness;
using Xunit;

namespace Novior.MentorAgent.Tests.Hosted;

/// <summary>
/// BUG-050 — the hosted-tool domain gate judges a request against a scope that describes the
/// application's <em>data</em> and never the capabilities the operator switched on.
///
/// <para>
/// Found live with the Responses API enabled and all four hosted tools declared. Asked the README's
/// own headline example for image generation — <i>"draw a banner for the summer sale"</i>, phrased
/// with the application's name in it — the relevance filter picked the right tool
/// (<c>image_generation</c> scored 0.275 against a 0.15 floor) and the gate then threw it away:
/// <c>Hosted-tool domain check → OUT of scope</c>. The user was told the assistant cannot create
/// images, which reads as a broken feature rather than a policy decision.
/// </para>
///
/// <para>
/// The cause is structural, not a bad classifier. The derived scope is
/// <c>AppName + AppDescription + page names</c> — every one of which describes what the application
/// <em>holds</em> (products, orders, customers, reports). Drawing a banner is not in that list and
/// never can be, so a classifier asked "is this within that scope?" is right to say no. Meanwhile
/// the operator has expressed the opposite intent twice: once by enabling the tool at all, once by
/// enabling it on this application.
/// </para>
///
/// <para>
/// Why the existing suite could not see it: every test in <c>HostedToolCostControlTests</c> injects
/// the verdict through <c>domainVerdict</c>, so the scope string the classifier actually reads is
/// never exercised by anything. These tests assert on the scope itself.
/// </para>
/// </summary>
public sealed class HostedToolDomainScopeTests
{
    /// <summary>
    /// The scope as the operator sees it — the orchestrator logs it at startup precisely so a
    /// misconfigured gate can be diagnosed without a debugger.
    /// </summary>
    private static string LoggedScope(MentorHostFixture host)
    {
        var line = host.Logs.Records
            .Select(r => r.Message)
            .FirstOrDefault(m => m.Contains("Hosted-tool domain check active", StringComparison.Ordinal));

        line.Should().NotBeNull("the gate logs its scope at startup when it is enabled");
        return line!;
    }

    /// <summary>
    /// The gate is constructed inside <c>ToolFilterChatClient</c>, so it only exists when tool
    /// filtering is on and an embedding generator is present — which is also the only configuration
    /// where a hosted tool can survive to cost anything.
    /// </summary>
    private static MentorHostFixture WithHostedTools(MentorHostedTools tools,
                                                     Action<Novior.MentorAgent.Options.MentorOptions>? extra = null) =>
        MentorHostFixture.Create(o =>
        {
            o.EnableToolFiltering   = true;
            o.HostedTools           = tools;
            o.HostedToolDomainCheck = true;
            o.HostedImageModel      = "gpt-image-1-mini";
            extra?.Invoke(o);
        }, withEmbeddings: true);

    [Fact]
    public async Task The_scope_names_image_generation_when_the_operator_enabled_it()
    {
        // The live repro. Without this the classifier reads a description of orders and products and
        // correctly concludes that drawing a banner is unrelated — refusing the one request the
        // README uses to introduce the feature.
        await using var host = WithHostedTools(MentorHostedTools.ImageGeneration);
        host.Chat!.SayRepeatedly("ok", times: 2);
        await host.RunTurnAsync("hello");

        LoggedScope(host).Should().Contain("image",
            "an operator who switched image generation on has already said this assistant draws " +
            "things; a scope that cannot mention it makes the gate override that intent");
    }

    [Fact]
    public async Task The_scope_names_web_search_and_code_interpreter_when_they_are_enabled()
    {
        await using var host = WithHostedTools(
            MentorHostedTools.WebSearch | MentorHostedTools.CodeInterpreter);
        host.Chat!.SayRepeatedly("ok", times: 2);
        await host.RunTurnAsync("hello");

        var scope = LoggedScope(host);
        scope.Should().Contain("web");
        scope.Should().Contain("calcul", "the computation capability has to be nameable too");
    }

    [Fact]
    public async Task The_scope_still_describes_the_application_itself()
    {
        // The control. Naming the capabilities must ADD to the scope, not replace it — a scope that
        // listed only capabilities would let "draw me a dog" through, which is the exact request the
        // gate exists to refuse.
        await using var host = WithHostedTools(MentorHostedTools.ImageGeneration);
        host.Chat!.SayRepeatedly("ok", times: 2);
        await host.RunTurnAsync("hello");

        var scope = LoggedScope(host);
        scope.Should().Contain("TestApp");
        scope.Should().Contain("A test application");
    }

    [Fact]
    public async Task A_capability_that_was_not_enabled_is_not_advertised()
    {
        // The gate must not widen itself beyond what the operator actually turned on: claiming a
        // capability the model was never given would let the classifier approve a turn whose tool
        // does not exist, and the withheld-capability notice would then never fire.
        await using var host = WithHostedTools(MentorHostedTools.WebSearch);
        host.Chat!.SayRepeatedly("ok", times: 2);
        await host.RunTurnAsync("hello");

        LoggedScope(host).Should().NotContain("image",
            "image generation was never enabled on this host");
    }

    [Fact]
    public async Task A_custom_scope_still_replaces_the_derived_one_entirely()
    {
        // HostedToolDomainScope is documented as the override. Appending capabilities to a scope the
        // operator wrote by hand would quietly edit their sentence.
        await using var host = WithHostedTools(MentorHostedTools.ImageGeneration,
            o => o.HostedToolDomainScope = "Only questions about badgers.");
        host.Chat!.SayRepeatedly("ok", times: 2);
        await host.RunTurnAsync("hello");

        var scope = LoggedScope(host);
        scope.Should().Contain("Only questions about badgers.");
        scope.Should().NotContain("TestApp", "a custom scope replaces the derived one");
    }
}
