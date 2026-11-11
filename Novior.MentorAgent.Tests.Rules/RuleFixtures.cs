using System.ComponentModel;
using Novior.MentorAgent.Attributes;

namespace Novior.MentorAgent.Tests.Rules;

// One group of classes per rule of ACTION-RULES.md, named after it. Every method name is unique in the
// assembly, so a test can look a tool up by name without the shared-name warning getting in the way.

// ── scan.concrete-classes ──────────────────────────────────────────────────────────────────────────

public abstract class AbstractRuleActions
{
    [MentorAction(Description = "An action on an abstract class")]
    public string ActionOnAbstractClass() => "abstract";
}

public struct StructRuleActions
{
    [MentorAction(Description = "An action on a struct")]
    public readonly string ActionOnStruct() => "struct";
}

// ── scan.public-instance-declared ──────────────────────────────────────────────────────────────────

public class MethodShapeActions
{
    [MentorAction(Description = "A public instance action")]
    public string PublicInstanceAction() => "public";

    [MentorAction(Description = "A static action")]
    public static string StaticAction() => "static";

    [MentorAction(Description = "An internal action")]
    internal string InternalAction() => "internal";

    [MentorAction(Description = "A protected action")]
    protected string ProtectedAction() => "protected";

    [MentorAction(Description = "A private action")]
    private string PrivateAction() => "private";

    // Keeps the private method referenced, so no analyzer reports it as unused.
    internal string CallPrivate() => PrivateAction();
}

public class BaseRuleActions
{
    [MentorAction(Description = "An action the derived class inherits without overriding")]
    public string InheritedNotOverridden() => "base";

    [MentorAction(Description = "An action the derived class overrides")]
    public virtual string OverriddenMentorAction() => "base";

    [Description("A described method the derived class overrides")]
    public virtual string OverriddenDescribedMethod() => "base";
}

public class DerivedRuleActions : BaseRuleActions
{
    public override string OverriddenMentorAction() => "derived";

    public override string OverriddenDescribedMethod() => "derived";
}

// ── scan.marker and scan.mentor-action-wins ────────────────────────────────────────────────────────

public class MarkerActions
{
    [Description("")]
    public string EmptyDescriptionMethod() => "empty";

    [Description("   ")]
    public string BlankDescriptionMethod() => "blank";

    public string UnmarkedMethod() => "unmarked";

    [MentorAction(Description = "")]
    public string EmptyMentorActionDescription() => "empty action";

    [MentorAction(Description = "What MentorAction says", RequiresConfirmation = true, RequiredRoles = ["Clerk"])]
    [Description("What Description says")]
    public string BothAttributesMethod() => "both";
}

// ── roles.any-of ───────────────────────────────────────────────────────────────────────────────────

public class RoleRuleActions
{
    [MentorAction(Description = "Approves a refund", RequiredRoles = ["OrderManager", "Admin"], RequiresConfirmation = false)]
    public string ApproveRuleRefund() => "approved";
}

// ── kind.one-per-class and kind.page-is-separate ───────────────────────────────────────────────────

[MentorTeam(Name = "Team Over Agent", Description = "A class marked as a team and as an agent")]
[MentorAgent(Name = "Agent Under Team", Description = "Never built: the team wins")]
public class TeamAndAgentClass
{
    [TeamMember(Role = "Reviewer", Instructions = "Review the request.")]
    public object? Reviewer { get; set; }

    [MentorAction(Description = "A method of a class that is a team")]
    public string MethodOfTeamClass() => "team";
}

[MentorAgent(Name = "Agent Over Skill", Description = "A class marked as an agent and as a skill")]
[MentorSkill(Name = "skill-under-agent", Description = "Not a skill of the coordinator's actions: the agent wins")]
public class AgentAndSkillClass
{
    [MentorAction(Description = "A method of a class that is an agent and a skill")]
    public string MethodOfAgentAndSkillClass() => "agent";
}

[MentorSkill(Name = "plain-skill-rule", Description = "A skill whose method is a coordinator action")]
public class SkillOnlyClass
{
    [MentorAction(Description = "A method of a skill")]
    public string MethodOfSkillClass() => "skill";
}

[MentorPage(Url = "/rules/page", Name = "Rule Page")]
public class PageWithActions
{
    [MentorAction(Description = "A method of a page class")]
    public string MethodOfPageClass() => "page";
}

[MentorPage(Url = "/rules/agent-page", Name = "Rule Agent Page")]
[MentorAgent(Name = "Page Agent", Description = "A page class that is also an agent")]
public class PageThatIsAnAgent
{
    [MentorAction(Description = "A method of a page that is an agent")]
    public string MethodOfPageAgent() => "page agent";
}
