namespace MentorAgentDemo.Identity;

/// <summary>The roles of the demo. MentorAgent's <c>RequiredRoles</c> name them.</summary>
public static class DemoRoles
{
    public const string Employee = "Employee";
    public const string Manager = "Manager";
    public const string Administrator = "Administrator";
}

/// <summary>A made-up person the visitor can act as. Nobody signs in: the role selector picks one.</summary>
public sealed record DemoUser(string Id, string Name, string Role)
{
    public static IReadOnlyList<DemoUser> All { get; } =
    [
        new("giulia", "Giulia Rossi", DemoRoles.Employee),
        new("marco", "Marco Bianchi", DemoRoles.Manager),
        new("sara", "Sara Conti", DemoRoles.Administrator),
    ];

    public static DemoUser Default => All[0];
}
