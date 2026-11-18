using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Attributes;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Server.Extensions;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Xunit;

namespace Novior.MentorAgent.Tests.Server;

/// <summary>A controller of the tests: its action is declared with <c>[MentorAction]</c> and read from its endpoint.</summary>
[ApiController]
[Route("api/stock")]
public sealed class StockEndpointController(EndpointActionTests.Ledger ledger) : ControllerBase
{
    [HttpGet("{sku}")]
    [MentorAction(Description = "Reads the stock level of a product")]
    public IActionResult GetStockLevel(string sku)
    {
        ledger.Calls.Enqueue("stock:" + sku);
        return Ok(new { sku, level = 12 });
    }
}

/// <summary>Story 1307: a controller whose <c>[Authorize(Roles = ...)]</c> gives its action's roles.</summary>
[ApiController]
[Route("api/approvals")]
[Authorize(Roles = "Manager,Admin")]
public sealed class ApprovalsEndpointController(EndpointActionTests.Ledger ledger) : ControllerBase
{
    [HttpPost("{id:int}")]
    [MentorAction(Description = "Approves a request of the approvals endpoint")]
    public IActionResult ApproveRequestEndpoint(int id)
    {
        ledger.Calls.Enqueue("approve:" + id);
        return Ok(new { id, approved = true });
    }
}

/// <summary>A controller by its base class only, without <c>[ApiController]</c>.</summary>
public sealed class PlainEndpointController : ControllerBase;

/// <summary>A class MVC must not take as a controller.</summary>
[NonController]
public sealed class NotAnEndpointController : ControllerBase;

/// <summary>
/// Story 1287, slices A and B — an endpoint of the application as an action of the coordinator: declared with
/// <c>WithMentorAction</c> or <c>[MentorAction]</c> on a controller, offered to the model with the parameters it binds,
/// and run as the endpoint itself, as the user of the turn, with its authorization first and then its filters.
/// </summary>
public sealed class EndpointActionTests
{
    /// <summary>What the endpoints of the tests did.</summary>
    public sealed class Ledger
    {
        public ConcurrentQueue<string> Calls { get; } = new();
    }

    public sealed record CancelRequest(string Reason);

    public sealed record Paging(int Page, int Size);

    private static void Map(IEndpointRouteBuilder e)
    {
        e.MapGet("/orders/{id:int}", (int id, Ledger ledger) =>
            {
                ledger.Calls.Enqueue($"get:{id}");
                return Results.Ok(new { id, status = "open" });
            })
            .WithName("GetOrderEndpoint")
            .WithMentorAction(a => a.Describe("Reads an order from the order endpoint"));

        e.MapPost("/orders/{id:int}/close", (int id, CancelRequest body, string? note, Ledger ledger) =>
            {
                ledger.Calls.Enqueue($"close:{id}:{body.Reason}:{note}");
                return Results.Ok(new { id, closed = true });
            })
            .WithMentorAction("close_order_endpoint", a => a.Describe("Closes an order").RequireConfirmation(false));

        e.MapPost("/orders/{id:int}/archive", (int id, Ledger ledger) =>
            {
                ledger.Calls.Enqueue($"archive:{id}");
                return Results.NoContent();
            })
            .WithMentorAction("archive_order_endpoint", a => a.Describe("Archives an order"));

        e.MapPost("/admin/purge", (Ledger ledger) =>
            {
                ledger.Calls.Enqueue("purge");
                return Results.Ok("purged");
            })
            .RequireAuthorization(p => p.RequireRole("Admin"))
            .WithMentorAction("purge_endpoint", a => a.Describe("Purges the archive").RequireConfirmation(false));

        e.MapPost("/archive/seal", (Ledger ledger) =>
            {
                ledger.Calls.Enqueue("seal");
                return Results.Ok("sealed");
            })
            .RequireAuthorization(p => p.RequireClaim("department", "archive"))
            .WithMentorAction("seal_endpoint", a => a.Describe("Seals the archive").RequireConfirmation(false));

        e.MapGet("/missing/{id:int}", (int id) => Results.NotFound(new { id, error = "no such order" }))
            .WithMentorAction("find_missing_endpoint", a => a.Describe("Looks for an order that is not there"));

        e.MapGet("/filtered", (Ledger ledger) =>
            {
                ledger.Calls.Enqueue("filtered");
                return Results.Ok("handler");
            })
            .AddEndpointFilter(async (context, next) =>
            {
                context.HttpContext.RequestServices.GetRequiredService<Ledger>().Calls.Enqueue("filter");
                return await next(context);
            })
            .WithMentorAction("filtered_endpoint", a => a.Describe("Goes through an endpoint filter"));

        e.MapControllers();
    }

    private static Task<ServerAppFactory> App(string? userId = "u1", string[]? roles = null,
        Action<IServiceCollection>? services = null, Action<IEndpointRouteBuilder>? map = null,
        Action<MentorOptions>? configure = null)
        => ServerAppFactory.CreateAsync(
            configure: configure,
            userId: userId,
            roles: roles,
            configureServices: s =>
            {
                s.AddSingleton<Ledger>();
                s.AddControllers().AddApplicationPart(typeof(EndpointActionTests).Assembly);
                services?.Invoke(s);
            },
            mapEndpoints: map ?? Map);

    private static async Task TurnAsync(ServerAppFactory app, string text = "please")
    {
        await using var hub = app.CreateHubConnection();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On("StreamingCompleted", () => done.TrySetResult());
        hub.On<string>("Error", _ => done.TrySetResult());
        await hub.StartAsync();
        await hub.InvokeAsync("SendMessage", text, null);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await app.Services.GetRequiredService<MentorActionDecisions>().FlushAsync();
    }

    /// <summary>What the action gave the model, as text.</summary>
    private static string ResultOf(ServerAppFactory app, string tool)
    {
        var callId = app.Chat.Calls.SelectMany(c => c.Messages).SelectMany(m => m.Contents).OfType<FunctionCallContent>()
            .Last(c => c.Name == tool).CallId;
        var result = app.Chat.Calls.SelectMany(c => c.Messages).SelectMany(m => m.Contents).OfType<FunctionResultContent>()
            .Last(r => r.CallId == callId).Result;
        return result is JsonElement json ? json.GetRawText() : result?.ToString() ?? "";
    }

    private static MentorActionInfo Info(ServerAppFactory app, string tool)
        => app.Services.GetRequiredService<IMentorActionCatalog>().GetActions().Should().ContainSingle(a => a.ToolName == tool).Which;

    [Fact]
    [Trait("Rule", "endpoint.declared")]
    public async Task A_declared_endpoint_is_an_action_of_the_coordinator_in_the_catalogue()
    {
        await using var app = await App();

        var read = Info(app, "get_order_endpoint");
        read.Origin.Should().Be(MentorActionOrigins.Endpoint);
        read.AgentKind.Should().Be(MentorAgentKind.Coordinator);
        read.Description.Should().Be("Reads an order from the order endpoint");

        Info(app, "get_stock_level").Origin.Should().Be(MentorActionOrigins.Endpoint, "a controller's [MentorAction] is read from its endpoint");

        // The model reads an endpoint's route, query and body, not its handler's parameters: no method is given.
        read.Method.Should().BeNull();
        Info(app, "get_stock_level").Method.Should().BeNull("a controller's action is an endpoint too");
    }

    [Fact]
    [Trait("Rule", "endpoint.trace")]
    public async Task Each_endpoint_is_a_registration_of_its_own_named_by_its_verb_and_route()
    {
        await using var app = await App();
        var catalog = app.Services.GetRequiredService<IMentorActionCatalog>();
        var endpoints = catalog.GetRegistrations().Where(r => r.Origin == MentorActionOrigins.Endpoint).ToList();
        var actions = catalog.GetActions().Where(a => a.Origin == MentorActionOrigins.Endpoint).ToList();

        var close = endpoints.Should().ContainSingle(r => r.Id == "endpoint:POST /orders/{id:int}/close").Which;
        close.Source.Should().Be("POST /orders/{id:int}/close");
        close.Actions.Should().Equal("close_order_endpoint");
        endpoints.Should().ContainSingle(r => r.Id == "endpoint:GET /api/stock/{sku}", "a controller's route is its template")
            .Which.Actions.Should().Equal("get_stock_level");

        endpoints.Should().OnlyContain(r => r.Actions.Count == 1 && r.Id == "endpoint:" + r.Source && r.Page == null && r.Revision == 0 && r.Enabled);
        endpoints.Should().HaveCount(actions.Count, "the lambdas of one class are not grouped by the compiler's class");
        foreach (var action in actions)
            endpoints.Should().ContainSingle(r => r.Id == action.RegistrationId, action.ToolName).Which.Actions.Should().Equal(action.ToolName);
    }

    [Fact]
    [Trait("Rule", "endpoint.trace")]
    public async Task Two_endpoints_with_one_handler_are_two_registrations()
    {
        await using var app = await App(map: e =>
        {
            e.MapPost("/shelves/{id:int}/tidy", Tidy).WithMentorAction("tidy_shelf", a => a.Describe("Tidies a shelf"));
            e.MapPost("/aisles/{id:int}/tidy", Tidy).WithMentorAction("tidy_aisle", a => a.Describe("Tidies an aisle"));
        });
        var catalog = app.Services.GetRequiredService<IMentorActionCatalog>();

        catalog.GetRegistrations().Where(r => r.Origin == MentorActionOrigins.Endpoint)
            .Select(r => r.Id + " = " + string.Join(", ", r.Actions))
            .Should().BeEquivalentTo("endpoint:POST /shelves/{id:int}/tidy = tidy_shelf", "endpoint:POST /aisles/{id:int}/tidy = tidy_aisle");
        catalog.GetActions().Select(a => a.RegistrationId)
            .Should().Contain(["endpoint:POST /shelves/{id:int}/tidy", "endpoint:POST /aisles/{id:int}/tidy"]);
    }

    private static IResult Tidy(int id) => Results.Ok(id);

    private static IReadOnlyList<string> RolesOf(ServerAppFactory app, string tool) => Info(app, tool).RequiredRoles;

    [Fact]
    [Trait("Rule", "endpoint.roles-from-authorization")]
    public async Task An_endpoint_with_a_role_rule_gives_its_action_the_same_roles()
    {
        await using var app = await App();

        RolesOf(app, "purge_endpoint").Should().Equal("Admin");
        RolesOf(app, "approve_request_endpoint").Should().Equal(["Manager", "Admin"], "[Authorize(Roles)] of the controller");
        RolesOf(app, "get_order_endpoint").Should().BeEmpty("no authorization, no roles");
        RolesOf(app, "seal_endpoint").Should().BeEmpty("a claim is not a role");
    }

    [Fact]
    [Trait("Rule", "endpoint.roles-from-authorization")]
    public async Task A_named_policy_and_the_fallback_policy_give_their_roles_and_AllowAnonymous_none()
    {
        await using var app = await App(
            services: s => s.AddAuthorization(o =>
            {
                o.AddPolicy("Managers", p => p.RequireRole("Manager"));
                o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireRole("Staff").Build();
            }),
            map: e =>
            {
                e.MapPost("/named", () => "n").RequireAuthorization("Managers").WithMentorAction("named_endpoint", a => a.Describe("N"));
                e.MapPost("/fallback", () => "f").WithMentorAction("fallback_endpoint", a => a.Describe("F"));
                e.MapPost("/open", () => "o").AllowAnonymous().WithMentorAction("open_endpoint", a => a.Describe("O"));
            });

        RolesOf(app, "named_endpoint").Should().Equal("Manager");
        RolesOf(app, "fallback_endpoint").Should().Equal(["Staff"], "the fallback policy decides an endpoint that says nothing");
        RolesOf(app, "open_endpoint").Should().BeEmpty();
    }

    [Fact]
    [Trait("Rule", "endpoint.roles-from-authorization")]
    public async Task Roles_asked_together_give_no_roles_and_the_log_says_the_endpoint_still_refuses()
    {
        await using var app = await App(map: e =>
            e.MapPost("/both", () => "b")
                .RequireAuthorization(p => p.RequireRole("Manager", "Admin"))
                .RequireAuthorization(p => p.RequireRole("Auditor"))
                .WithMentorAction("both_endpoint", a => a.Describe("B")));

        RolesOf(app, "both_endpoint").Should().BeEmpty("roles asked together cannot be said as any one of them");
        app.Logs.Records.Should().ContainSingle(r => r.Level == LogLevel.Information && r.Message.Contains("asks for roles together"))
            .Which.Message.Should().Contain("/both").And.Contain("Manager or Admin and Auditor").And.Contain("403");
    }

    [Fact]
    [Trait("Rule", "endpoint.roles-from-authorization")]
    public async Task The_same_role_rule_twice_is_one_set_of_roles()
    {
        await using var app = await App(map: e =>
            e.MapPost("/twice", () => "t")
                .RequireAuthorization(p => p.RequireRole("Admin"))
                .RequireAuthorization(p => p.RequireRole("Admin"))
                .WithMentorAction("twice_endpoint", a => a.Describe("T")));

        RolesOf(app, "twice_endpoint").Should().Equal("Admin");
    }

    [Fact]
    [Trait("Rule", "endpoint.roles-from-authorization")]
    public async Task Roles_declared_on_the_action_replace_the_ones_of_the_rule()
    {
        await using var app = await App(map: e =>
            e.MapPost("/declared", () => "d")
                .RequireAuthorization(p => p.RequireRole("Admin"))
                .WithMentorAction("declared_roles_endpoint", a => a.Describe("D").RequireRoles("Auditor")));

        RolesOf(app, "declared_roles_endpoint").Should().Equal("Auditor");
    }

    [Fact]
    [Trait("Rule", "endpoint.roles-from-authorization")]
    public async Task A_user_without_the_role_is_not_offered_the_action_and_is_refused_before_the_endpoint()
    {
        var observer = new RecordingActionObserver();
        await using var app = await App(roles: ["Clerk"], services: s => s.AddMentorActionObserver(observer));
        app.Chat.CallTool("purge_endpoint").Say("I could not.");

        await TurnAsync(app);

        app.Chat.Calls[0].Tool("purge_endpoint").Should().BeNull("the model is offered only what the user's roles allow");
        observer.Steps("purge_endpoint").Should().Equal(["Denied/role-missing"], "the gate refuses it, before any confirmation");
        app.Services.GetRequiredService<Ledger>().Calls.Should().BeEmpty();
    }

    [Fact]
    [Trait("Rule", "endpoint.runs-the-pipeline")]
    public async Task The_model_reads_the_route_and_query_and_body_and_the_action_runs_the_endpoint()
    {
        var observer = new RecordingActionObserver();
        await using var app = await App(services: s => s.AddMentorActionObserver(observer));
        app.Chat.CallTool("close_order_endpoint", new { id = 1002, body = new { reason = "duplicate" }, note = "by phone" }).Say("Closed.");

        await TurnAsync(app);

        var tool = app.Chat.Calls[0].Tool("close_order_endpoint")!;
        var schema = tool.JsonSchema.GetRawText();
        schema.Should().Contain("\"id\"").And.Contain("\"body\"").And.Contain("\"note\"").And.NotContain("ledger", "a service is not the model's");

        ResultOf(app, "close_order_endpoint").Should().Contain("\"closed\":true");
        app.Services.GetRequiredService<Ledger>().Calls.Should().Equal(["close:1002:duplicate:by phone"]);
        observer.Steps("close_order_endpoint").Should().Equal(["Allowed/allowed", "Executed/completed"]);
    }

    [Fact]
    [Trait("Rule", "endpoint.runs-the-pipeline")]
    public async Task A_controller_action_runs_through_mvc()
    {
        await using var app = await App();
        app.Chat.CallTool("get_stock_level", new { sku = "A-1" }).Say("Twelve.");

        await TurnAsync(app);

        app.Services.GetRequiredService<Ledger>().Calls.Should().Equal(["stock:A-1"]);
        ResultOf(app, "get_stock_level").Should().Contain("\"level\":12");
    }

    [Fact]
    [Trait("Rule", "endpoint.runs-the-pipeline")]
    public async Task The_endpoint_filters_run()
    {
        await using var app = await App();
        app.Chat.CallTool("filtered_endpoint").Say("Done.");

        await TurnAsync(app);

        app.Services.GetRequiredService<Ledger>().Calls.Should().Equal(["filter", "filtered"]);
    }

    [Fact]
    [Trait("Rule", "endpoint.authorization-first")]
    public async Task An_endpoint_the_user_may_not_call_does_not_run_and_the_model_reads_why()
    {
        var observer = new RecordingActionObserver();
        await using var app = await App(roles: ["Clerk"], services: s => s.AddMentorActionObserver(observer));
        app.Chat.CallTool("seal_endpoint").Say("I could not.");

        await TurnAsync(app);

        app.Services.GetRequiredService<Ledger>().Calls.Should().BeEmpty("the authorization comes before the endpoint");
        ResultOf(app, "seal_endpoint").Should().Contain("not allowed").And.Contain("403");
        observer.Steps("seal_endpoint").Should().Equal(["Allowed/allowed", "Failed/exception"],
            "a claim is not a role: the gate lets it through, the endpoint refuses");
    }

    [Fact]
    [Trait("Rule", "endpoint.authorization-first")]
    public async Task An_endpoint_the_user_may_call_runs()
    {
        await using var app = await App(roles: ["Admin"]);
        app.Chat.CallTool("purge_endpoint").Say("Purged.");

        await TurnAsync(app);

        app.Services.GetRequiredService<Ledger>().Calls.Should().Equal(["purge"]);
    }

    [Fact]
    [Trait("Rule", "endpoint.authorization-first")]
    public async Task An_anonymous_user_is_refused_as_not_signed_in()
    {
        await using var app = await App(userId: null);
        app.Chat.CallTool("seal_endpoint").Say("I could not.");

        await TurnAsync(app);

        app.Services.GetRequiredService<Ledger>().Calls.Should().BeEmpty();
        ResultOf(app, "seal_endpoint").Should().Contain("not signed in").And.Contain("401");
    }

    [Fact]
    [Trait("Rule", "endpoint.runs-the-pipeline")]
    public async Task An_answer_of_400_or_more_is_a_failed_action_with_the_status_and_the_body()
    {
        var observer = new RecordingActionObserver();
        await using var app = await App(services: s => s.AddMentorActionObserver(observer));
        app.Chat.CallTool("find_missing_endpoint", new { id = 9 }).Say("Not there.");

        await TurnAsync(app);

        ResultOf(app, "find_missing_endpoint").Should().Contain("404").And.Contain("no such order");
        observer.Steps("find_missing_endpoint").Should().Equal(["Allowed/allowed", "Failed/exception"]);
    }

    [Fact]
    [Trait("Rule", "endpoint.verb-default")]
    public async Task A_GET_reads_and_another_verb_asks_for_a_confirmation_unless_it_says_otherwise()
    {
        await using var app = await App();

        Info(app, "get_order_endpoint").Should().Match<MentorActionInfo>(a => a.ReadOnly && !a.RequiresConfirmation);
        Info(app, "archive_order_endpoint").Should().Match<MentorActionInfo>(a => !a.ReadOnly && a.RequiresConfirmation,
            "a POST writes, and RequireConfirmationForWrites is on by default");
        Info(app, "close_order_endpoint").RequiresConfirmation.Should().BeFalse("what is declared wins");
    }

    [Fact]
    [Trait("Rule", "endpoint.not-published")]
    public async Task An_endpoint_action_is_not_published_on_mcp_nor_on_the_card()
    {
        // The controllers are services here, so the MCP server could build the controller's action as a plain method,
        // past its authorization: the one way an endpoint's action could reach /mcp.
        await using var app = await App(configure: o => o.McpServerEnabled = true,
            services: s => s.AddControllers().AddControllersAsServices(),
            map: e =>
            {
                Map(e);
                e.MapMentorAgentMcp();
            });

        foreach (var name in new[] { "get_order_endpoint", "get_stock_level" })
        {
            var read = Info(app, name);
            read.McpExposure.Should().Be(MentorMcpExposure.NotServed, "a GET that asks nothing would be published, were it not an endpoint's");
            read.OnAgentCard.Should().BeFalse();
        }

        app.Services.GetRequiredService<MentorAgentMcpToolCollection>().GetTools()
            .Should().NotContain(t => t.ProtocolTool.Name == "get_order_endpoint" || t.ProtocolTool.Name == "get_stock_level");
        app.Services.GetRequiredService<IMentorActionCatalog>().GetActions()
            .Should().Contain(a => a.McpExposure == MentorMcpExposure.Published, "the server is mapped");
    }

    [Fact]
    [Trait("Rule", "endpoint.authorization-first")]
    public async Task A_policy_that_stops_the_action_keeps_the_endpoint_from_running()
    {
        var policy = new TestActionPolicy
        {
            Verdict = r => r.ToolName == "close_order_endpoint" ? MentorActionVerdict.Stop("not now") : MentorActionVerdict.Proceed,
        };
        await using var app = await App(services: s => s.AddMentorActionPolicy(policy));
        app.Chat.CallTool("close_order_endpoint", new { id = 1, body = new { reason = "x" } }).Say("Stopped.");

        await TurnAsync(app);

        app.Services.GetRequiredService<Ledger>().Calls.Should().BeEmpty();
    }

    public static TheoryData<string, string> BadDeclarations => new()
    {
        { "lambda", "its handler is a lambda" },
        { "two-verbs", "an action needs an endpoint with one verb" },
        { "header", "comes from a header, a form, a file or [AsParameters]" },
        { "as-parameters", "its parameter 'paging' comes from a header, a form, a file or [AsParameters]" },
        { "every-verb", "it answers every verb" },
        { "no-description", "it has no description" },
        { "bad-name", "is not a tool name" },
        { "taken-name", "has the name of another action" },
        { "same-name", "Two endpoint actions are named" },
        { "missing-policy", "its authorization names the policy 'Managerz', which is not registered" },
    };

    [Theory]
    [Trait("Rule", "endpoint.declared")]
    [MemberData(nameof(BadDeclarations))]
    public async Task A_declaration_the_action_cannot_honour_stops_the_application(string which, string message)
    {
        void MapBad(IEndpointRouteBuilder e)
        {
            switch (which)
            {
                case "lambda": e.MapGet("/x", () => "x").WithMentorAction(a => a.Describe("X")); break;
                case "two-verbs": e.MapMethods("/x", ["GET", "POST"], () => "x").WithMentorAction("x_two", a => a.Describe("X")); break;
                case "header": e.MapGet("/x", ([FromHeader] string token) => token).WithMentorAction("x_header", a => a.Describe("X")); break;
                case "as-parameters": e.MapGet("/x", ([AsParameters] Paging paging) => paging.Page).WithMentorAction("x_paging", a => a.Describe("X")); break;
                case "every-verb": e.Map("/x", () => "x").WithMentorAction("x_every", a => a.Describe("X")); break;
                case "no-description": e.MapGet("/x", () => "x").WithMentorAction("x_nodesc", _ => { }); break;
                case "bad-name": e.MapGet("/x", () => "x").WithMentorAction("x y", a => a.Describe("X")); break;
                case "taken-name": e.MapGet("/x", () => "x").WithMentorAction("cancel_order", a => a.Describe("X")); break;
                case "missing-policy": e.MapGet("/x", () => "x").RequireAuthorization("Managerz").WithMentorAction("x_policy", a => a.Describe("X")); break;
                case "same-name":
                    e.MapGet("/x", () => "x").WithMentorAction("x_same", a => a.Describe("X"));
                    e.MapGet("/y", () => "y").WithMentorAction("x_same", a => a.Describe("Y"));
                    break;
            }
        }

        await using var app = await App(map: MapBad);

        app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested
            .Should().BeTrue("the application stops when it starts");
        app.Logs.Records.Should().Contain(r => r.Level == LogLevel.Critical && r.Message.Contains(message));
    }

    [Theory]
    [Trait("Rule", "endpoint.authorization-first")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task In_a_host_without_authorization_an_endpoint_that_asks_for_it_is_refused(bool asksForAuthorization)
    {
        var ran = false;
        var metadata = new List<object> { new HttpMethodMetadata(["POST"]) };
        if (asksForAuthorization) metadata.Add(new AuthorizeAttribute());
        var endpoint = new RouteEndpoint(
            _ => { ran = true; return Task.CompletedTask; },
            Microsoft.AspNetCore.Routing.Patterns.RoutePatternFactory.Parse("/x"), 0,
            new EndpointMetadataCollection(metadata), "POST /x");
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var function = new Novior.MentorAgent.Server.Endpoints.MentorEndpointActionFunction(
            AIFunctionFactory.Create(() => "x", "x_endpoint"),
            new Novior.MentorAgent.Server.Endpoints.MentorEndpointBinding(endpoint, "POST", []),
            services, NullLogger<Novior.MentorAgent.Server.Endpoints.MentorEndpointActionFunction>.Instance);

        var call = async () => await function.InvokeAsync(new AIFunctionArguments());

        if (asksForAuthorization)
        {
            (await call.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden,
                "nothing runs past a rule nobody can check");
            ran.Should().BeFalse();
        }
        else
        {
            await call.Should().NotThrowAsync();
            ran.Should().BeTrue();
        }
    }

    [Fact]
    [Trait("Rule", "scan.controllers-are-endpoints")]
    public void A_controller_is_not_read_by_the_scan()
    {
        MentorDiscoveryService.IsController(typeof(StockEndpointController)).Should().BeTrue();
        MentorDiscoveryService.IsController(typeof(PlainEndpointController)).Should().BeTrue("it derives from ControllerBase");
        MentorDiscoveryService.IsController(typeof(NotAnEndpointController)).Should().BeFalse("[NonController]");
        MentorDiscoveryService.IsController(typeof(Ledger)).Should().BeFalse();

        var options = new MentorOptions { AppName = "Scan", ScanAssemblies = [typeof(StockEndpointController).Assembly] };
        var found = new MentorDiscoveryService(Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<MentorDiscoveryService>.Instance).Discover();

        found.Level1Methods.Should().NotContain(m => m.DeclaringType == typeof(StockEndpointController),
            "a controller's [MentorAction] is read from its endpoint, where its authorization and filters run");
    }
}
