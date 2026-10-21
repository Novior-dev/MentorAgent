using FluentAssertions;
using Novior.MentorAgent.Attributes;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Discovery;

/// <summary>
/// BUG-182 — two methods that carry the same tool name are named at startup, with tool filtering on or off.
///
/// <para>
/// A model calls a tool by name, so an agent offered both methods reaches only one of them, and tool filtering
/// decides by name. Up to 1.0.0 the gate did too: a read-only copy of a specialist's action at level 1 decided
/// the roles of the specialist's own call (BUG-188). The only line about shared names came from tool
/// filtering, so a host without it heard nothing.
/// </para>
///
/// <para>
/// The classes below live in this assembly, which these hosts scan next to the test app's. Two of them share
/// <c>find_shared_invoice</c>; the third gives level 1 the test app's specialist action <c>get_tracking_status</c>.
/// </para>
/// </summary>
public sealed class SharedToolNameTests
{
    private sealed class SharedInvoiceReader
    {
        [MentorAction(Description = "Finds an invoice", ReadOnly = true)]
        public string FindSharedInvoice(int id) => "reader";
    }

    private sealed class SharedInvoiceAuditor
    {
        [MentorAction(Description = "Finds an invoice for an audit", ReadOnly = true, RequiredRoles = ["Auditor"])]
        public string FindSharedInvoice(int id) => "auditor";
    }

    private sealed class CoordinatorTracking
    {
        [MentorAction(Description = "Returns the tracking status of an order from the coordinator", ReadOnly = true)]
        public string GetTrackingStatus(int orderId) => "coordinator";
    }

    private static MentorHostFixture HostScanningBoth(bool filtering) => MentorHostFixture.Create(o =>
    {
        o.MemoryAutoCapture = false;
        o.ScanAssemblies = [typeof(TestOrderService).Assembly, typeof(SharedInvoiceReader).Assembly];
        o.EnableToolFiltering = filtering;
    }, withEmbeddings: filtering);

    private static List<string> SharedNameWarnings(MentorHostFixture host)
        => [.. host.Logs.Records
            .Where(r => r.Level == LogLevel.Warning && r.Message.Contains("carried by more than one method"))
            .Select(r => r.Message)];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Two_methods_with_one_tool_name_are_named_at_startup(bool filtering)
    {
        await using var host = HostScanningBoth(filtering);

        host.Get<MentorDiscoveryService>().Discover();

        var line = SharedNameWarnings(host).Should().ContainSingle(
            "the line comes from discovery, which tool filtering does not decide").Subject;
        line.Should().Contain("find_shared_invoice (SharedInvoiceReader.FindSharedInvoice, SharedInvoiceAuditor.FindSharedInvoice)");
        line.Should().Contain("get_tracking_status (CoordinatorTracking.GetTrackingStatus, TestShippingAgent.GetTrackingStatus)",
            "a level-1 method and a specialist's action share the name");
    }

    [Fact]
    public async Task One_method_found_twice_is_not_a_shared_name()
    {
        // The control: the test app's skill gives its methods to level 1 and to a team member, and they are
        // the same methods. Nothing else in the test app shares a name.
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);

        var discovery = host.Get<MentorDiscoveryService>().Discover();

        discovery.Level1Methods.Select(m => m.ToolName).Should().Contain("submit_expense_report");
        discovery.Level3Teams.SelectMany(t => t.Members).SelectMany(m => m.Tools).Select(m => m.ToolName)
            .Should().Contain("submit_expense_report");
        SharedNameWarnings(host).Should().BeEmpty();
    }
}
