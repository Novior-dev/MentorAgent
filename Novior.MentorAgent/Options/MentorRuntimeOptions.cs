using System.Collections.Frozen;
using System.Reflection;

namespace Novior.MentorAgent.Options;

/// <summary>
/// When MentorAgent reads an option of <see cref="MentorOptions"/>, and so from when a change made while the
/// application runs applies.
/// </summary>
public enum MentorOptionReadAt
{
    /// <summary>
    /// Before the application starts: at registration, or once by a service that keeps the value. Set it there: a change
    /// made while the application runs is not supported.
    /// </summary>
    Startup,

    /// <summary>Again for every new session: a change made while the application runs applies to the sessions that begin after it.</summary>
    Session,

    /// <summary>Again at every turn: a change made while the application runs applies from the next turn of every session.</summary>
    Turn,
}

/// <summary>
/// The options of <see cref="MentorOptions"/> that can be changed while the application runs, and from when a change
/// applies.
/// <para>
/// The options are one object for the process, the value of <c>IOptions&lt;MentorOptions&gt;</c>. To change one while the
/// application runs, assign its property on that object, and replace a collection rather than change it in place. A
/// turn already running may still read the value from before.
/// </para>
/// <para>
/// Every option listed is a promise, with a test that changes it on a running application and sees the change apply.
/// Every other option is read before the application starts: set it there. One that is not listed may happen to work
/// when changed later; nothing promises it.
/// </para>
/// </summary>
public static class MentorRuntimeOptions
{
    /// <summary>
    /// When MentorAgent reads each option, by the name of its <see cref="MentorOptions"/> property, as the property
    /// declares it. Every property declares it: a new option cannot ship without the decision.
    /// </summary>
    internal static IReadOnlyDictionary<string, MentorOptionReadAt> Declared { get; } =
        typeof(MentorOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => (p.Name, Attribute: p.GetCustomAttribute<MentorOptionReadAtAttribute>()))
            .Where(p => p.Attribute is not null)
            .ToFrozenDictionary(p => p.Name, p => p.Attribute!.ReadAt, StringComparer.Ordinal);

    /// <summary>
    /// Every option that can be changed while the application runs, by the name of its <see cref="MentorOptions"/>
    /// property: <see cref="MentorOptionReadAt.Session"/> when a change applies to the sessions that begin after it,
    /// <see cref="MentorOptionReadAt.Turn"/> when it applies from the next turn of every session.
    /// </summary>
    public static IReadOnlyDictionary<string, MentorOptionReadAt> Changeable { get; } =
        Declared.Where(d => d.Value != MentorOptionReadAt.Startup)
            .ToFrozenDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);

    /// <summary>Whether the option named <paramref name="optionName"/>, a property of <see cref="MentorOptions"/>, can be changed while the application runs.</summary>
    /// <param name="optionName">The name of the property, as <c>nameof(MentorOptions.Language)</c> gives it.</param>
    public static bool IsChangeable(string optionName)
    {
        ArgumentNullException.ThrowIfNull(optionName);
        return Changeable.ContainsKey(optionName);
    }
}

/// <summary>
/// When MentorAgent reads the option: every property of <see cref="MentorOptions"/> carries one, and
/// <see cref="MentorRuntimeOptions.Changeable"/> is made of the ones that are not <see cref="MentorOptionReadAt.Startup"/>.
/// <see cref="MentorOptionReadAt.Session"/> and <see cref="MentorOptionReadAt.Turn"/> are promises: each has a test,
/// named by <c>[Trait("RuntimeOption", name)]</c>, that changes the option on a running application.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
internal sealed class MentorOptionReadAtAttribute(MentorOptionReadAt readAt) : Attribute
{
    public MentorOptionReadAt ReadAt { get; } = readAt;
}
