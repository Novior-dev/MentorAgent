using System.IO;
using Bunit;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Components;
using Novior.MentorAgent.Abstractions.Core;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Tests.Components;
using Novior.MentorAgent.Tests.Docs;
using Novior.MentorAgent.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Localisation;

/// <summary>
/// BUG-049 — the MCP and A2A badge strings were the only widget copy built from literals instead of
/// from the localiser table.
///
/// <para>
/// Found live on the NuGet parity pass, on an English-configured deployment whose MCP pill read
/// <c>"MCP — tutti i server online"</c>. Two components render that badge and the two disagreed on
/// language: <c>ChatWidget</c> carried the Italian copy, the standalone <c>McpStatusBadge</c> the
/// English. Whichever language a host chose, at least one of the two was wrong — and for the eight
/// other languages the library ships, both were.
/// </para>
///
/// <para>
/// B17's localisation suite could not see this. It checks that every <c>L.Get("…")</c> in all five
/// packages resolves to a real key, which is the right question for a string that goes *through* the
/// table — and no question at all for one that never does. So the sweep below is the complement:
/// no component source may contain the badge copy as a literal.
/// </para>
/// </summary>
public sealed class BadgeLocalisationTests : ComponentTestBase
{
    private readonly RecordingStateService _state = new();

    public BadgeLocalisationTests()
    {
        Services.AddSingleton<IMentorStateService>(_state);
        Services.AddSingleton<IMentorOrchestrator>(new StubOrchestrator());
        Services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));

        Widget.ShowMcpStatus   = true;
        Widget.HasMcpServers   = true;
        Widget.McpServerNames  = ["filesystem", "memory"];
        Widget.ShowA2AStatus   = true;
        Widget.HasRemoteAgents = true;
        Widget.RemoteAgentDisplays = [new AgentDisplayInfo { Name = "ShopFlowRemote" }];
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, RepoFiles.Solution)))
            dir = dir.Parent;

        dir.Should().NotBeNull("the tests must be able to find the repository root");
        return dir!.FullName;
    }

    [Theory]
    [InlineData(MentorLanguage.English,  "MCP — connecting…")]
    [InlineData(MentorLanguage.Italian,  "MCP — connessione in corso…")]
    [InlineData(MentorLanguage.German,   "MCP — Verbindung wird aufgebaut…")]
    [InlineData(MentorLanguage.Japanese, "MCP — 接続しています…")]
    public void The_mcp_pill_speaks_the_configured_language(MentorLanguage language, string expected)
    {
        // Before the fix this returned the Italian string for all ten.
        Widget.Language = language;
        var cut = Render<ChatWidget>();
        cut.Find(".bm-fab").Click();

        cut.Find(".bm-mcp-pill").GetAttribute("title").Should().Be(expected);
    }

    [Theory]
    [InlineData(MentorLanguage.English, "Remote A2A agents — click for details")]
    [InlineData(MentorLanguage.Italian, "Agenti A2A remoti — clicca per i dettagli")]
    [InlineData(MentorLanguage.Spanish, "Agentes A2A remotos — haz clic para ver detalles")]
    public void The_a2a_pill_speaks_the_configured_language(MentorLanguage language, string expected)
    {
        Widget.Language = language;
        var cut = Render<ChatWidget>();
        cut.Find(".bm-fab").Click();

        cut.Find(".bm-a2a-pill").GetAttribute("title").Should().Be(expected);
    }

    [Fact]
    public void No_component_carries_the_badge_copy_as_a_literal()
    {
        // The structural half. A literal is invisible to every check that starts from L.Get, so
        // this one starts from the other end: the phrases themselves must not appear in source.
        var components = Path.Combine(RepositoryRoot(), RepoFiles.Abstractions, "Components");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(components, "*.razor*", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            // Anchored so the patterns cannot match the localiser *keys* that replaced them —
            // "a2a.agents" contains the word and is exactly what this file wants to see.
            foreach (var literal in new[] { "\"MCP — ", "$\"MCP — ", "Remote A2A Agents", "} agents\"" })
                if (text.Contains(literal, StringComparison.Ordinal))
                    offenders.Add($"{Path.GetFileName(file)} contains {literal}");
        }

        string.Join(" | ", offenders).Should().BeEmpty(
            "badge copy belongs in MentorLocalizer like every other widget string; a literal here " +
            "ships one language to all ten audiences");
    }

    [Fact]
    public void No_component_names_a_control_with_a_literal()
    {
        // BUG-257: the close buttons of the MCP and A2A details said "Chiudi", by title alone, in every language. A name
        // or a title written in the markup is one language for all ten: every one comes from the localiser, or from
        // the application (a server's or an agent's name), always through an expression.
        var components = Path.Combine(RepositoryRoot(), RepoFiles.Abstractions, "Components");
        var literal = new System.Text.RegularExpressions.Regex(@"\b(title|aria-label)=""(?!@)[^""]*""");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(components, "*.razor", SearchOption.AllDirectories))
            foreach (System.Text.RegularExpressions.Match m in literal.Matches(File.ReadAllText(file)))
                offenders.Add($"{Path.GetFileName(file)}: {m.Value}");

        string.Join(" | ", offenders).Should().BeEmpty("a control's name and title speak the widget's language");
    }

    [Theory]
    [InlineData(MentorLanguage.English, "Close")]
    [InlineData(MentorLanguage.Japanese, "閉じる")]
    public void The_close_buttons_of_the_MCP_and_A2A_details_are_named_in_the_widgets_language(MentorLanguage language, string close)
    {
        Widget.Language = language;
        var cut = Render<ChatWidget>();
        cut.Find(".bm-fab").Click();
        cut.Find(".bm-mcp-pill").Click();
        cut.Find(".bm-a2a-pill").Click();

        foreach (var button in new[] { cut.Find(".bm-mcp-detail__close"), cut.Find(".bm-a2a-detail__close") })
        {
            button.GetAttribute("aria-label").Should().Be(close, "an icon-only button is named by its aria-label");
            button.GetAttribute("title").Should().Be(close);
        }

        Widget.Language = language;
        var mcp = Render<McpDetailBar>(p => p.Add(c => c.Visible, true)
            .Add(c => c.ServerStatus, new Dictionary<string, bool?> { ["filesystem"] = true }));
        mcp.Find(".bm-mcp-detail__close").GetAttribute("aria-label").Should().Be(close, "the component on its own too");
        Render<A2ADetailBar>(p => p.Add(c => c.Visible, true))
            .Find(".bm-a2a-detail__close").GetAttribute("aria-label").Should().Be(close);
    }

    /// <summary>Does nothing; this file is about markup, not turns.</summary>
    private sealed class StubOrchestrator : IMentorOrchestrator
    {
        public Task SendMessageAsync(string userMessage, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task SendMessageAsync(string userMessage, IReadOnlyList<MentorAttachment>? attachments,
                                     CancellationToken ct = default)
            => Task.CompletedTask;

        public void CancelCurrentRequest() { }

        public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<System.Text.Json.JsonElement?> SerializeSessionAsync(CancellationToken ct = default)
            => Task.FromResult<System.Text.Json.JsonElement?>(null);

        public Task RestoreSessionAsync(System.Text.Json.JsonElement snapshot, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task RespondToApprovalAsync(ConfirmationRequest req, bool approved, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
