namespace Novior.MentorAgent.Abstractions.Models;

/// <summary>Position of the chat widget on screen (always fixed).</summary>
public enum ChatPosition
{
    /// <summary>Bottom-right corner (default).</summary>
    BottomRight = 0,
    /// <summary>Bottom-left corner.</summary>
    BottomLeft = 1,
    /// <summary>Top-right corner.</summary>
    TopRight = 2,
    /// <summary>Top-left corner.</summary>
    TopLeft = 3,
    /// <summary>Full-height right side panel.</summary>
    SideRight = 4,
    /// <summary>Full-height left side panel.</summary>
    SideLeft = 5
}
