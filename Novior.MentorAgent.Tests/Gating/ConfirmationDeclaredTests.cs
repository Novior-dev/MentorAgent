using System.Reflection;
using FluentAssertions;
using Novior.MentorAgent.Attributes;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Harness;
using Xunit;

namespace Novior.MentorAgent.Tests.Gating;

/// <summary>
/// Whether a <c>[MentorAction]</c> declared its confirmation, read from outside the library.
///
/// <para>
/// With <c>RequireConfirmationForWrites</c> on, the default, an action that declares nothing asks for confirmation and
/// one that declares <c>RequiresConfirmation = false</c> does not, yet <c>RequiresConfirmation</c> reads false for
/// both. A package that shows an administrator the actions of an application could not tell them apart:
/// <c>RequiresConfirmationDeclared</c> was internal. The fixture's <c>IssueRefundAsync</c> declares true,
/// <c>ShowOrderCard</c> declares false, and <c>MarkOrderReviewed</c> declares nothing.
/// </para>
/// </summary>
public sealed class ConfirmationDeclaredTests
{
    private static MentorActionAttribute Of(string method)
        => typeof(TestOrderService).GetMethod(method)!.GetCustomAttribute<MentorActionAttribute>()!;

    private static async Task<bool> AsksInTheHost(string toolName)
    {
        await using var host = MentorHostFixture.Create();
        return host.Get<MentorDiscoveryService>().Discover().Level1Methods
            .Single(m => m.ToolName == toolName).RequiresConfirmation;
    }

    [Fact]
    public void The_declaration_can_be_read_from_outside_the_library_and_not_written()
    {
        var property = typeof(MentorActionAttribute).GetProperty(nameof(MentorActionAttribute.RequiresConfirmationDeclared));

        property.Should().NotBeNull();
        property!.GetMethod!.IsPublic.Should().BeTrue("a package outside the library reads it");
        property.SetMethod.Should().BeNull("only writing RequiresConfirmation in the attribute declares it");
    }

    [Fact]
    public async Task An_action_that_declares_true_is_declared_and_asks()
    {
        var action = Of(nameof(TestOrderService.IssueRefundAsync));

        action.RequiresConfirmationDeclared.Should().BeTrue();
        action.RequiresConfirmation.Should().BeTrue();
        (await AsksInTheHost("issue_refund")).Should().BeTrue();
    }

    [Fact]
    public async Task An_action_that_declares_false_is_declared_and_does_not_ask()
    {
        var action = Of(nameof(TestOrderService.ShowOrderCard));

        action.RequiresConfirmationDeclared.Should().BeTrue("false written in the attribute is a decision");
        action.RequiresConfirmation.Should().BeFalse();
        (await AsksInTheHost("show_order_card")).Should().BeFalse();
    }

    [Fact]
    public async Task An_action_that_declares_nothing_is_not_declared_and_asks_by_default()
    {
        var action = Of(nameof(TestOrderService.MarkOrderReviewed));

        action.RequiresConfirmationDeclared.Should().BeFalse();
        action.RequiresConfirmation.Should().BeFalse("the attribute alone reads false, as for the action that declares false");
        (await AsksInTheHost("mark_order_reviewed")).Should().BeTrue("RequireConfirmationForWrites is on by default");
    }
}
