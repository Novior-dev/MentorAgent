using System.Text.Json;
using FluentAssertions;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Middleware;
using Novior.MentorAgent.Tests.Fakes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Novior.MentorAgent.Tests.Filtering;

/// <summary>
/// BUG-118's forced calls on a <see cref="ToolFilterChatClient"/> built by hand, for the two shapes the test
/// application cannot take — both found by the review of the first candidate: an application with no action of
/// its own, whose page requests were never classified, and an action whose input schema cannot be shown to the
/// classifier, which was offered as needing no input at all.
/// </summary>
public sealed class ForcedCallFilterTests
{
    private const string Page = "Portami ai report";
    private const string Cancel = "Annulla l'ordine 1001: è un duplicato";

    private static readonly AIFunction Navigate =
        AIFunctionFactory.Create((string url) => "ok", "navigate_to", "Navigates to a page within the application");

    /// <summary>An action with the schema a server published, whatever it is.</summary>
    private sealed class PublishedFunction(string name, string description, string schema) : AIFunction
    {
        private readonly JsonElement _schema = JsonDocument.Parse(schema).RootElement.Clone();
        public override string Name => name;
        public override string Description => description;
        public override JsonElement JsonSchema => _schema;
        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
            => new((object?)"ok");
    }

    private static (ToolFilterChatClient Client, FakeChatClient Inner, List<RequestShape> Asked) Build(
        RequestKind? answer, params AIFunction[] actions)
    {
        var inner = new FakeChatClient();
        inner.Say("ok");
        var asked = new List<RequestShape>();

        var embeddings = new FakeEmbeddingGenerator().OnAxis(Page, 3).OnAxis(Cancel, 0);
        foreach (var a in actions) embeddings.AtSimilarity($"{a.Name}: {a.Description}", 0, 0.70f);

        var client = new ToolFilterChatClient(
            inner,
            filterableNames: new HashSet<string>(actions.Select(a => a.Name), StringComparer.OrdinalIgnoreCase),
            embeddings: embeddings,
            minScore: 0.35f,
            maxTools: 12,
            logger: NullLogger.Instance,
            isConfirmationGated: _ => true,
            isForcibleAction: _ => true,
            navigationTool: "navigate_to",
            classify: (_, shape, _) =>
            {
                asked.Add(shape);
                return Task.FromResult(answer);
            });
        return (client, inner, asked);
    }

    private static Task Send(ToolFilterChatClient client, string message, params AIFunction[] tools)
        => client.GetResponseAsync([new ChatMessage(ChatRole.User, message)], new ChatOptions { Tools = [.. tools] });

    [Fact]
    public async Task An_application_with_no_action_of_its_own_still_navigates_a_page_request()
    {
        var (client, inner, asked) = Build(RequestKind.Navigate);

        await Send(client, Page, Navigate);

        var shape = asked.Should().ContainSingle().Subject;
        shape.Navigation.Should().BeTrue();
        shape.Lookup.Should().BeFalse();
        shape.Actions.Should().BeEmpty();
        inner.LastCall!.ToolMode.Should().Be(ChatToolMode.RequireAny);
        inner.LastCall.ToolNames.Should().Equal("navigate_to");
        inner.LastCall.AdditionalPropertyKeys.Should().Contain(ToolFilterChatClient.ForcedCallKey,
            "the marker UIActionsMiddleware takes off — it sits below this client");
    }

    [Fact]
    public async Task With_no_action_of_its_own_anything_else_runs_as_without_the_option()
    {
        var (client, inner, _) = Build(RequestKind.Other);

        await Send(client, Page, Navigate);

        inner.LastCall!.ToolMode.Should().NotBe(ChatToolMode.RequireAny);
        inner.LastCall.AdditionalPropertyKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task With_no_action_of_its_own_a_follow_up_of_the_turn_is_never_classified()
    {
        var (client, inner, asked) = Build(RequestKind.Navigate);

        await client.GetResponseAsync(
            [
                new ChatMessage(ChatRole.User, Page),
                new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "navigate_to")]),
                new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", "ok")]),
            ],
            new ChatOptions { Tools = [Navigate] });

        asked.Should().BeEmpty();
        inner.LastCall!.ToolMode.Should().NotBe(ChatToolMode.RequireAny);
    }

    [Fact]
    public async Task An_action_whose_schema_cannot_be_shown_is_never_offered_nor_required()
    {
        var published = new PublishedFunction("cancel_order", "Cancels an order",
            """{"$ref":"#/definitions/Cancel","definitions":{"Cancel":{"type":"object","properties":{"orderId":{"type":"integer"},"reason":{"type":"string"}},"required":["orderId","reason"]}}}""");
        var (client, inner, asked) = Build(RequestKind.Action, published);

        await Send(client, Cancel, published, Navigate);

        asked.Should().ContainSingle().Which.Actions.Should().BeEmpty(
            "shown as \"Inputs: none\" it would read as complete, and the forced call would invent orderId and reason");
        inner.LastCall!.ToolMode.Should().NotBe(ChatToolMode.RequireAny);
        inner.LastCall.ToolNames.Should().Contain("cancel_order", "it is still declared, as without the option");
    }

    [Fact]
    public async Task The_same_action_with_a_schema_that_can_be_shown_is_offered_and_required()
    {
        // The control: without it the test above would pass for an action that is never offered at all.
        var published = new PublishedFunction("cancel_order", "Cancels an order",
            """{"type":"object","properties":{"orderId":{"type":"integer"},"reason":{"type":"string"}},"required":["orderId","reason"]}""");
        var (client, inner, asked) = Build(RequestKind.Action, published);

        await Send(client, Cancel, published, Navigate);

        asked.Should().ContainSingle().Which.Actions.Should().Equal(
            "- cancel_order: Cancels an order. Inputs: orderId (integer), reason (string).");
        inner.LastCall!.ToolMode.Should().Be(ChatToolMode.RequireAny);
        inner.LastCall.ToolNames.Should().Equal("cancel_order");
    }
}
