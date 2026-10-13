using System.Net.Http.Json;
using A2A;
using FluentAssertions;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Federation;

/// <summary>
/// BUG-097 — on a Blazor Server host every A2A task told the operator their sign-in was broken.
///
/// <para>
/// Found live on 23 September 2026, driving the published rc.11's <c>MentorAgentNuget</c> as the A2A
/// <b>peer</b> for BUG-083. An A2A task has no signed-in user by design — the README says so, and the
/// agent card leaves every role-gated action out for that reason. But on Blazor Server the
/// <see cref="AuthenticationStateProvider"/> DI hands out cannot even be read outside a circuit: it
/// throws "Do not call GetAuthenticationStateAsync outside of the DI scope for a Razor component".
/// The memory service caught that and logged BUG-072's warning — "check the provider can be read
/// from a scoped service", advice for a real misconfiguration — three times per task; and a
/// role-gated tool the model reached for anyway was logged at <b>Error</b>, with a stack trace, and
/// answered "Error during authorization check". The outcome was right both times (anonymous, refused);
/// what the operator read was a fault that does not exist, on every task.
/// </para>
/// </summary>
public sealed class A2ANoSignedInUserTests
{
    /// <summary>What Blazor Server's provider does outside a circuit.</summary>
    private sealed class OutsideACircuitAuthenticationStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            throw new InvalidOperationException(
                "Do not call GetAuthenticationStateAsync outside of the DI scope for a Razor component.");
    }

    private static Task<ServerAppFactory> HostAsync() => ServerAppFactory.CreateAsync(
        configure: o =>
        {
            o.A2AServerEnabled = true;
            o.A2AServerUrl = "http://localhost/a2a";
            o.UseMemoryContext = true;
            o.MemoryAutoCapture = false;
        },
        configureServices: s => s.AddScoped<AuthenticationStateProvider, OutsideACircuitAuthenticationStateProvider>(),
        configureApp: a => a.UseEndpoints(e => e.MapMentorAgentA2A()));

    private static async Task<A2A.SendMessageResponse> AskOverA2AAsync(ServerAppFactory app, string text)
    {
        var card = await new A2A.A2ACardResolver(new Uri("http://localhost"), app.Client).GetAgentCardAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return await A2A.A2AClientFactory.Create(card, app.Client)
            .SendMessageAsync(text, A2A.Role.User, null, deadline.Token);
    }

    [Fact]
    public async Task An_a2a_task_is_anonymous_without_blaming_the_hosts_sign_in()
    {
        await using var app = await HostAsync();
        app.Chat.Say("Otto prodotti a catalogo.");

        await AskOverA2AAsync(app, "quanti prodotti ci sono a catalogo?");

        app.Logs.HasWarning("AuthenticationStateProvider threw").Should().BeFalse(
            "a task from another agent has no signed-in user by design; the provider is not broken. Warnings:{0}{1}",
            Environment.NewLine, app.Logs.WarningText);
    }

    [Fact]
    public async Task A_role_gated_tool_on_an_a2a_task_is_refused_as_a_missing_login_not_as_an_error()
    {
        await using var app = await HostAsync();
        app.Chat.CallTool("export_ledger").Say("Serve un accesso.");

        await AskOverA2AAsync(app, "esporta il registro ordini");

        app.Logs.Records.Should().NotContain(r => r.Level >= LogLevel.Error,
            "nothing failed: an anonymous caller asked for a gated action. Errors:{0}{1}",
            Environment.NewLine, string.Join(Environment.NewLine,
                app.Logs.Records.Where(r => r.Level >= LogLevel.Error).Select(r => r.Message)));

        var results = app.Chat.Calls
            .SelectMany(c => c.Messages)
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Select(r => r.Result?.ToString() ?? "");
        results.Should().Contain(r => r.Contains("log in", StringComparison.OrdinalIgnoreCase),
            "the model is told why, in the words it relays for any anonymous caller");
    }

    /// <summary>
    /// The regression the first fix introduced, caught by the review of the rc.12 notes before release:
    /// it stopped asking the provider on every A2A task. On a headless host (Novior.MentorAgent.Server) the
    /// provider reads <c>HttpContext.User</c>, so a peer that authenticated to a protected
    /// <c>/a2a</c> kept its identity in rc.11 — for RequiredRoles, memory and RateLimitPerUser. Only a
    /// provider that cannot be read may fall back to anonymous.
    /// </summary>
    [Fact]
    public async Task An_authenticated_a2a_caller_on_a_headless_host_keeps_its_roles()
    {
        await using var app = await ServerAppFactory.CreateAsync(
            configure: o =>
            {
                o.A2AServerEnabled = true;
                o.A2AServerUrl = "http://localhost/a2a";
                o.MemoryAutoCapture = false;
            },
            userId: "peer-agent",
            roles: ["Admin"],
            // Authentication must run before the endpoint, as in a host that protects /a2a; the
            // harness adds its own UseAuthentication only after configureApp.
            configureApp: a =>
            {
                a.UseAuthentication();
                a.UseEndpoints(e => e.MapMentorAgentA2A());
            });
        app.Chat.CallTool("export_ledger").Say("Fatto.");

        await AskOverA2AAsync(app, "esporta il registro ordini");

        var results = app.Chat.Calls
            .SelectMany(c => c.Messages)
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Select(r => r.Result?.ToString() ?? "")
            .ToList();
        results.Should().Contain(r => r.Contains("ledger"),
            "the caller authenticated and holds Admin, as rc.11 honoured. Results: {0}", string.Join(" | ", results));
    }

    [Fact]
    public async Task A_person_on_mentor_chat_is_read_from_the_request_not_from_a_provider_that_throws()
    {
        // Up to rc.14 this was the control for BUG-072: a turn a person started on /mentor/chat asked the
        // host's provider, which throws outside a circuit, and the warning had to appear. Since rc.15 the
        // endpoint records its caller and MentorAgent reads that first (BUG-131): the provider is not asked,
        // nothing failed, nothing to report. The warning's own control — a circuit whose provider throws —
        // is AnonymousIsolationTests.A_provider_that_throws_is_reported_once_and_the_session_stays_anonymous.
        await using var app = await HostAsync();
        app.Chat.Say("Ciao.");

        var res = await app.Client.PostAsJsonAsync("/mentor/chat", new { message = "ciao" });
        await res.Content.ReadAsStringAsync();

        app.Logs.HasWarning("AuthenticationStateProvider threw").Should().BeFalse(
            "the caller came with the request. Warnings:{0}{1}", Environment.NewLine, app.Logs.WarningText);
    }
}
