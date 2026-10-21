using Microsoft.AspNetCore.Components;
using Novior.MentorAgent.Abstractions.Interfaces;

namespace MentorAgentDemo.Components;

/// <summary>
/// The base of the demo's routable pages. It gives a page MentorAgent's page context, through which the page tells
/// the assistant what it shows and which actions it can run.
/// </summary>
public abstract class DemoPage : DemoComponent
{
    [Inject] private IServiceProvider Services { get; set; } = default!;

    /// <summary>MentorAgent's page context; null when the assistant is off because no model is configured.</summary>
    protected IMentorPageContext? PageContext { get; private set; }

    protected override void OnInitialized()
    {
        base.OnInitialized();
        PageContext = Services.GetService<IMentorPageContext>();
    }
}
