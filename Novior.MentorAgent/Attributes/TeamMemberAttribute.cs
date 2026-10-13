/// <summary>
/// Decorates a property to define a Group Chat team member.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class TeamMemberAttribute : Attribute
{
    /// <summary>The member's role within the team.</summary>
    public required string Role { get; init; }

    /// <summary>Instructions (system prompt) for this team member.</summary>
    public required string Instructions { get; init; }

    /// <summary>
    /// Tool classes this member can use during the discussion.
    /// Use READ-ONLY tools — execution is delegated to L2 agents via HandoffTo.
    ///
    /// Example: Tools = [typeof(ReportTools), typeof(OrderTools)]
    /// </summary>
    public Type[] Tools { get; init; } = [];
}

// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Decorates the Group Chat termination method.
/// Required signature: bool ShouldTerminate(string lastMessage, string lastSpeaker)
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class TeamTerminationConditionAttribute : Attribute;