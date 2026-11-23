using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Novior.MentorAgent.Attributes;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Options;
using Xunit;

namespace Novior.MentorAgent.Tests.Discovery;

/// <summary>
/// The scan's typed reads (<c>GetCustomAttribute&lt;T&gt;</c>) resolve the type of every attribute of the member they read,
/// so an attribute whose type, or its assembly, does not load made the scan throw, and the application with it, from
/// 1.0.0. BUG-236 skips such a class; a method or a team member's property is now skipped with a warning, and the rest of
/// the class stays. The fixture is a real assembly, written here, whose attributes' assembly is not where it can load.
/// </summary>
public sealed class UnloadableMemberScanTests
{
    private static readonly Lazy<Assembly> Scanned = new(Build);

    private static DiscoveryResult Discover(ListLogger logger)
        => new MentorDiscoveryService(
                Microsoft.Extensions.Options.Options.Create(new MentorOptions { AppName = "Scan", ScanAssemblies = [Scanned.Value] }),
                logger)
            .Discover();

    [Fact]
    [Trait("Rule", "scan.attributes-not-created")]
    public void A_method_whose_attributes_name_an_assembly_that_does_not_load_is_skipped_and_the_class_stays()
    {
        var parcels = Scanned.Value.GetType("Scanned.Shop.Shelf", throwOnError: true)!.GetMethod("CountParcels")!;
        var read = () => parcels.GetCustomAttribute<MentorActionAttribute>();
        read.Should().Throw<Exception>().Where(e => MentorDiscoveryService.IsLoadFailure(e),
            "the fixture's attribute is in an assembly that is not there: the typed read throws as it does in an application");

        var logger = new ListLogger();
        var found = Discover(logger);

        found.Level1Methods.Select(m => m.ToolName).Should().Equal(["count_boxes"],
            "the method that cannot be read is skipped, the other action of the class is found");
        logger.Warnings.Should().ContainSingle(w => w.Contains("Scanned.Shop.Shelf.CountParcels"))
            .Which.Should().Contain("does not load").And.Contain("The method is skipped");
    }

    [Fact]
    [Trait("Rule", "scan.attributes-not-created")]
    public void A_team_member_whose_attributes_name_an_assembly_that_does_not_load_is_skipped_and_the_team_stays()
    {
        var logger = new ListLogger();
        var found = Discover(logger);

        var team = found.Level3Teams.Should().ContainSingle(t => t.Attribute.Name == "packing").Which;
        team.Members.Select(m => m.Attribute.Role).Should().Equal(["packer"]);
        logger.Warnings.Should().ContainSingle(w => w.Contains("Scanned.Shop.PackingTeam.Checker"))
            .Which.Should().Contain("The property is skipped");
    }

    // ── The fixture ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Two assemblies written to a folder of their own: <c>Missing.Attributes</c>, with an attribute, and the scanned one,
    /// whose members carry it. The scanned one is loaded in a context of its own, where the attributes' assembly cannot
    /// be found.
    /// </summary>
    private static Assembly Build()
    {
        var root = Path.Combine(Path.GetTempPath(), "mentor-scan-" + Guid.NewGuid().ToString("N"));
        var attributesDir = Directory.CreateDirectory(Path.Combine(root, "attributes")).FullName;
        var scannedDir = Directory.CreateDirectory(Path.Combine(root, "scanned")).FullName;

        var attributes = new PersistedAssemblyBuilder(
            new AssemblyName("Missing.Attributes") { Version = new Version(1, 0, 0, 0) }, typeof(object).Assembly);
        var attributeType = attributes.DefineDynamicModule("Missing.Attributes").DefineType(
            "Missing.Attributes.AuditedAttribute", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class,
            typeof(Attribute));
        attributeType.DefineDefaultConstructor(MethodAttributes.Public);
        attributeType.CreateType();
        var attributesPath = Path.Combine(attributesDir, "Missing.Attributes.dll");
        attributes.Save(attributesPath);

        // Loaded only to name its constructor: the scanned assembly's context never sees this one.
        var audited = new AssemblyLoadContext("mentor-scan-attributes", isCollectible: true)
            .LoadFromAssemblyPath(attributesPath).GetType("Missing.Attributes.AuditedAttribute", throwOnError: true)!;
        var auditedAttribute = new CustomAttributeBuilder(audited.GetConstructor(Type.EmptyTypes)!, []);

        var scanned = new PersistedAssemblyBuilder(new AssemblyName("Mentor.Scanned.Shop"), typeof(object).Assembly);
        var module = scanned.DefineDynamicModule("Mentor.Scanned.Shop");

        var shelf = module.DefineType("Scanned.Shop.Shelf", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class);
        shelf.DefineDefaultConstructor(MethodAttributes.Public);
        Action(shelf, "CountBoxes", "Counts the boxes", null);
        Action(shelf, "CountParcels", "Counts the parcels", auditedAttribute);
        shelf.CreateType();

        var team = module.DefineType("Scanned.Shop.PackingTeam", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class);
        team.DefineDefaultConstructor(MethodAttributes.Public);
        team.SetCustomAttribute(Named(typeof(MentorTeamAttribute), ("Name", "packing"), ("Description", "Packs the orders")));
        Member(team, "Packer", "packer", null);
        Member(team, "Checker", "checker", auditedAttribute);
        team.CreateType();

        var scannedPath = Path.Combine(scannedDir, "Mentor.Scanned.Shop.dll");
        scanned.Save(scannedPath);
        return new AssemblyLoadContext("mentor-scan").LoadFromAssemblyPath(scannedPath);
    }

    private static void Action(TypeBuilder type, string name, string description, CustomAttributeBuilder? audited)
    {
        var method = type.DefineMethod(name, MethodAttributes.Public | MethodAttributes.HideBySig, typeof(int), Type.EmptyTypes);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldc_I4_3);
        il.Emit(OpCodes.Ret);
        method.SetCustomAttribute(Named(typeof(MentorActionAttribute), ("Description", description)));
        if (audited is not null) method.SetCustomAttribute(audited);
    }

    private static void Member(TypeBuilder type, string name, string role, CustomAttributeBuilder? audited)
    {
        var field = type.DefineField("_" + name, typeof(object), FieldAttributes.Private);
        var getter = type.DefineMethod("get_" + name,
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig, typeof(object), Type.EmptyTypes);
        var il = getter.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, field);
        il.Emit(OpCodes.Ret);
        var property = type.DefineProperty(name, PropertyAttributes.None, typeof(object), Type.EmptyTypes);
        property.SetGetMethod(getter);
        property.SetCustomAttribute(Named(typeof(TeamMemberAttribute), ("Role", role), ("Instructions", "Works on the orders")));
        if (audited is not null) property.SetCustomAttribute(audited);
    }

    private static CustomAttributeBuilder Named(Type attribute, params (string Name, object Value)[] values)
        => new(attribute.GetConstructor(Type.EmptyTypes)!, [],
            [.. values.Select(v => attribute.GetProperty(v.Name)!)], [.. values.Select(v => v.Value)]);

    /// <summary>Keeps the warnings it is given.</summary>
    private sealed class ListLogger : ILogger<MentorDiscoveryService>
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
