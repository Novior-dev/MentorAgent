using FluentAssertions;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Novior.MentorAgent.Tests.Federation;

/// <summary>
/// BUG-103 over the real <c>/mcp</c> endpoint: <c>tools/list</c> and <c>tools/call</c> as two signed-in
/// callers whose roles are in a <c>roles</c> claim — the shape of the live repro on the published rc.13.
///
/// <para>
/// <c>tools/call</c> looks the name up in the caller's list, so the list IS the authorization: a
/// caller served somebody else's list could run somebody else's actions.
/// </para>
/// </summary>
public sealed class McpCallerIdentityHttpTests
{
    private static Task<ServerAppFactory> CreateAsync() => ServerAppFactory.CreateAsync(
        userId: "ada",
        roles: ["Admin"],
        roleClaimType: "roles",
        configure: o => o.McpServerEnabled = true,
        configureServices: s =>
        {
            // The README's recipe for an ASP.NET Core host.
            s.AddHttpContextAccessor();
            s.AddOptions<MentorOptions>().Configure<IHttpContextAccessor>(
                (o, http) => o.McpCallerPrincipal = () => http.HttpContext?.User);
        },
        mapEndpoints: e => e.MapMentorAgentMcp());

    [Fact]
    public async Task A_roleless_caller_cannot_call_an_admin_action_that_an_admin_listed_first()
    {
        await using var app = await CreateAsync();

        var ada = await McpTestSession.OpenAsync(app.Client);
        (await ada.ListToolNamesAsync()).Should().Contain("export_ledger");
        (await ada.CallAsync("export_ledger")).Text.Should().Contain("ledger");

        // eve is signed in (the header authenticates her) and holds no role.
        var eve = await McpTestSession.OpenAsync(app.Client, asUser: "eve");

        (await eve.ListToolNamesAsync()).Should().NotContain("export_ledger",
            "the list is decided for the caller who asks, not the one who asked first");
        var (text, isError) = await eve.CallAsync("export_ledger");
        isError.Should().BeTrue();
        text.Should().Contain("Unknown tool: 'export_ledger'",
            "the call is re-checked on the same decision, and answered like any name eve may not use");
    }

    [Fact]
    public async Task An_admin_listing_after_a_roleless_caller_still_gets_the_action()
    {
        await using var app = await CreateAsync();

        var eve = await McpTestSession.OpenAsync(app.Client, asUser: "eve");
        (await eve.ListToolNamesAsync()).Should().NotContain("export_ledger");

        var ada = await McpTestSession.OpenAsync(app.Client);
        (await ada.ListToolNamesAsync()).Should().Contain("export_ledger");
        (await ada.CallAsync("export_ledger")).IsError.Should().BeFalse();
    }
}
