using System.Net;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Blazor.Hub;
using Novior.MentorAgent.Blazor.Models;
using Novior.MentorAgent.Blazor.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Novior.MentorAgent.Tests.Client;

/// <summary>
/// The WebAssembly client's own HTTP calls — answering a confirmation, Stop — go where the hub is,
/// carrying the hub's credential.
///
/// <para>
/// Found by the rc.11 README audit. They went through the host's <see cref="HttpClient"/> with a
/// relative URL and no Authorization header. <c>/mentor/approve</c> and <c>/mentor/cancel</c> check
/// that the caller owns the hub connection, so for a signed-in user every confirmation and every Stop
/// was answered 401 — after the banner had already closed — and in the documented cross-origin layout
/// the host's client pointed at its own static origin unless the host knew to change it.
/// </para>
/// </summary>
public sealed class WasmServerRequestTests
{
    private sealed class Recorder : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static MentorAgentBlazorOptions CrossOrigin(string? token = "tok-123") => new()
    {
        HubUrl = "https://api.example.com/mentor-hub",
        AccessTokenProvider = token is null ? null : () => Task.FromResult<string?>(token),
    };

    private static HttpClient HostClient(Recorder recorder)
        => new(recorder) { BaseAddress = new Uri("https://client.example.com/") };   // the WASM template's default

    [Fact]
    public async Task A_confirmation_goes_to_the_hubs_origin_with_the_hubs_token()
    {
        var recorder = new Recorder();
        var options = CrossOrigin();
        var state = new WasmMentorStateService(new MentorHubClient(options, NullLogger<MentorHubClient>.Instance),
            new WasmMentorPageContext(), NullLogger<WasmMentorStateService>.Instance, HostClient(recorder), options);
        var id = Guid.NewGuid();

        await state.ConfirmAsync(id);

        var request = recorder.Requests.Should().ContainSingle().Subject;
        request.RequestUri!.ToString().Should().Be($"https://api.example.com/mentor/approve?actionId={id}&approved=true",
            "the host's client points at the static site; the endpoint is on the server");
        request.Headers.Authorization!.Scheme.Should().Be("Bearer");
        request.Headers.Authorization.Parameter.Should().Be("tok-123",
            "the endpoint checks that the caller owns the connection — anonymous is 401");
    }

    [Fact]
    public async Task An_answer_from_the_hosts_own_ui_goes_the_same_way()
    {
        var recorder = new Recorder();
        var options = CrossOrigin();
        var orchestrator = new WasmMentorOrchestrator(new MentorHubClient(options, NullLogger<MentorHubClient>.Instance),
            new WasmMentorPageContext(), NullLogger<WasmMentorOrchestrator>.Instance, HostClient(recorder), options);
        var req = new ConfirmationRequest { ActionId = Guid.NewGuid(), ToolName = "cancel_order", Message = "Cancel order 7" };

        await orchestrator.RespondToApprovalAsync(req, approved: false);

        var request = recorder.Requests.Should().ContainSingle().Subject;
        request.RequestUri!.GetLeftPart(UriPartial.Path).Should().Be("https://api.example.com/mentor/approve");
        request.Headers.Authorization!.Parameter.Should().Be("tok-123");
    }

    [Fact]
    public void Same_origin_hosting_keeps_the_relative_url_for_the_hosts_base_address()
    {
        // HubUrl "/mentor-hub" (the default): the server is the page's own origin, and the host's
        // BaseAddress resolves the path exactly as before. In the browser, as on Linux, "/mentor-hub"
        // parses as an absolute file:/// URI, which is why the resolver checks the scheme.
        var uri = MentorServerRequests.Resolve(new MentorAgentBlazorOptions(), "/mentor/approve?actionId=1&approved=true");

        uri.IsAbsoluteUri.Should().BeFalse();
        uri.OriginalString.Should().Be("/mentor/approve?actionId=1&approved=true");
    }

    [Fact]
    public async Task No_token_provider_means_no_authorization_header()
    {
        var recorder = new Recorder();
        var options = CrossOrigin(token: null);
        var state = new WasmMentorStateService(new MentorHubClient(options, NullLogger<MentorHubClient>.Instance),
            new WasmMentorPageContext(), NullLogger<WasmMentorStateService>.Instance, HostClient(recorder), options);

        await state.ConfirmAsync(Guid.NewGuid());

        recorder.Requests.Should().ContainSingle().Which.Headers.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task Session_methods_on_WebAssembly_point_to_the_http_endpoint()
    {
        // BUG-110: the exception said there was no endpoint that hands a snapshot to a client; there is.
        var options = CrossOrigin();
        var orchestrator = new WasmMentorOrchestrator(new MentorHubClient(options, NullLogger<MentorHubClient>.Instance),
            new WasmMentorPageContext(), NullLogger<WasmMentorOrchestrator>.Instance, new HttpClient(), options);

        var save = () => orchestrator.SerializeSessionAsync();
        var restore = () => orchestrator.RestoreSessionAsync(System.Text.Json.JsonDocument.Parse("{}").RootElement);

        (await save.Should().ThrowAsync<NotSupportedException>()).Which.Message.Should().Contain("/mentor/session");
        (await restore.Should().ThrowAsync<NotSupportedException>()).Which.Message.Should().Contain("/mentor/session");
    }
}
