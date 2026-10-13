using System;
using System.Collections.Generic;
using System.Text;

namespace Novior.MentorAgent.Data
{

    /// <summary>
    /// A single memory entry persisted for a user.
    /// </summary>
    public sealed record MemoryEntry(
        string UserId,
        string Key,
        string Value,
        DateTime SavedAt
    );
}
