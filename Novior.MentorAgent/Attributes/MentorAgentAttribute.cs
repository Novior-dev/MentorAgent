namespace Novior.MentorAgent.Attributes;

/// <summary>
/// Level 2 — Decorates a class to turn it into a specialized AI agent with a Handoff Workflow.
///
/// The class must implement <see cref="IMentorAgent"/> and be registered in the DI container.
/// MentorAgent automatically builds a <c>ChatClientAgent</c> with the class methods as tools,
/// and connects it to the Handoff graph via <c>HandoffTo</c>.
///
/// <code>
/// [MentorAgent(
///     Name = "OrderAgent",
///     Description = "Handles everything related to orders: creation, tracking, cancellation",
///     HandoffTo = ["CustomerAgent", "InvoiceAgent"]
/// )]
/// public class OrderAgent : IMentorAgent
/// {
///     [Description("Creates a new order")]
///     public async Task&lt;Order&gt; CreateOrder(int customerId, List&lt;string&gt; products) { }
/// }
/// </code>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class MentorAgentAttribute : Attribute
{
    /// <summary>
    /// The agent name. Used as <c>name</c> in the <c>ChatClientAgent</c>
    /// and as the key in the Handoff graph.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Description of the agent's capabilities. Used by the Main Coordinator
    /// to decide when to hand off to this agent.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Names of other [MentorAgent] agents this agent can hand off to.
    /// Must match the <c>Name</c> values of other <c>MentorAgentAttribute</c> instances.
    /// Example: <c>HandoffTo = ["CustomerAgent", "InvoiceAgent"]</c>
    /// </summary>
    public string[] HandoffTo { get; init; } = [];

    /// <summary>
    /// Instructions (system prompt) for this agent.
    /// If null, MentorAgent auto-generates a prompt from Name and Description.
    /// </summary>
    public string? Instructions { get; init; }
}