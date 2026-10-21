using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Novior.MentorAgent.Tests.Docs;
using Novior.MentorAgent.Tests.Harness;
using Xunit;

namespace Novior.MentorAgent.Tests;

/// <summary>
/// Pins down the parts of the Agent Framework's declarative agent YAML (#23) that the June 2026
/// guide does not document.
///
/// <para>
/// The guide's only example shows <c>kind</c>, <c>name</c>, <c>description</c>, <c>instructions</c>,
/// <c>model.options</c> and <c>outputSchema</c> — and no <c>tools</c> section at all. Both facts
/// below were established by running the reader, not by reading about it, because the failure modes
/// are unhelpful: a wrong tool shape throws <c>NotSupportedException</c> from deep inside the
/// factory, and an unsupported scalar style reports a parse error at <i>end of file</i> rather than
/// at the line that caused it.
/// </para>
///
/// <para>
/// These tests exist so that an Agent Framework upgrade that changes either contract is caught here,
/// with an explanation, instead of in someone's application as a YAML file that stopped loading.
/// </para>
/// </summary>
public sealed class DeclarativeYamlTests
{
    private sealed class StubChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        { yield return new ChatResponseUpdate(ChatRole.Assistant, "ok"); await Task.CompletedTask; }

        public object? GetService(Type t, object? key = null) => null;
        public void Dispose() { }
    }

    private static ChatClientPromptAgentFactory Factory() => new(
        new StubChatClient(),
        [AIFunctionFactory.Create(() => "[]", name: "get_all_orders", description: "Returns all orders")],
        loggerFactory: NullLoggerFactory.Instance);

    private const string Head = """
        kind: Prompt
        name: ShippingAgent
        description: Answers questions about deliveries
        instructions: You handle shipping questions only.
        """;

    [Fact]
    public async Task MinimalDefinition_Loads()
    {
        var agent = await Factory().CreateFromYamlAsync(Head);

        agent.Should().NotBeNull();
        agent!.Name.Should().Be("ShippingAgent");
    }

    /// <summary>
    /// The tool shape. <c>kind</c> is required on every entry, and <c>function</c> is the one that
    /// binds to a tool the host supplied.
    /// </summary>
    [Fact]
    public async Task FunctionTool_RequiresKindFunction()
    {
        var yaml = Head + "\ntools:\n  - kind: function\n    name: get_all_orders\n";

        var agent = await Factory().CreateFromYamlAsync(yaml);

        agent.Should().NotBeNull();
    }

    [Theory]
    [InlineData("bare string",  "tools:\n  - get_all_orders\n")]
    [InlineData("name only",    "tools:\n  - name: get_all_orders\n")]
    [InlineData("id only",      "tools:\n  - id: get_all_orders\n")]
    [InlineData("wrong kind",   "tools:\n  - kind: InvokeClientTask\n    name: get_all_orders\n")]
    public async Task ToolWithoutKindFunction_IsRejected(string label, string toolsBlock)
    {
        var act = async () => await Factory().CreateFromYamlAsync(Head + "\n" + toolsBlock);

        // The message is the only place the accepted set is written down, so assert on it: it is
        // what a developer will search for after seeing the exception.
        (await act.Should().ThrowAsync<NotSupportedException>($"'{label}' is not a tool definition"))
            .Which.Message.Should().Contain("codeInterpreter,fileSearch,function,webSearch,mcp");
    }

    /// <summary>
    /// A name that matches no supplied tool is accepted silently — the agent loads without it.
    /// Convenient (a definition survives a tool being renamed) and a trap (a typo is invisible), so
    /// it is documented rather than "fixed".
    /// </summary>
    [Fact]
    public async Task UnknownToolName_LoadsTheAgentAnyway()
    {
        var yaml = Head + "\ntools:\n  - kind: function\n    name: does_not_exist\n";

        var agent = await Factory().CreateFromYamlAsync(yaml);

        agent.Should().NotBeNull();
    }

    /// <summary>
    /// Literal blocks work; folded blocks do not. Worth pinning because <c>&gt;</c> is the natural
    /// choice for a long one-line description, and its failure names the wrong line.
    /// </summary>
    [Fact]
    public async Task LiteralBlockScalar_IsSupported()
    {
        var yaml = "kind: Prompt\nname: A\ndescription: d\ninstructions: |\n  line one\n  line two\n";

        (await Factory().CreateFromYamlAsync(yaml)).Should().NotBeNull();
    }

    [Fact]
    public async Task FoldedBlockScalar_IsNotSupported()
    {
        var yaml = "kind: Prompt\nname: A\ndescription: >\n  one line\n  and another\ninstructions: x\n";

        var act = async () => await Factory().CreateFromYamlAsync(yaml);

        await act.Should().ThrowAsync<Exception>(
            "the reader rejects '>' — use '|' or a plain scalar instead");
    }

    [Fact]
    public async Task CommentInsideAList_IsFine()
    {
        var yaml = Head + "\ntools:\n  # pick from the tools the app already registers\n  - kind: function\n    name: get_all_orders\n";

        (await Factory().CreateFromYamlAsync(yaml)).Should().NotBeNull();
    }

    [Fact]
    public async Task ModelOptions_AreAccepted()
    {
        var yaml = Head + "\nmodel:\n  options:\n    temperature: 0.2\n    topP: 0.95\n";

        (await Factory().CreateFromYamlAsync(yaml)).Should().NotBeNull();
    }

    /// <summary>
    /// The shipped sample must actually load — it is the copy-paste source for users. Without the sample repository
    /// the test is reported as skipped; it passed without having run before (BUG-189).
    /// </summary>
    [SamplesFact]
    public async Task TheSampleDefinition_Loads()
    {
        RepoFiles.SamplesRoot.Should().NotBeNull("a release run loads the sample definition, and {0}", RepoFiles.SamplesMissing);

        var path = Path.Combine(RepoFiles.SamplesRoot!, "MentorAgentServer", "Agents", "shipping.agent.yaml");
        File.Exists(path).Should().BeTrue("the sample repository is there, so its definition must be: {0}", path);

        var agent = await Factory().CreateFromYamlAsync(await File.ReadAllTextAsync(path));

        agent.Should().NotBeNull();
        agent!.Name.Should().Be("ShippingAgent");
    }
}
