using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Novior.MentorAgent.Observability;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Novior.MentorAgent.Tests.Docs;

/// <summary>
/// Names that look like the namespace and are not. A host, a dashboard or a snapshot saved by an earlier
/// version holds them, so they stay as they are when the namespace changes (1.0.0: MentorAgent becomes
/// Novior.MentorAgent). A rename that reached one of them would compile and break the host in silence:
/// the exporter receives nothing, every saved snapshot reads as unreadable, <c>RequireCors("MentorAgentMcp")</c>
/// finds no policy, a hand-written <c>index.html</c> loads no widget.
///
/// <para>
/// Each value is read from the library at run time, so a rename that changed it there fails here. The
/// Data Protection purpose is spelled in pieces: written whole, it matches the namespace a rename looks
/// for, and a tool that rewrote it in the library would rewrite this test with it.
/// </para>
/// </summary>
public sealed class ContractNameTests
{
    [Fact]
    public async Task Telemetry_keeps_the_MentorAgent_source_and_the_mentoragent_metric_names()
    {
        // Hosts subscribe with .AddSource("MentorAgent").AddMeter("MentorAgent") and chart these names.
        new MentorOptions { AppName = "x", ScanAssemblies = [] }.ObservabilitySourceName.Should().Be("MentorAgent");

        var instruments = new ConcurrentBag<string>();
        var tags = new ConcurrentBag<string>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name != "MentorAgent") return;
                instruments.Add(instrument.Name);
                l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((_, _, measured, _) =>
        {
            foreach (var tag in measured) tags.Add(tag.Key);
        });
        listener.Start();

        await using var host = MentorHostFixture.Create(o => o.EnableObservability = true);
        host.Get<MentorTelemetry>().ActivitySource.Name.Should().Be("MentorAgent");

        host.Chat!.CallTool("count_open_orders").Say("There are two.");
        await host.RunTurnAsync("How many open orders?");

        instruments.Should().Contain(
            ["mentoragent.turns", "mentoragent.tool_calls", "mentoragent.errors", "mentoragent.guardrail_blocks"]);
        tags.Should().Contain("mentoragent.tool");
    }

    [Fact]
    public async Task A_blank_source_name_falls_back_to_MentorAgent()
    {
        await using var host = MentorHostFixture.Create(o =>
        {
            o.EnableObservability = true;
            o.ObservabilitySourceName = " ";
        });

        var telemetry = host.Get<MentorTelemetry>();
        telemetry.ActivitySource.Name.Should().Be("MentorAgent");
        telemetry.Meter.Name.Should().Be("MentorAgent");
    }

    [Fact]
    public async Task Session_snapshots_keep_their_purpose_and_their_markers()
    {
        // Snapshots have travelled protected since rc.14 (BUG-110): a new purpose or a new marker leaves every
        // snapshot a client already saved unreadable.
        await using var app = await ServerAppFactory.CreateAsync();
        await using var hub = app.CreateHubConnection();
        await hub.StartAsync();
        await hub.InvokeAsync("SendMessage", "hello", null);

        var saved = await app.Client.GetFromJsonAsync<JsonElement>($"/mentor/session?connectionId={hub.ConnectionId}");
        saved.GetProperty("mentorAgentServerSession").GetInt32().Should().Be(1);

        var purpose = string.Join('.', "MentorAgent", "Server", "SessionSnapshot");
        var core = app.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(purpose, "v1", "anonymous")
            .Unprotect(saved.GetProperty("protected").GetString()!);
        JsonDocument.Parse(core).RootElement.TryGetProperty("mentorAgentSession", out _).Should().BeTrue();

        // The server tells a core snapshot sent back unprotected by the same marker, to say why it refuses it.
        using var refused = await app.Client.PostAsync($"/mentor/session?connectionId={hub.ConnectionId}",
            new StringContent("""{"mentorAgentSession":1,"session":{}}""", Encoding.UTF8, "application/json"));
        (await refused.Content.ReadAsStringAsync()).Should().Contain("saved unprotected");
    }

    [Fact]
    public async Task The_MCP_CORS_policy_keeps_its_name()
    {
        // Hosts with a browser-based MCP client opt in with RequireCors("MentorAgentMcp"), as the README shows.
        await using var app = await ServerAppFactory.CreateAsync(configure: o => o.McpServerEnabled = true);

        app.Services.GetRequiredService<IOptions<CorsOptions>>().Value
            .GetPolicy("MentorAgentMcp").Should().NotBeNull();
    }

    [Fact]
    public void The_widget_assets_keep_their_file_names_and_their_global()
    {
        // A WASM or MAUI host copies these paths into its index.html; the components call window.MentorAgent.
        var wwwroot = Path.Combine(RepoFiles.Root, RepoFiles.Abstractions, "wwwroot");
        File.Exists(Path.Combine(wwwroot, "css", "MentorAgent.css")).Should().BeTrue();
        File.ReadAllText(Path.Combine(wwwroot, "js", "MentorAgent.js")).Should().Contain("window.MentorAgent = {");

        var widget = File.ReadAllText(Path.Combine(RepoFiles.Root, RepoFiles.Abstractions, "Components", "ChatWidget.razor"));
        widget.Should().Contain("/css/MentorAgent.css?v=").And.Contain("/js/MentorAgent.js?v=");
        RepoFiles.SourceText.Should().Contain("InvokeVoidAsync(\"MentorAgent.");
    }
}
