using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Xunit;

namespace Novior.MentorAgent.Tests.Discovery
{
    /// <summary>
    /// Story 1569 — the AutoMentor's candidates: the public methods of the services in the perimeter the code declares
    /// become actions of the coordinator of origin <c>candidate</c>, off and confirmed, and the trace lists them with
    /// those a rule left out (slice A). Slice B: their description comes from the XML documentation file next to the
    /// assembly (here <c>Discovery/CandidateDocs.xml</c>, copied as <c>Novior.MentorAgent.Tests.xml</c>), their roles from
    /// <c>[Authorize]</c>. Every rule of the <c>candidate</c> area is named on its test.
    /// </summary>
    public sealed class CandidateTests
    {
        private const string Invoices = "CandidateShop.Services.InvoiceService";

        private static MentorHostFixture Host(Action<MentorOptions>? configure = null, Action<IServiceCollection>? services = null,
            bool enabled = true)
            => MentorHostFixture.Create(o =>
            {
                o.MemoryAutoCapture = false;
                o.AutoMentor.Enabled = enabled;
                o.AutoMentor.Scan = [typeof(CandidateTests).Assembly];
                o.AutoMentor.Namespaces = ["CandidateShop.Services"];
                o.Actions.Add<CandidateShop.Services.InvoiceService>(nameof(CandidateShop.Services.InvoiceService.Archive),
                    a => a.Describe("Archives an invoice"));
                configure?.Invoke(o);
            }, s =>
            {
                s.AddScoped<CandidateShop.Services.InvoiceService>();
                s.AddScoped<CandidateShop.Services.NamingService>();
                s.AddScoped<CandidateShop.Services.PaymentGateway>();
                s.AddScoped<CandidateShop.Services.Orders.ShipmentService>();
                s.AddScoped<CandidateShop.ServicesLegacy.LegacyService>();
                s.AddScoped<CandidateShop.Other.OutsideService>();
                services?.Invoke(s);
            });

        /// <summary>The perimeter of slice B: <c>CandidateShop.Guarded</c> only.</summary>
        private static MentorHostFixture GuardedHost(Action<IServiceCollection>? services = null)
            => Host(o => o.AutoMentor.Namespaces = ["CandidateShop.Guarded"], s =>
            {
                s.AddScoped<CandidateShop.Guarded.ApprovalService>();
                s.AddScoped<CandidateShop.Guarded.OpenService>();
                s.AddScoped<CandidateShop.Guarded.SealedApprovalService>();
                services?.Invoke(s);
            });

        private static MentorActionInfo Candidate(MentorHostFixture host, string name)
            => Candidates(host).Should().ContainSingle(a => a.ToolName == name).Which;

        private static IReadOnlyList<MentorActionInfo> Candidates(MentorHostFixture host)
            => [.. host.Get<IMentorActionCatalog>().GetActions().Where(a => a.Origin == MentorActionOrigins.Candidate)];

        private static IReadOnlyList<string> CandidateNames(MentorHostFixture host) => [.. Candidates(host).Select(a => a.ToolName)];

        private static MentorActionRegistration Registration(MentorHostFixture host, string type)
            => host.Get<IMentorActionCatalog>().GetRegistrations().Should().ContainSingle(r => r.Id == "candidate:" + type).Which;

        [Fact]
        [Trait("Rule", "candidate.opt-in")]
        public async Task Without_AutoMentor_nothing_is_a_candidate()
        {
            await using var host = Host(enabled: false);

            Candidates(host).Should().BeEmpty();
            host.Get<IMentorActionCatalog>().GetRegistrations().Should().NotContain(r => r.Origin == MentorActionOrigins.Candidate);
        }

        [Fact]
        [Trait("Rule", "candidate.opt-in")]
        public async Task An_empty_perimeter_gives_no_candidate_and_says_so()
        {
            await using var host = Host(o => o.AutoMentor.Namespaces = []);

            Candidates(host).Should().BeEmpty();
            host.Logs.ScopedRecords.Should().Contain(r => r.Level == LogLevel.Warning && r.Message.Contains("AutoMentor.Namespaces is empty"));
        }

        [Fact]
        [Trait("Rule", "candidate.in-perimeter")]
        public async Task Only_the_classes_in_the_perimeter_and_under_it_give_candidates()
        {
            await using var host = Host();

            var types = Candidates(host).Select(a => a.RegistrationId).Distinct().ToList();
            types.Should().BeEquivalentTo(
                ["candidate:" + Invoices, "candidate:CandidateShop.Services.Orders.ShipmentService"],
                "Shop.Services covers Shop.Services.Orders, not Shop.ServicesLegacy nor another namespace");
            CandidateNames(host).Should().NotContain(["wipe", "delete_everything"]);
        }

        [Fact]
        [Trait("Rule", "candidate.in-perimeter")]
        public async Task MentorAgent_own_types_are_never_candidates()
        {
            await using var host = Host(o =>
            {
                o.AutoMentor.Scan = [typeof(MentorOptions).Assembly, typeof(MentorActionInfo).Assembly];
                o.AutoMentor.Namespaces = ["Novior.MentorAgent"];
            });

            Candidates(host).Should().BeEmpty();
            host.Get<IMentorActionCatalog>().GetRegistrations().Should().NotContain(r => r.Origin == MentorActionOrigins.Candidate);
        }

        [Fact]
        [Trait("Rule", "candidate.shape")]
        public async Task Only_public_instance_methods_the_class_declares_are_candidates()
        {
            await using var host = Host();

            Candidates(host).Where(a => a.RegistrationId == "candidate:" + Invoices).Select(a => a.ToolName)
                .Should().Equal(["issue", "total_of"],
                    "not internal, static, generic, out, accessors, object's methods, nor the excluded or registered ones");
        }

        [Fact]
        [Trait("Rule", "candidate.excluded")]
        public async Task MentorExclude_keeps_a_method_or_a_class_out_and_out_of_the_trace()
        {
            await using var host = Host();

            CandidateNames(host).Should().NotContain(["purge", "charge"]);
            var trace = host.Get<IMentorActionCatalog>().GetRegistrations();
            trace.SelectMany(r => r.Actions.Concat(r.Rejected.Select(x => x.Name)))
                .Should().NotContain(["purge", "Purge", "charge", "Charge"], "a name excluded on purpose does not leave the code");
            trace.Should().NotContain(r => r.Source == typeof(CandidateShop.Services.PaymentGateway).FullName);
        }

        [Fact]
        [Trait("Rule", "candidate.not-yet-an-action")]
        public async Task A_method_that_is_already_an_action_keeps_its_own_registration()
        {
            await using var host = Host();

            var archive = host.Get<IMentorActionCatalog>().GetActions().Should().ContainSingle(a => a.ToolName == "archive").Which;
            archive.Origin.Should().Be(MentorActionOrigins.Fluent);
            archive.EnabledByDefault.Should().BeTrue();
        }

        [Fact]
        [Trait("Rule", "candidate.di-resolvable")]
        public async Task A_class_the_container_cannot_give_is_left_out_with_its_rule()
        {
            await using var host = Host();

            var unregistered = Registration(host, "CandidateShop.Services.UnregisteredService");
            unregistered.Actions.Should().BeEmpty();
            unregistered.Rejected.Should().BeEquivalentTo([new MentorActionRejection { Name = "Ping", Rule = "candidate.di-resolvable" }]);
        }

        [Fact]
        [Trait("Rule", "candidate.name")]
        public async Task A_name_another_action_or_MentorAgent_has_is_left_out_with_its_rule()
        {
            await using var host = Host();

            Registration(host, Invoices).Rejected.Should().ContainEquivalentOf(
                new MentorActionRejection { Name = "CountOpenOrders", Rule = "candidate.name" }, "count_open_orders is an action of the app");
            Registration(host, "CandidateShop.Services.NamingService").Rejected.Should().BeEquivalentTo(
                [new MentorActionRejection { Name = "Remember", Rule = "candidate.name" },
                 new MentorActionRejection { Name = "NavigateTo", Rule = "candidate.name" },
                 new MentorActionRejection { Name = "Issue", Rule = "candidate.name" }],
                "MentorAgent's own tools, and a name another candidate took first");
        }

        [Fact]
        [Trait("Rule", "candidate.name")]
        public async Task A_candidate_gives_its_name_to_an_endpoint_that_comes_later()
        {
            await using var host = Host();
            var discovery = host.Get<MentorDiscoveryService>();
            var handler = typeof(CandidateShop.Other.OutsideService).GetMethod(nameof(CandidateShop.Other.OutsideService.DeleteEverything))!;

            discovery.AddEndpointActions([new MethodDiscoveryInfo(typeof(CandidateShop.Other.OutsideService), handler, "issue",
                "Issues through an endpoint", null, true, null, [], null, Origin: MentorActionOrigins.Endpoint)]);

            var issue = host.Get<IMentorActionCatalog>().GetActions().Should().ContainSingle(a => a.ToolName == "issue").Which;
            issue.Origin.Should().Be(MentorActionOrigins.Endpoint, "the application does not stop for a candidate");
            issue.Method.Should().BeNull("an endpoint's method is its handler's, not one the application wrote as an action");
            Registration(host, Invoices).Rejected.Should().ContainEquivalentOf(new MentorActionRejection { Name = "Issue", Rule = "candidate.name" });
        }

        [Fact]
        [Trait("Rule", "candidate.schema")]
        public async Task A_parameter_a_tool_cannot_describe_leaves_the_method_out_with_its_rule()
        {
            await using var host = Host();

            Registration(host, Invoices).Rejected.Should().ContainEquivalentOf(new MentorActionRejection { Name = "Scan", Rule = "candidate.schema" });
            CandidateNames(host).Should().NotContain("scan");
        }

        [Fact]
        [Trait("Rule", "candidate.off-and-confirmed")]
        public async Task A_candidate_is_an_action_of_the_coordinator_off_and_confirmed_and_not_offered()
        {
            await using var host = Host();
            host.Chat!.SayRepeatedly("ok", times: 2);

            var issue = Candidates(host).Should().ContainSingle(a => a.ToolName == "issue").Which;
            issue.AgentKind.Should().Be(MentorAgentKind.Coordinator);
            issue.EnabledByDefault.Should().BeFalse();
            issue.RequiresConfirmation.Should().BeTrue();
            issue.ReadOnly.Should().BeFalse();
            issue.RegistrationId.Should().Be("candidate:" + Invoices);

            await host.RunTurnAsync("hello");
            host.FirstToolsSent.Should().NotContain(["issue", "total_of", "ship"], "a candidate is off until a governor turns it on");
        }

        [Fact]
        [Trait("Rule", "candidate.off-and-confirmed")]
        public async Task A_governor_turns_a_candidate_on_and_it_is_offered()
        {
            var governor = new TestActionGovernor { Enabled = r => r.ToolName == "issue" ? true : null };
            await using var host = Host(services: s => s.AddSingleton<IMentorActionGovernor>(governor));
            host.Chat!.SayRepeatedly("ok", times: 2);

            await host.RunTurnAsync("hello");

            host.FirstToolsSent.Should().Contain("issue").And.NotContain(["total_of", "ship"]);
        }

        [Fact]
        [Trait("Rule", "candidate.never-outside")]
        public async Task A_governor_that_says_yes_to_everything_turns_nothing_on_outside_the_perimeter()
        {
            var governor = new TestActionGovernor { Enabled = _ => true };
            await using var host = Host(services: s => s.AddSingleton<IMentorActionGovernor>(governor));
            host.Chat!.SayRepeatedly("ok", times: 2);

            await host.RunTurnAsync("hello");

            string[] outside = ["wipe", "delete_everything", "purge", "charge", "ping", "scan", "hidden", "helper"];
            host.FirstToolsSent.Should().Contain(["issue", "total_of", "ship"]).And.NotContain(outside);
            governor.Seen.Select(r => r.ToolName).Should().NotContain(outside, "the governor is asked only about actions");
            host.Get<IMentorActionCatalog>().GetActions().Select(a => a.ToolName).Should().NotContain(outside);
        }

        [Fact]
        [Trait("Rule", "candidate.trace")]
        public async Task The_trace_has_one_registration_per_class_with_its_candidates_and_what_a_rule_left_out()
        {
            await using var host = Host();

            var invoices = Registration(host, Invoices);
            invoices.Origin.Should().Be(MentorActionOrigins.Candidate);
            invoices.Source.Should().Be(Invoices);
            invoices.Actions.Should().Equal("issue", "total_of");
            Candidates(host).Should().ContainSingle(a => a.ToolName == "issue").Which.Method
                .Should().BeSameAs(typeof(CandidateShop.Services.InvoiceService).GetMethod(nameof(CandidateShop.Services.InvoiceService.Issue)),
                    "a candidate is the application's method, read for its parameters");
            invoices.Rejected.Select(r => r.Rule).Should().OnlyContain(r => r.StartsWith("candidate.", StringComparison.Ordinal));
            invoices.Rejected.Select(r => r.Name).Should().BeEquivalentTo(["CountOpenOrders", "Scan"]);
            host.Logs.ScopedRecords.Should().Contain(r => r.Level == LogLevel.Information && r.Message.Contains("AutoMentor: 3 candidate(s)"));
        }

        [Fact]
        [Trait("Rule", "candidate.described-by-xml")]
        public async Task A_candidate_is_described_by_the_summary_of_its_documentation_comment_without_its_tags()
        {
            await using var host = GuardedHost();

            Candidate(host, "approve").Description.Should().Be(
                "Approves the expense expenseId for the ApprovalService, as Manager. Nothing is paid until the end of the month.",
                "a reference gives its name, <c> its text, <para> a space; white space is one space");
            Candidate(host, "remind").Description.Should().Be("Reminds the people in to of the expense, after days days or null.",
                "the member id names Nullable{Int32} and List{String} as the compiler does");
            Candidate(host, "total").Description.Should().Be("Totals the expenses ids. It only reads.",
                "an array is Int32[], and two paragraphs side by side are apart");
        }

        [Fact]
        [Trait("Rule", "candidate.described-by-xml")]
        public async Task A_candidate_without_a_comment_or_without_a_file_has_no_description()
        {
            await using var host = GuardedHost();

            Candidate(host, "note").Description.Should().BeNull("the method has no comment in the file");
            Candidate(host, "grant_refund").Description.Should().BeNull("the summary is only white space");
            MentorXmlDocumentation.Summaries(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xml"), NullLogger.Instance)
                .Should().BeEmpty("no file, no description");
        }

        [Fact]
        [Trait("Rule", "candidate.described-by-xml")]
        public void A_documentation_file_that_cannot_be_read_gives_no_description_and_a_warning()
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xml");
            File.WriteAllText(path, "<doc><members><member name=\"M:A.B.C\"><summary>broken");
            try
            {
                var logger = new ListLogger();
                MentorXmlDocumentation.Summaries(path, logger).Should().BeEmpty();
                logger.Warnings.Should().ContainSingle().Which.Should().Contain("could not be read");

                File.WriteAllText(path, "<!DOCTYPE doc [<!ENTITY x SYSTEM \"file:///c:/windows/win.ini\">]><doc><members>" +
                                        "<member name=\"M:A.B.C\"><summary>&x;</summary></member></members></doc>");
                MentorXmlDocumentation.Summaries(path, logger).Should().BeEmpty("a document type is refused, not resolved");
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        [Trait("Rule", "candidate.roles-from-authorize")]
        public async Task A_candidate_takes_the_roles_of_its_Authorize_any_one_of_them()
        {
            // The attributes are ASP.NET Core's, from the shared framework as in a web app; the core, which references no
            // ASP.NET Core assembly but the components', reads them by the name of their interfaces.
            typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute).Assembly.Location
                .Should().Contain(Path.Combine("shared", "Microsoft.AspNetCore.App"));
            await using var host = GuardedHost();

            Candidate(host, "approve").RequiredRoles.Should().BeEquivalentTo(["Manager", "Admin"], "the class's [Authorize(Roles)]");
            Candidate(host, "close").RequiredRoles.Should().BeEquivalentTo(["Manager", "Admin"],
                "the method asks for the same roles as the class: one set");
            Candidate(host, "release").RequiredRoles.Should().Equal(["Auditor"], "inherited from the base class");
            Candidate(host, "status").RequiredRoles.Should().BeEmpty("[AllowAnonymous] on the method");
            Candidate(host, "note").RequiredRoles.Should().BeEmpty("no [Authorize]");
            Candidate(host, "escalate").RequiredRoles.Should().Equal(["Manager"], "an attribute derived from [Authorize]");
        }

        [Fact]
        [Trait("Rule", "candidate.roles-from-authorize")]
        public async Task Roles_asked_together_or_a_policy_give_no_roles_and_a_line_in_the_log()
        {
            await using var host = GuardedHost();

            Candidate(host, "seal").RequiredRoles.Should().BeEmpty("Manager or Admin, and Auditor: an action's roles cannot say it");
            Candidate(host, "grant_refund").RequiredRoles.Should().BeEmpty("a policy is not roles");
            host.Logs.ScopedRecords.Should().Contain(r => r.Level == LogLevel.Information
                && r.Message.Contains("ApprovalService.Seal asks for roles together (Manager or Admin and Auditor)"));
            host.Logs.ScopedRecords.Should().Contain(r => r.Level == LogLevel.Information
                && r.Message.Contains("ApprovalService.GrantRefund names the policy Finance"));
        }

        [Fact]
        [Trait("Rule", "candidate.roles-from-authorize")]
        public async Task A_candidate_turned_on_is_offered_only_to_a_user_with_one_of_its_roles()
        {
            var governor = new TestActionGovernor { Enabled = r => r.ToolName is "approve" or "note" ? true : null };

            await using var employee = GuardedHost(s => s.AddSingleton<IMentorActionGovernor>(governor));
            employee.Chat!.SayRepeatedly("ok", times: 2);
            employee.Auth.SignIn("giulia", "Employee");
            await employee.RunTurnAsync("hello");
            employee.FirstToolsSent.Should().Contain("note").And.NotContain("approve");

            await using var manager = GuardedHost(s => s.AddSingleton<IMentorActionGovernor>(governor));
            manager.Chat!.SayRepeatedly("ok", times: 2);
            manager.Auth.SignIn("marco", "Manager");
            await manager.RunTurnAsync("hello");
            manager.FirstToolsSent.Should().Contain(["approve", "note"]);
        }

        /// <summary>Keeps the warnings it is given.</summary>
        private sealed class ListLogger : ILogger
        {
            public List<string> Warnings { get; } = [];

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
            }
        }
    }
}

// The services of the tests' perimeter: outside the tests' namespace, as an application's would be.
namespace CandidateShop.Services
{
    public sealed class InvoiceService
    {
        public string Issue(int orderId) => "INV-" + orderId;

        public decimal TotalOf(int invoiceId) => invoiceId;

        public string Archive(int invoiceId) => "archived " + invoiceId;

        public int CountOpenOrders() => 0;

        public int Scan(ReadOnlySpan<char> text) => text.Length;

        [Novior.MentorAgent.Attributes.MentorExclude]
        public void Purge() { }

        internal void Hidden() { }

        public static void Helper() { }

        public T Echo<T>(T value) => value;

        public bool TryFind(int id, out string name)
        {
            name = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }

        public string Label { get; set; } = "";

        public override string ToString() => "invoices";
    }

    /// <summary>Names a model calls MentorAgent's own tools by, and one another candidate took first.</summary>
    public sealed class NamingService
    {
        public void Remember(string text) { }

        public void NavigateTo(string page) { }

        public string Issue(int id) => "again " + id;
    }

    [Novior.MentorAgent.Attributes.MentorExclude]
    public sealed class PaymentGateway
    {
        public void Charge(int amount) { }
    }

    /// <summary>Not in the container.</summary>
    public sealed class UnregisteredService
    {
        public void Ping() { }
    }
}

namespace CandidateShop.Services.Orders
{
    public sealed class ShipmentService
    {
        public string Ship(int orderId) => "shipped " + orderId;
    }
}

namespace CandidateShop.ServicesLegacy
{
    public sealed class LegacyService
    {
        public void Wipe() { }
    }
}

// Slice B: the documentation comments are in Discovery/CandidateDocs.xml, as the compiler would write them.
namespace CandidateShop.Guarded
{
    [Microsoft.AspNetCore.Authorization.Authorize(Roles = "Manager, Admin")]
    public class ApprovalService
    {
        public string Approve(int expenseId) => "approved " + expenseId;

        [Microsoft.AspNetCore.Authorization.AllowAnonymous]
        public string Status(int expenseId) => "open " + expenseId;

        [Microsoft.AspNetCore.Authorization.Authorize(Roles = "Admin,Manager")]
        public void Close(int expenseId) { }

        [Microsoft.AspNetCore.Authorization.Authorize(Roles = "Auditor")]
        public void Seal(int expenseId) { }

        [Microsoft.AspNetCore.Authorization.Authorize(Policy = "Finance")]
        public void GrantRefund(int expenseId) { }

        public decimal Total(int[] ids) => ids.Length;
    }

    public sealed class OpenService
    {
        public void Note(string text) { }

        public void Remind(int? days, List<string> to) { }

        [ManagersOnly]
        public void Escalate(int expenseId) { }
    }

    public sealed class ManagersOnlyAttribute : Microsoft.AspNetCore.Authorization.AuthorizeAttribute
    {
        public ManagersOnlyAttribute() => Roles = "Manager";
    }

    [Microsoft.AspNetCore.Authorization.Authorize(Roles = "Auditor")]
    public abstract class AuditedService
    {
    }

    public sealed class SealedApprovalService : AuditedService
    {
        public void Release(int expenseId) { }
    }
}

namespace CandidateShop.Other
{
    public sealed class OutsideService
    {
        public void DeleteEverything() { }
    }
}
