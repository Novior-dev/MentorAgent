namespace Novior.MentorAgent.Attributes;

/// <summary>
/// Decorates a class to register it as a MentorAgent Skill.
///
/// A skill class plays a <b>dual role</b>:
/// <list type="number">
///   <item>
///     <b>Knowledge</b> — the class (or its linked <c>SKILL.md</c>) provides domain
///     instructions: policies, rules, workflows. These are shown to the AI as a catalogue
///     entry and loaded on demand via <c>load_skill</c> (progressive disclosure).
///   </item>
///   <item>
///     <b>Tools</b> — any <c>[Description]</c> or <c>[MentorAction]</c> methods on the
///     class are automatically registered as L1 tools on the Main Coordinator.
///     The skill provides the HOW (policy + context); the methods provide the DO (execution).
///   </item>
/// </list>
///
/// ⚠️ When the class has methods, register it in DI so MentorAgent can resolve instances:
/// <code>
/// builder.Services.AddScoped&lt;ExpenseReportSkill&gt;();
/// </code>
///
/// Usage:
/// <code>
/// // Dual-role skill — knowledge + executable tools
/// [MentorSkill(
///     Name             = "expense-report",
///     Description      = "Handles expense report filing, validation, and policy checks",
///     InstructionsFile = "Skills/expense-report/SKILL.md")]
/// public class ExpenseReportSkill
/// {
///     private readonly ExpenseRepository _repo;
///     public ExpenseReportSkill(ExpenseRepository repo) => _repo = repo;
///
///     [Description("Submits a new expense report for the current user")]
///     public async Task&lt;string&gt; SubmitExpenseReportAsync(
///         string category, decimal amount, string notes) { ... }
///
///     [Description("Returns the reimbursement policy for a given expense category")]
///     public string GetPolicyForCategory(string category) { ... }
/// }
///
/// // Knowledge-only skill — no methods, just instructions / policy text
/// [MentorSkill(
///     Name        = "refund-policy",
///     Description = "Return and refund rules for the online store",
///     Instructions = "## Refund Policy\n- Items can be returned within 30 days...")]
/// public class RefundPolicySkill { }
///
/// // Auto-discovery — MentorAgent looks for Skills/{Name}/SKILL.md automatically
/// [MentorSkill(Name = "shipping-policy", Description = "Shipping and delivery policy")]
/// public class ShippingPolicySkill { }
/// </code>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class MentorSkillAttribute : Attribute
{
    /// <summary>
    /// Unique skill name in kebab-case (e.g. "expense-report", "shipping-policy").
    /// Used as the key for the <c>load_skill</c> tool call.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// One-sentence description shown to the AI in the skill catalogue.
    /// Keep it concise — this is the only text visible before the skill is loaded.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Path to the skill instructions markdown file.
    /// Can be absolute or relative to the application's content root.
    /// If null, MentorAgent looks for <c>Skills/{Name}/SKILL.md</c> automatically.
    /// </summary>
    public string? InstructionsFile { get; init; }

    /// <summary>
    /// Inline skill instructions (markdown). Takes precedence over <see cref="InstructionsFile"/>.
    /// Ideal for short skills; use file-based for rich, longer content.
    /// </summary>
    public string? Instructions { get; init; }
}
