using System.Buffers;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Novior.MentorAgent.Abstractions.Models;

namespace Novior.MentorAgent.Core;

/// <summary>
/// Story 1288, slice C — the names of the UI actions the pages declared since the application started, one registration
/// per page, for <see cref="MentorActionCatalog.GetRegistrations"/>. Every page context of the process writes here
/// (<see cref="MentorPageContext"/>: a Blazor Server or MAUI Hybrid page, and on the hub the client's snapshot), so the
/// bridge builds its catalogue per page, not per session.
///
/// <para>
/// What a page declares comes from the browser: only names are kept, never a description, a parameter hint or a
/// handler, and only within <see cref="MentorPageActionLimits"/>. Nothing is ever taken out: a page that drops an
/// action keeps it here. One counter grows at every real change, and the page that changed takes its value as its
/// <see cref="MentorActionRegistration.Revision"/>, so a reader sees that something changed, and where.
/// </para>
/// </summary>
internal sealed class MentorPageActionTrace(ILogger<MentorPageActionTrace>? logger = null)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, PageTrace> _pages = new(StringComparer.Ordinal);
    private readonly List<PageTrace> _order = [];
    private long _revision;
    private bool _warnedPageName;
    private bool _warnedPageLimit;

    private sealed class PageTrace(string name)
    {
        internal string Name { get; } = name;
        internal List<string> Actions { get; } = [];
        internal HashSet<string> Known { get; } = new(StringComparer.Ordinal);
        internal List<MentorActionRejection> Rejected { get; } = [];
        internal long Revision { get; set; }
    }

    /// <summary>Writes down that the page <paramref name="pageName"/> declared the UI action <paramref name="actionName"/>.</summary>
    internal void Record(string? pageName, string? actionName)
    {
        if (actionName is null) return;

        lock (_lock)
        {
            if (!IsPageName(pageName))
            {
                // page.name-shape: there is no registration to write the rejection in. The name is not logged: it
                // comes from the browser.
                if (!_warnedPageName)
                {
                    _warnedPageName = true;
                    logger?.LogWarning(
                        "[MentorAgent] page.name-shape: a page declared UI actions without a name, or with a name the trace does not take (1 to {Max} characters, not white space only, no control or format character). Its actions are offered to the model, but not listed in IMentorActionCatalog.GetRegistrations(). This is written once.",
                        MentorPageActionLimits.MaxPageNameLength);
                }
                return;
            }

            if (!_pages.TryGetValue(pageName!, out var page))
            {
                if (_order.Count >= MentorPageActionLimits.MaxPages)
                {
                    if (!_warnedPageLimit)
                    {
                        _warnedPageLimit = true;
                        logger?.LogWarning(
                            "[MentorAgent] page.limit: the trace of the pages' UI actions already lists {Max} pages; the pages that come after are not listed in IMentorActionCatalog.GetRegistrations(). Their actions are still offered to the model. This is written once.",
                            MentorPageActionLimits.MaxPages);
                    }
                    return;
                }

                page = new PageTrace(pageName!);
                _pages[page.Name] = page;
                _order.Add(page);
            }

            if (page.Known.Contains(actionName)) return;

            if (!IsActionName(actionName))
                Reject(page, actionName, "page.name-shape");
            else if (page.Actions.Count >= MentorPageActionLimits.MaxActionsPerPage)
                Reject(page, actionName, "page.limit");
            else
            {
                page.Known.Add(actionName);
                page.Actions.Add(actionName);
                page.Revision = ++_revision;
            }
        }
    }

    /// <summary>One registration per page, in the order the pages were first seen.</summary>
    internal IReadOnlyList<MentorActionRegistration> Registrations()
    {
        lock (_lock)
        {
            return [.. _order.Select(p => new MentorActionRegistration
            {
                Id       = MentorActionOrigins.Page + ":" + p.Name,
                Origin   = MentorActionOrigins.Page,
                Source   = p.Name,
                Page     = p.Name,
                Actions  = [.. p.Actions],
                Rejected = [.. p.Rejected],
                Revision = p.Revision,
            })];
        }
    }

    private void Reject(PageTrace page, string actionName, string rule)
    {
        if (page.Rejected.Count >= MentorPageActionLimits.MaxRejectedPerPage) return;

        var name = Printable(actionName, MentorPageActionLimits.MaxActionNameLength);
        if (page.Rejected.Any(r => r.Rule == rule && string.Equals(r.Name, name, StringComparison.Ordinal))) return;

        page.Rejected.Add(new MentorActionRejection { Name = name, Rule = rule });
        page.Revision = ++_revision;
    }

    /// <summary>page.name-shape: <c>^[A-Za-z0-9_-]{1,64}$</c>, a model's tool name.</summary>
    internal static bool IsActionName(string name)
    {
        if (name.Length is 0 or > MentorPageActionLimits.MaxActionNameLength) return false;
        foreach (var c in name)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) return false;
        return true;
    }

    /// <summary>page.name-shape: 1 to 128 characters, not white space only, no code point of Unicode category C.</summary>
    internal static bool IsPageName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MentorPageActionLimits.MaxPageNameLength) return false;

        for (var i = 0; i < name.Length;)
        {
            if (Rune.DecodeFromUtf16(name.AsSpan(i), out var rune, out var used) != OperationStatus.Done
                || IsOther(Rune.GetUnicodeCategory(rune)))
                return false;
            i += used;
        }

        return true;
    }

    /// <summary>A rejected name as the trace writes it: cut to <paramref name="max"/>, a code point of category C as U+FFFD.</summary>
    private static string Printable(string name, int max)
    {
        var text = new StringBuilder(Math.Min(name.Length, max));
        for (var i = 0; i < name.Length && text.Length < max;)
        {
            if (Rune.DecodeFromUtf16(name.AsSpan(i), out var rune, out var used) != OperationStatus.Done
                || IsOther(Rune.GetUnicodeCategory(rune)))
            {
                text.Append('�');
                i += Math.Max(used, 1);
                continue;
            }

            if (text.Length + rune.Utf16SequenceLength > max) break;
            text.Append(rune.ToString());
            i += used;
        }

        return text.ToString();
    }

    private static bool IsOther(UnicodeCategory category) => category is UnicodeCategory.Control or UnicodeCategory.Format
        or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned;
}
