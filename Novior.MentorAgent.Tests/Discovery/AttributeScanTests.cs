using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Attributes;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Harness;
using Novior.MentorAgent.Tests.Server;
using Xunit;

namespace Novior.MentorAgent.Tests.Discovery
{
    /// <summary>An attribute of the application that cannot be created: its constructor throws.</summary>
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
    public sealed class ExplodingAttribute : Attribute
    {
        public ExplodingAttribute() => throw new InvalidOperationException("An attribute of the application that cannot be created.");
    }

    /// <summary>A class of a scanned assembly with that attribute, on itself and on its action.</summary>
    [Exploding]
    public sealed class ExplodingAttributeActions
    {
        [MentorAction(Description = "Counts the parcels on the shelf")]
        [Exploding]
        public int CountParcels() => 3;
    }

    /// <summary>
    /// BUG-236 — the scan read which classes are controllers by creating every attribute of every class of the scanned
    /// assemblies (<c>GetCustomAttributes(inherit: true)</c>), so an attribute whose constructor throws stopped the
    /// application when it started. The names are now read from the metadata (rule <c>scan.attributes-not-created</c>).
    /// </summary>
    public sealed class AttributeScanTests
    {
        private static DiscoveryResult Discover(Assembly assembly)
            => new MentorDiscoveryService(
                    Microsoft.Extensions.Options.Options.Create(new MentorOptions { AppName = "Scan", ScanAssemblies = [assembly] }),
                    NullLogger<MentorDiscoveryService>.Instance)
                .Discover();

        [Fact]
        [Trait("Rule", "scan.attributes-not-created")]
        [Trait("Rule", "scan.controllers-are-endpoints")]
        public void A_class_with_an_attribute_that_throws_is_read_and_a_controller_is_still_one()
        {
            var created = () => typeof(ExplodingAttributeActions).GetCustomAttributes(inherit: true);
            created.Should().Throw<InvalidOperationException>("the fixture's attribute cannot be created: the scan must not create it");

            MentorDiscoveryService.IsController(typeof(ExplodingAttributeActions)).Should().BeFalse();
            MentorDiscoveryService.IsController(typeof(StockEndpointController)).Should().BeTrue("[ApiController]");
            MentorDiscoveryService.IsController(typeof(PlainEndpointController)).Should().BeTrue("it derives from ControllerBase, which has [Controller]");
            MentorDiscoveryService.IsController(typeof(NotAnEndpointController)).Should().BeFalse("[NonController] wins over the base class's [Controller]");

            var found = Discover(typeof(AttributeScanTests).Assembly);

            found.Level1Methods.Should().ContainSingle(m => m.ToolName == "count_parcels", "the class is read, and its action found");
            found.Level1Methods.Should().NotContain(m => m.DeclaringType == typeof(StockEndpointController), "a controller is still not read");
        }

        [Fact]
        [Trait("Rule", "scan.attributes-not-created")]
        public void A_class_whose_attributes_name_a_type_that_does_not_load_is_not_a_controller()
        {
            MentorDiscoveryService.IsController(new UnloadableAttributes(), out var unreadable).Should().BeFalse();
            unreadable.Should().BeTrue("the scan skips the class, with a warning that names it");

            MentorDiscoveryService.IsController(typeof(StockEndpointController), out unreadable).Should().BeTrue();
            unreadable.Should().BeFalse();
        }

        [Fact]
        [Trait("Rule", "scan.attributes-not-created")]
        [Trait("Rule", "candidate.roles-from-authorize")]
        public async Task A_candidate_with_an_attribute_that_throws_is_read_with_the_roles_of_its_Authorize()
        {
            await using var host = MentorHostFixture.Create(o =>
            {
                o.MemoryAutoCapture = false;
                o.AutoMentor.Enabled = true;
                o.AutoMentor.Scan = [typeof(AttributeScanTests).Assembly];
                o.AutoMentor.Namespaces = ["CandidateShop.Exploding"];
            }, s => s.AddScoped<CandidateShop.Exploding.ParcelDesk>());

            var weigh = host.Get<IMentorActionCatalog>().GetActions()
                .Should().ContainSingle(a => a.Origin == MentorActionOrigins.Candidate && a.ToolName == "weigh").Which;
            weigh.RequiredRoles.Should().Equal("Manager");
        }

        /// <summary>A class whose attributes' metadata names a type that does not load.</summary>
        private sealed class UnloadableAttributes() : TypeDelegator(typeof(ExplodingAttributeActions))
        {
            public override IList<CustomAttributeData> GetCustomAttributesData()
                => throw new FileNotFoundException("The assembly of one of the attributes is not there.", "Missing.Attributes");
        }
    }
}

namespace CandidateShop.Exploding
{
    /// <summary>A service in the AutoMentor's perimeter, with an attribute that cannot be created beside its [Authorize].</summary>
    [Novior.MentorAgent.Tests.Discovery.Exploding]
    [Microsoft.AspNetCore.Authorization.Authorize(Roles = "Manager")]
    public sealed class ParcelDesk
    {
        [Novior.MentorAgent.Tests.Discovery.Exploding]
        public string Weigh(int parcelId) => $"Parcel {parcelId}: 1 kg.";
    }
}
