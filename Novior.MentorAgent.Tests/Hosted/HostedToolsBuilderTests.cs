using FluentAssertions;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Models;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Xunit;

namespace Novior.MentorAgent.Tests.Hosted;

/// <summary>
/// <see cref="HostedToolsBuilder"/> — which provider-hosted tools are declared, and which are
/// dropped.
///
/// <para>
/// Hosted tools are the one part of the surface MentorAgent cannot supervise: the provider runs
/// them on its own infrastructure, so nothing reaches the local gate — no role check, no
/// confirmation, no per-tool metrics. Everything protective therefore has to happen at declaration
/// time, which is this class. A tool declared by mistake is a capability granted with no way to take
/// it back mid-turn.
/// </para>
///
/// <para>
/// The second reason this matters is that the returned <c>Active</c> set drives the widget badge.
/// A tool that failed validation must not appear there: a badge claiming web search that the model
/// was never given is worse than no badge, because it is believed.
/// </para>
/// </summary>
public sealed class HostedToolsBuilderTests
{
    private static MentorOptions Options(Action<MentorOptions>? configure = null)
    {
        var options = new MentorOptions
        {
            AppName = "TestApp",
            ScanAssemblies = [typeof(HostedToolsBuilderTests).Assembly],
            // Hosted tools travel in ChatOptions.Tools, which only exists on Path A. Without a
            // client the builder drops everything — that branch has its own test.
            ChatClient = new FakeChatClient(),
        };

        configure?.Invoke(options);
        return options;
    }

    private static (List<AITool> Tools, MentorHostedTools Active, TestLogCollector Logs) Build(
        Action<MentorOptions>? configure = null)
    {
        var logs = new TestLogCollector();
        var logger = new TestLoggerProvider(logs).CreateLogger("hosted");
        var (tools, active) = HostedToolsBuilder.Build(Options(configure), logger);
        return (tools, active, logs);
    }

    private static MentorHostedMcpServer Server(
        string name = "docs", string url = "https://mcp.example.com/sse",
        Action<MentorHostedMcpServer>? configure = null)
    {
        var server = new MentorHostedMcpServer { Name = name, Url = url };
        configure?.Invoke(server);
        return server;
    }

    /// <summary>Options that satisfy every tool's prerequisites, so a flag test is about the flag.</summary>
    private static void FullyConfigured(MentorOptions o)
    {
        o.FileSearchVectorStoreIds = ["vs_1"];
        o.HostedMcpServers = [Server()];
    }

    // ── Nothing asked for ───────────────────────────────────────────────────────

    [Fact]
    public void No_hosted_tools_declares_nothing()
    {
        var (tools, active, _) = Build(o => o.HostedTools = MentorHostedTools.None);

        tools.Should().BeEmpty();
        active.Should().Be(MentorHostedTools.None);
    }

    [Fact]
    public void With_nothing_enabled_nothing_is_logged_about_hosted_tools()
    {
        // The startup log is read by whoever is diagnosing "unsupported tool" from a provider. A
        // line about hosted tools when none are configured sends them the wrong way.
        var (_, _, logs) = Build(o => o.HostedTools = MentorHostedTools.None);

        logs.Records.Should().NotContain(r => r.Message.Contains("Hosted tools enabled"));
    }

    // ── Each flag on its own ────────────────────────────────────────────────────

    [Fact]
    public void Web_search_declares_the_provider_web_search_tool()
    {
        var (tools, active, _) = Build(o => o.HostedTools = MentorHostedTools.WebSearch);

        tools.Should().ContainSingle().Which.Should().BeOfType<HostedWebSearchTool>();
        active.Should().Be(MentorHostedTools.WebSearch);
    }

    [Fact]
    public void Code_interpreter_declares_the_provider_sandbox()
    {
        var (tools, active, _) = Build(o => o.HostedTools = MentorHostedTools.CodeInterpreter);

        tools.Should().ContainSingle().Which.Should().BeOfType<HostedCodeInterpreterTool>();
        active.Should().Be(MentorHostedTools.CodeInterpreter);
    }

    [Fact]
    public void File_search_declares_the_configured_vector_stores()
    {
        var (tools, active, _) = Build(o =>
        {
            o.HostedTools = MentorHostedTools.FileSearch;
            o.FileSearchVectorStoreIds = ["vs_1", "vs_2"];
        });

        var fileSearch = tools.Should().ContainSingle().Which.Should().BeOfType<HostedFileSearchTool>().Subject;
        fileSearch.Inputs.Should().HaveCount(2);
        active.Should().Be(MentorHostedTools.FileSearch);
    }

    [Fact]
    public void Image_generation_declares_the_provider_image_tool()
    {
        var (tools, active, _) = Build(o => o.HostedTools = MentorHostedTools.ImageGeneration);

        tools.Should().ContainSingle().Which.Should().BeOfType<HostedImageGenerationTool>();
        active.Should().Be(MentorHostedTools.ImageGeneration);
    }

    [Fact]
    public void Hosted_mcp_declares_one_tool_per_server()
    {
        var (tools, active, _) = Build(o =>
        {
            o.HostedTools = MentorHostedTools.HostedMcp;
            o.HostedMcpServers = [Server("docs"), Server("issues", "https://issues.example.com/mcp")];
        });

        tools.Should().AllBeOfType<HostedMcpServerTool>().And.HaveCount(2);
        active.Should().Be(MentorHostedTools.HostedMcp);
    }

    // ── Every combination ───────────────────────────────────────────────────────

    [Fact]
    public void Every_flag_combination_declares_exactly_what_it_asked_for()
    {
        // All 32 subsets of the five flags. Combinations are where a `HasFlag` written as `==`
        // hides: any single-flag test passes either way, and the bug only appears when two are set
        // together — which is the normal configuration for anyone using this feature.
        var all = new[]
        {
            MentorHostedTools.WebSearch, MentorHostedTools.CodeInterpreter,
            MentorHostedTools.FileSearch, MentorHostedTools.ImageGeneration,
            MentorHostedTools.HostedMcp,
        };

        var failures = new List<string>();

        for (var mask = 0; mask < 32; mask++)
        {
            var requested = MentorHostedTools.None;
            for (var bit = 0; bit < all.Length; bit++)
                if ((mask & (1 << bit)) != 0) requested |= all[bit];

            var (tools, active, _) = Build(o =>
            {
                FullyConfigured(o);
                o.HostedTools = requested;
            });

            if (active != requested)
                failures.Add($"{requested}: active was {active}");

            var expectedCount = all.Count(f => requested.HasFlag(f) && f != MentorHostedTools.None);
            if (requested != MentorHostedTools.None && tools.Count != expectedCount)
                failures.Add($"{requested}: {tools.Count} tool(s), expected {expectedCount}");
        }

        string.Join(Environment.NewLine, failures).Should().BeEmpty();
    }

    [Fact]
    public void A_tool_that_failed_validation_is_not_claimed_as_active()
    {
        // The badge reads Active. File search is enabled but unconfigured, so the model never gets
        // it — and the widget must not tell the user it has it.
        var (tools, active, _) = Build(o =>
        {
            o.HostedTools = MentorHostedTools.WebSearch | MentorHostedTools.FileSearch;
            o.FileSearchVectorStoreIds = [];
        });

        active.Should().Be(MentorHostedTools.WebSearch);
        active.HasFlag(MentorHostedTools.FileSearch).Should().BeFalse();
        tools.Should().ContainSingle();
    }

    [Fact]
    public void One_failed_tool_does_not_take_the_others_with_it()
    {
        var (tools, active, _) = Build(o =>
        {
            o.HostedTools = MentorHostedTools.WebSearch | MentorHostedTools.CodeInterpreter
                          | MentorHostedTools.HostedMcp;
            o.HostedMcpServers = [];
        });

        active.Should().Be(MentorHostedTools.WebSearch | MentorHostedTools.CodeInterpreter);
        tools.Should().HaveCount(2);
    }

    // ── File search details ─────────────────────────────────────────────────────

    [Fact]
    public void Blank_vector_store_ids_count_as_none()
    {
        // A configuration bound from appsettings can easily produce ["", " "]. Declaring a file
        // search scoped to nothing is the unscoped case the fail-closed rule exists to prevent.
        var (tools, active, logs) = Build(o =>
        {
            o.HostedTools = MentorHostedTools.FileSearch;
            o.FileSearchVectorStoreIds = ["", "   "];
        });

        tools.Should().BeEmpty();
        active.Should().Be(MentorHostedTools.None);
        logs.HasWarning("FileSearchVectorStoreIds is empty").Should().BeTrue(logs.WarningText);
    }

    [Fact]
    public void Blank_ids_are_dropped_but_the_real_ones_survive()
    {
        var (tools, _, _) = Build(o =>
        {
            o.HostedTools = MentorHostedTools.FileSearch;
            o.FileSearchVectorStoreIds = ["vs_1", "", "vs_2"];
        });

        tools.OfType<HostedFileSearchTool>().Single().Inputs.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(5, 5)]
    [InlineData(1, 1)]
    public void A_positive_result_cap_is_applied(int configured, int expected)
    {
        var (tools, _, _) = Build(o =>
        {
            o.HostedTools = MentorHostedTools.FileSearch;
            o.FileSearchVectorStoreIds = ["vs_1"];
            o.FileSearchMaxResults = configured;
        });

        tools.OfType<HostedFileSearchTool>().Single().MaximumResultCount.Should().Be(expected);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_result_cap_leaves_the_provider_default(int configured)
    {
        // Zero would mean "search and return nothing", which no caller can have meant.
        var (tools, _, _) = Build(o =>
        {
            o.HostedTools = MentorHostedTools.FileSearch;
            o.FileSearchVectorStoreIds = ["vs_1"];
            o.FileSearchMaxResults = configured;
        });

        tools.OfType<HostedFileSearchTool>().Single().MaximumResultCount.Should().BeNull();
    }

    // ── Image generation ────────────────────────────────────────────────────────

    [Fact]
    public void The_image_media_type_is_a_full_mime_type()
    {
        // Load-bearing. Microsoft.Extensions.AI converts this into the OpenAI output-file-format
        // with no null check, so leaving it unset throws ArgumentNullException while the request is
        // being assembled — before any network call, on every single turn. "png" fails exactly like
        // null; only a full MIME type works. Verified against Azure OpenAI, 2026-07-31.
        var (tools, _, _) = Build(o => o.HostedTools = MentorHostedTools.ImageGeneration);

        tools.OfType<HostedImageGenerationTool>().Single().Options!.MediaType.Should().Be("image/png");
    }

    [Fact]
    public void The_azure_header_requirement_is_stated_at_startup()
    {
        // Azure resolves the image deployment from a request header, and MentorAgent is handed an
        // already-built IChatClient so it cannot add one. Said once at startup, because the
        // alternative is a 400 mid-turn whose message never reaches the developer.
        var (_, _, logs) = Build(o => o.HostedTools = MentorHostedTools.ImageGeneration);

        string.Join("\n", logs.Records.Select(r => r.Message))
              .Should().Contain("x-ms-oai-image-generation-deployment");
    }

    [Fact]
    public void An_image_model_is_passed_through_when_set()
    {
        var (tools, _, _) = Build(o =>
        {
            o.HostedTools = MentorHostedTools.ImageGeneration;
            o.HostedImageModel = "gpt-image-1";
        });

        tools.OfType<HostedImageGenerationTool>().Single().Options!.ModelId.Should().Be("gpt-image-1");
    }

    [Fact]
    public void No_image_model_leaves_the_provider_to_choose()
    {
        var (tools, _, _) = Build(o => o.HostedTools = MentorHostedTools.ImageGeneration);

        tools.OfType<HostedImageGenerationTool>().Single().Options!.ModelId.Should().BeNull();
    }

    [Theory]
    [InlineData("1024x1024", 1024, 1024)]
    [InlineData("1024X768", 1024, 768)]
    [InlineData("512×512", 512, 512)]
    [InlineData(" 256 x 256 ", 256, 256)]
    public void A_well_formed_image_size_is_parsed(string configured, int width, int height)
    {
        var (tools, _, _) = Build(o =>
        {
            o.HostedTools = MentorHostedTools.ImageGeneration;
            o.HostedImageSize = configured;
        });

        var size = tools.OfType<HostedImageGenerationTool>().Single().Options!.ImageSize;
        size.Should().NotBeNull();
        size!.Value.Width.Should().Be(width);
        size.Value.Height.Should().Be(height);
    }

    [Theory]
    [InlineData("1024")]
    [InlineData("big")]
    [InlineData("1024x")]
    [InlineData("0x0")]
    [InlineData("-1x100")]
    [InlineData("1024x1024x1024")]
    public void A_malformed_image_size_warns_and_falls_back(string configured)
    {
        // A typo in a size must not stop the application from starting — but it must not be
        // swallowed either, or the developer spends the afternoon wondering why every image is
        // 1024 square.
        var (tools, _, logs) = Build(o =>
        {
            o.HostedTools = MentorHostedTools.ImageGeneration;
            o.HostedImageSize = configured;
        });

        tools.OfType<HostedImageGenerationTool>().Single().Options!.ImageSize.Should().BeNull();
        logs.HasWarning("is not in WIDTHxHEIGHT form").Should().BeTrue(logs.WarningText);
    }

    [Fact]
    public void An_unset_image_size_is_not_an_error()
    {
        var (tools, _, logs) = Build(o => o.HostedTools = MentorHostedTools.ImageGeneration);

        tools.OfType<HostedImageGenerationTool>().Single().Options!.ImageSize.Should().BeNull();
        logs.HasWarning("WIDTHxHEIGHT").Should().BeFalse();
    }

    // ── Hosted MCP ──────────────────────────────────────────────────────────────

    [Fact]
    public void A_server_with_a_relative_url_is_skipped_and_reported()
    {
        // The provider dials this address, so a relative one can only fail mid-turn with an opaque
        // provider error.
        var (tools, active, logs) = Build(o =>
        {
            o.HostedTools = MentorHostedTools.HostedMcp;
            o.HostedMcpServers = [Server("docs", "/mcp")];
        });

        tools.Should().BeEmpty();
        active.Should().Be(MentorHostedTools.None);
        logs.HasWarning("is not an absolute URL").Should().BeTrue(logs.WarningText);
    }

    [Fact]
    public void A_valid_server_survives_a_broken_neighbour()
    {
        var (tools, active, logs) = Build(o =>
        {
            o.HostedTools = MentorHostedTools.HostedMcp;
            o.HostedMcpServers = [Server("broken", "not a url"), Server("docs")];
        });

        tools.Should().ContainSingle();
        active.Should().Be(MentorHostedTools.HostedMcp);
        logs.HasWarning("'broken' was skipped").Should().BeTrue(logs.WarningText);
    }

    [Fact]
    public void A_server_with_no_name_or_no_url_is_ignored()
    {
        var (tools, _, _) = Build(o =>
        {
            o.HostedTools = MentorHostedTools.HostedMcp;
            o.HostedMcpServers = [Server("", "https://a.example.com"), Server("b", "  "), Server("ok")];
        });

        tools.OfType<HostedMcpServerTool>().Select(t => t.ServerName).Should().Equal("ok");
    }

    [Fact]
    public void The_allowed_tool_list_is_passed_to_the_provider()
    {
        // For a server you do not control this list is the only limit on what the provider may
        // call, and it is enforced there — nothing local can intercept it.
        var (tools, _, _) = Build(o =>
        {
            o.HostedTools = MentorHostedTools.HostedMcp;
            o.HostedMcpServers = [Server(configure: s => s.AllowedTools = ["search_docs", "get_page"])];
        });

        tools.OfType<HostedMcpServerTool>().Single().AllowedTools
             .Should().BeEquivalentTo(["search_docs", "get_page"]);
    }

    [Fact]
    public void No_allowed_list_means_every_tool_the_server_offers()
    {
        var (tools, _, _) = Build(o =>
        {
            o.HostedTools = MentorHostedTools.HostedMcp;
            o.HostedMcpServers = [Server()];
        });

        tools.OfType<HostedMcpServerTool>().Single().AllowedTools.Should().BeNull();
    }

    [Fact]
    public void Headers_are_passed_to_the_provider()
    {
        var (tools, _, _) = Build(o =>
        {
            o.HostedTools = MentorHostedTools.HostedMcp;
            o.HostedMcpServers = [Server(configure: s => s.Headers =
                new Dictionary<string, string> { ["Authorization"] = "Bearer t" })];
        });

        tools.OfType<HostedMcpServerTool>().Single().Headers
             .Should().ContainKey("Authorization");
    }

    [Fact]
    public void The_server_description_reaches_the_provider()
    {
        var (tools, _, _) = Build(o =>
        {
            o.HostedTools = MentorHostedTools.HostedMcp;
            o.HostedMcpServers = [Server(configure: s => s.Description = "Product documentation")];
        });

        tools.OfType<HostedMcpServerTool>().Single().ServerDescription.Should().Be("Product documentation");
    }

    // ── MCP approval ────────────────────────────────────────────────────────────

    [Fact]
    public void Approval_is_required_by_default()
    {
        // The provider is the MCP client, so this is the only place a call can be intercepted at
        // all. Defaulting to "ask" is the only safe default for a remote server.
        var (tools, _, _) = Build(o =>
        {
            o.HostedTools = MentorHostedTools.HostedMcp;
            o.HostedMcpServers = [Server()];
        });

        tools.OfType<HostedMcpServerTool>().Single().ApprovalMode
             .Should().Be(HostedMcpServerToolApprovalMode.AlwaysRequire);
    }

    [Fact]
    public void Turning_approval_off_is_allowed_but_reported()
    {
        var (tools, _, logs) = Build(o =>
        {
            o.HostedTools = MentorHostedTools.HostedMcp;
            o.HostedMcpServers = [Server(configure: s => s.RequireApproval = false)];
        });

        tools.OfType<HostedMcpServerTool>().Single().ApprovalMode
             .Should().Be(HostedMcpServerToolApprovalMode.NeverRequire);
        logs.HasWarning("runs with no approval").Should().BeTrue(logs.WarningText);
    }

    [Fact]
    public void A_per_tool_list_overrides_the_blanket_flag()
    {
        // That is the point of configuring one: RequireApproval = false plus an "always" list means
        // "trust this server except for these", and a blanket NeverRequire would silently drop the
        // exception.
        var (tools, _, logs) = Build(o =>
        {
            o.HostedTools = MentorHostedTools.HostedMcp;
            o.HostedMcpServers = [Server(configure: s =>
            {
                s.RequireApproval = false;
                s.AlwaysRequireApprovalTools = ["delete_page"];
            })];
        });

        tools.OfType<HostedMcpServerTool>().Single().ApprovalMode
             .Should().NotBe(HostedMcpServerToolApprovalMode.NeverRequire);
        logs.HasWarning("runs with no approval").Should().BeFalse(
            "an explicit approval list is not the same as no approval at all");
    }

    [Fact]
    public void A_never_list_also_counts_as_configured_approval()
    {
        var (tools, _, _) = Build(o =>
        {
            o.HostedTools = MentorHostedTools.HostedMcp;
            o.HostedMcpServers = [Server(configure: s => s.NeverRequireApprovalTools = ["search_docs"])];
        });

        tools.OfType<HostedMcpServerTool>().Single().ApprovalMode
             .Should().NotBe(HostedMcpServerToolApprovalMode.AlwaysRequire);
    }

    // ── Startup diagnostics ─────────────────────────────────────────────────────

    [Fact]
    public void The_active_set_is_named_at_startup()
    {
        // Hosted tools fail at the provider, not in our code, so the configured set has to be
        // visible when a call comes back "unsupported tool".
        var (_, _, logs) = Build(o =>
            o.HostedTools = MentorHostedTools.WebSearch | MentorHostedTools.CodeInterpreter);

        string.Join("\n", logs.Records.Select(r => r.Message))
              .Should().Contain("Hosted tools enabled").And.Contain("WebSearch");
    }

    [Fact]
    public void Combining_hosted_tools_with_model_routing_is_flagged()
    {
        // Routing sends the same tool list to whichever model serves the turn, so a strong model on
        // a client that does not support hosted tools fails only on escalated turns —
        // intermittently, which is far harder to diagnose than a startup error.
        var (_, _, logs) = Build(o =>
        {
            o.HostedTools = MentorHostedTools.WebSearch;
            o.StrongChatClient = new FakeChatClient();
        });

        logs.HasWarning("combined with model routing").Should().BeTrue(logs.WarningText);
    }

    [Fact]
    public void Routing_is_not_flagged_when_no_hosted_tool_survived()
    {
        // Warning about a combination the user is not getting is noise, and noise is what makes the
        // real warnings unread.
        var (_, _, logs) = Build(o =>
        {
            o.HostedTools = MentorHostedTools.FileSearch;
            o.FileSearchVectorStoreIds = [];
            o.StrongChatClient = new FakeChatClient();
        });

        logs.HasWarning("combined with model routing").Should().BeFalse();
    }
}
