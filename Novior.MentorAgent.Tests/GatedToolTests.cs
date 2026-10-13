using FluentAssertions;
using Novior.MentorAgent.Abstractions.Core;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Attributes;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Middleware;
using Novior.MentorAgent.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Novior.MentorAgent.Tests;

/// <summary>
/// Tests the gate that travels with a tool (<see cref="GatedAIFunction"/>).
///
/// <para>
/// This is the property the declarative-agent feature (#23) rests on. An agent built from a YAML
/// file runs its own function-calling loop inside the handoff workflow, where the coordinator's
/// middleware never sees the call — the same gap that once let
/// <c>[MentorAction(RequiredRoles = […])]</c> be silently ignored on C# specialists. If wrapping
/// ever stops enforcing the role check, a definition file becomes a way to reach a protected tool
/// without one, and nothing in the logs would say so.
/// </para>
/// </summary>
public sealed class GatedToolTests
{
    private sealed class ProtectedTools
    {
        public bool WasExecuted { get; private set; }

        // RequiresConfirmation = false declared: these tests are about the role gate alone, and since rc.15
        // an undeclared [MentorAction] would also ask for confirmation (RequireConfirmationForWrites).
        [MentorAction(
            Description = "Creates an order",
            RequiredRoles = ["OrderManager", "Admin"],
            RequiresConfirmation = false)]
        public string CreateOrder(int customerId)
        {
            WasExecuted = true;
            return $"created for {customerId}";
        }

        [MentorAction(Description = "Reads the catalogue", ReadOnly = true)]
        public string ReadCatalogue()
        {
            WasExecuted = true;
            return "ok";
        }
    }

    private static MentorAgentUIMiddleware BuildGate(MentorOptions? options = null)
    {
        options ??= new MentorOptions
        {
            AppName = "Test",
            ScanAssemblies = [typeof(GatedToolTests).Assembly],
            EnableActionFeedback = false,
        };
        var wrapped = Microsoft.Extensions.Options.Options.Create(options);
        var discovery = new MentorDiscoveryService(wrapped, NullLogger<MentorDiscoveryService>.Instance);

        return new MentorAgentUIMiddleware(
            Substitute.For<IMentorStateService>(),
            Substitute.For<IApprovalStore>(),
            wrapped,
            discovery,
            new MentorConfirmationBuilder(discovery, wrapped),
            new MentorMetrics(new MentorTokenUsage(), wrapped),
            new MentorLocalizer(MentorLanguage.English),
            NullLogger<MentorAgentUIMiddleware>.Instance);
    }

    private static AIFunction Wrap(ProtectedTools target, string method, string toolName, MentorAgentUIMiddleware gate)
    {
        var fn = AIFunctionFactory.Create(
            typeof(ProtectedTools).GetMethod(method)!, target, name: toolName, description: "x");
        return (AIFunction)GatedAIFunction.Wrap(fn, gate);
    }

    /// <summary>
    /// No authenticated user, so no roles: a role-protected tool must be refused, and — the part
    /// that actually matters — the method body must not run.
    /// </summary>
    [Fact]
    public async Task RoleProtectedTool_IsRefusedAndNeverExecuted()
    {
        var target = new ProtectedTools();
        var gated = Wrap(target, nameof(ProtectedTools.CreateOrder), "create_order", BuildGate());

        var result = await gated.InvokeAsync(new AIFunctionArguments { ["customerId"] = 3 });

        target.WasExecuted.Should().BeFalse("the gate must block before the method body runs");
        result?.ToString().Should().NotContain("created for 3");
    }

    [Fact]
    public async Task UnprotectedTool_RunsNormallyThroughTheSameGate()
    {
        // The negative control: if wrapping blocked everything, the test above would pass for the
        // wrong reason.
        var target = new ProtectedTools();
        var gated = Wrap(target, nameof(ProtectedTools.ReadCatalogue), "read_catalogue", BuildGate());

        var result = await gated.InvokeAsync(new AIFunctionArguments());

        target.WasExecuted.Should().BeTrue();
        result?.ToString().Should().Contain("ok");
    }

    /// <summary>
    /// The wrapper must not disturb what the model sees. A renamed or re-described tool would
    /// change which one the model picks, turning a security wrapper into a behaviour change.
    /// </summary>
    [Fact]
    public void Wrapping_PreservesTheToolNameAndDescription()
    {
        var inner = AIFunctionFactory.Create(
            typeof(ProtectedTools).GetMethod(nameof(ProtectedTools.ReadCatalogue))!,
            new ProtectedTools(), name: "read_catalogue", description: "Reads the catalogue");

        var gated = (AIFunction)GatedAIFunction.Wrap(inner, BuildGate());

        gated.Name.Should().Be(inner.Name);
        gated.Description.Should().Be(inner.Description);
        gated.JsonSchema.ToString().Should().Be(inner.JsonSchema.ToString());
    }
}
