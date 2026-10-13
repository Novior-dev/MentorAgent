using Bunit;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Components;
using Novior.MentorAgent.Abstractions.Models;
using Xunit;

namespace Novior.MentorAgent.Tests.Components;

/// <summary>
/// The MCP and A2A badges, and their detail bars.
///
/// <para>
/// The MCP badge is the only component in the library with genuinely three-valued state: a server
/// is online, offline, or <see langword="null"/> = still connecting. Every mapping below is a
/// distinct CSS class, and "connecting" collapsing into "offline" is exactly the kind of bug that
/// looks like a broken integration to whoever is watching the badge.
/// </para>
/// </summary>
public sealed class StatusBadgeTests : ComponentTestBase
{
    private static IReadOnlyDictionary<string, bool?> Status(params (string Name, bool? State)[] servers)
        => servers.ToDictionary(s => s.Name, s => s.State);

    private IRenderedComponent<McpStatusBadge> RenderMcp(IReadOnlyDictionary<string, bool?> status)
    {
        Widget.ShowMcpStatus = true;
        Widget.HasMcpServers = true;
        return Render<McpStatusBadge>(p => p.Add(c => c.ServerStatus, status));
    }

    // ── MCP badge: three-valued state ───────────────────────────────────────────

    [Fact]
    public void All_servers_online_is_the_online_class()
    {
        var cut = RenderMcp(Status(("files", true), ("db", true)));

        (cut.Find("button").GetAttribute("class") ?? "").Should().Contain("bm-mcp-pill--online");
        cut.Find("button").GetAttribute("title").Should().Be("MCP — all servers online");
    }

    [Fact]
    public void All_servers_offline_is_the_offline_class()
    {
        var cut = RenderMcp(Status(("files", false), ("db", false)));

        (cut.Find("button").GetAttribute("class") ?? "").Should().Contain("bm-mcp-pill--offline");
    }

    [Fact]
    public void Some_online_and_some_offline_is_the_warn_class()
    {
        var cut = RenderMcp(Status(("files", true), ("db", false)));

        (cut.Find("button").GetAttribute("class") ?? "").Should().Contain("bm-mcp-pill--warn");
        cut.Find("button").GetAttribute("title").Should().Be("MCP — 1/2 servers online");
    }

    [Fact]
    public void Null_with_nothing_online_yet_is_connecting_not_offline()
    {
        // The distinction that matters: at startup every server is null, and showing red there
        // reports a failure that has not happened.
        var cut = RenderMcp(Status(("files", null), ("db", null)));

        var classes = cut.Find("button").GetAttribute("class") ?? "";
        classes.Should().Contain("bm-mcp-pill--connecting");
        classes.Should().NotContain("bm-mcp-pill--offline");
        cut.Find("button").GetAttribute("title").Should().Be("MCP — connecting…");
    }

    [Fact]
    public void Connecting_stops_being_connecting_as_soon_as_one_server_is_up()
    {
        // Mixed null + online: the connecting state is defined as "nothing up yet", so this must
        // fall through to warn. Otherwise a permanently-null server would pin the badge to
        // "connecting" forever while the working ones are usable.
        var cut = RenderMcp(Status(("files", true), ("db", null)));

        var classes = cut.Find("button").GetAttribute("class") ?? "";
        classes.Should().Contain("bm-mcp-pill--warn");
        classes.Should().NotContain("bm-mcp-pill--connecting");
    }

    [Fact]
    public void Null_mixed_with_offline_is_still_connecting()
    {
        var cut = RenderMcp(Status(("files", null), ("db", false)));

        (cut.Find("button").GetAttribute("class") ?? "").Should().Contain("bm-mcp-pill--connecting");
    }

    [Fact]
    public void A_single_server_is_labelled_by_name_and_several_by_count()
    {
        RenderMcp(Status(("filesystem", true))).Find(".bm-mcp-pill__name").TextContent
            .Should().Be("filesystem", "with one server its name is more useful than 1/1");

        RenderMcp(Status(("a", true), ("b", false), ("c", true))).Find(".bm-mcp-pill__name").TextContent
            .Should().Be("2/3");
    }

    [Fact]
    public void Server_names_are_matched_case_insensitively()
    {
        // The status dictionary is rebuilt with an ordinal-ignore-case comparer, so a server
        // reporting as "Files" must not appear twice alongside "files".
        var cut = RenderMcp(Status(("Files", true)));

        cut.Find(".bm-mcp-pill__name").TextContent.Should().Be("Files");
        cut.FindAll("button").Should().HaveCount(1);
    }

    [Fact]
    public void Clicking_the_badge_raises_the_toggle()
    {
        Widget.ShowMcpStatus = true;
        Widget.HasMcpServers = true;
        var toggled = 0;

        var cut = Render<McpStatusBadge>(p => p
            .Add(c => c.ServerStatus, Status(("files", true)))
            .Add(c => c.OnToggle, () => toggled++));

        cut.Find("button").Click();

        toggled.Should().Be(1);
    }

    // ── MCP badge: the three ways it renders nothing ────────────────────────────

    [Fact]
    public void Nothing_renders_when_the_option_is_off()
    {
        Widget.ShowMcpStatus = false;
        Widget.HasMcpServers = true;

        Render<McpStatusBadge>(p => p.Add(c => c.ServerStatus, Status(("files", true))))
            .Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void Nothing_renders_when_no_servers_are_configured()
    {
        Widget.ShowMcpStatus = true;
        Widget.HasMcpServers = false;

        Render<McpStatusBadge>(p => p.Add(c => c.ServerStatus, Status(("files", true))))
            .Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void Nothing_renders_before_any_status_has_arrived()
    {
        // Configured but not yet reporting. An empty badge with no label is worse than no badge.
        Widget.ShowMcpStatus = true;
        Widget.HasMcpServers = true;

        Render<McpStatusBadge>().Markup.Trim().Should().BeEmpty();
    }

    // ── A2A badge ───────────────────────────────────────────────────────────────

    [Fact]
    public void The_a2a_badge_names_a_single_agent_and_counts_several()
    {
        Widget.ShowA2AStatus = true;
        Widget.HasRemoteAgents = true;
        Widget.RemoteAgentDisplays = [new AgentDisplayInfo { Name = "Billing", AgentCardUrl = "https://b.test/.well-known/agent.json" }];

        Render<A2AStatusBadge>().Find(".bm-a2a-pill__name").TextContent.Should().Be("Billing");

        Widget.RemoteAgentDisplays.Add(new AgentDisplayInfo { Name = "Shipping", AgentCardUrl = "https://s.test/card" });
        Render<A2AStatusBadge>().Find(".bm-a2a-pill__name").TextContent.Should().Be("2 agents");
    }

    [Fact]
    public void The_a2a_badge_hides_when_either_switch_is_off()
    {
        Widget.RemoteAgentDisplays = [new AgentDisplayInfo { Name = "Billing", AgentCardUrl = "https://b.test/c" }];

        Widget.ShowA2AStatus = false;
        Widget.HasRemoteAgents = true;
        Render<A2AStatusBadge>().Markup.Trim().Should().BeEmpty();

        Widget.ShowA2AStatus = true;
        Widget.HasRemoteAgents = false;
        Render<A2AStatusBadge>().Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void Clicking_the_a2a_badge_raises_the_toggle()
    {
        Widget.ShowA2AStatus = true;
        Widget.HasRemoteAgents = true;
        Widget.RemoteAgentDisplays = [new AgentDisplayInfo { Name = "Billing", AgentCardUrl = "https://b.test/c" }];
        var toggled = 0;

        var cut = Render<A2AStatusBadge>(p => p.Add(c => c.OnToggle, () => toggled++));
        cut.Find("button").Click();

        toggled.Should().Be(1);
    }

    // ── Detail bars ─────────────────────────────────────────────────────────────

    [Fact]
    public void The_mcp_detail_bar_lists_every_server_with_its_own_state_word()
    {
        Widget.HasMcpServers = true;

        var cut = Render<McpDetailBar>(p => p
            .Add(c => c.Visible, true)
            .Add(c => c.ServerStatus, Status(("up", true), ("down", false), ("pending", null))));

        cut.FindAll(".bm-mcp-detail__state").Select(e => e.TextContent)
            .Should().Equal(["online", "offline", "connecting"]);
        cut.FindAll(".bm-mcp-detail__name").Select(e => e.TextContent)
            .Should().Equal(["up", "down", "pending"]);
    }

    [Fact]
    public void The_mcp_detail_bar_renders_nothing_while_hidden()
    {
        Widget.HasMcpServers = true;

        Render<McpDetailBar>(p => p
            .Add(c => c.Visible, false)
            .Add(c => c.ServerStatus, Status(("up", true))))
            .Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void Closing_the_mcp_detail_bar_raises_the_callback()
    {
        Widget.HasMcpServers = true;
        var closed = 0;

        var cut = Render<McpDetailBar>(p => p
            .Add(c => c.Visible, true)
            .Add(c => c.ServerStatus, Status(("up", true)))
            .Add(c => c.OnClose, () => closed++));

        cut.Find(".bm-mcp-detail__close").Click();

        closed.Should().Be(1);
    }

    [Fact]
    public void The_a2a_detail_bar_shows_each_agents_name_and_card_url()
    {
        Widget.RemoteAgentDisplays =
        [
            new AgentDisplayInfo { Name = "Billing", AgentCardUrl = "https://b.test/.well-known/agent.json" },
            new AgentDisplayInfo { Name = "Shipping", AgentCardUrl = "https://s.test/.well-known/agent.json" },
        ];

        var cut = Render<A2ADetailBar>(p => p.Add(c => c.Visible, true));

        cut.FindAll(".bm-a2a-detail__name").Select(e => e.TextContent).Should().Equal(["Billing", "Shipping"]);
        cut.FindAll(".bm-a2a-detail__url").Select(e => e.TextContent)
            .Should().Equal(["https://b.test/.well-known/agent.json", "https://s.test/.well-known/agent.json"]);
    }

    [Fact]
    public void The_a2a_detail_bar_renders_nothing_with_no_agents()
    {
        Render<A2ADetailBar>(p => p.Add(c => c.Visible, true)).Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void An_agent_name_containing_markup_is_rendered_as_text()
    {
        // The name comes off a remote agent card, which is a third party's JSON — the one place
        // in this component where the string is not written by the host application.
        Widget.RemoteAgentDisplays =
        [
            new AgentDisplayInfo { Name = "<script>alert(1)</script>", AgentCardUrl = "https://x.test/c" },
        ];

        var cut = Render<A2ADetailBar>(p => p.Add(c => c.Visible, true));

        cut.Find(".bm-a2a-detail").QuerySelectorAll("script").Should().BeEmpty();
        cut.Find(".bm-a2a-detail__name").TextContent.Should().Be("<script>alert(1)</script>");
    }
}
