using Bunit;
using Novior.MentorAgent.Abstractions.Core;
using Novior.MentorAgent.Abstractions.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Novior.MentorAgent.Tests.Components;

/// <summary>
/// The minimum service graph a MentorAgent component needs to render.
///
/// <para>
/// Every component injects <see cref="IOptions{TOptions}"/> of <see cref="MentorWidgetOptions"/>,
/// and several also inject <see cref="MentorLocalizer"/>. A component whose dependencies are not
/// registered throws at render rather than failing an assertion, so this base class exists to keep
/// that noise out of the tests themselves.
/// </para>
///
/// <para>
/// The options are configured per-test through <see cref="Widget"/>: badges and panels read them
/// to decide whether to render at all, which makes "did it render" a genuine assertion about
/// configuration rather than about markup.
/// </para>
/// </summary>
public abstract class ComponentTestBase : BunitContext
{
    protected ComponentTestBase()
    {
        Services.AddSingleton(Widget);
        Services.AddSingleton<IOptions<MentorWidgetOptions>>(new OptionsWrapper<MentorWidgetOptions>(Widget));

        // Resolved lazily: a test sets Widget.Language before rendering, and the localizer takes
        // its culture once in the constructor. Building it eagerly here would pin every test to
        // English and quietly defeat the localisation cases.
        Services.AddSingleton(_ => new MentorLocalizer(Widget.Language));

        // Components call JS for scrolling, focus and speech. bUnit's loose mode records the calls
        // and returns default, which is what a test wants: the calls are not the subject here, and
        // strict mode would turn every unrelated interop into a failure.
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    /// <summary>Mutate this before rendering to configure the component under test.</summary>
    protected MentorWidgetOptions Widget { get; } = new();
}
