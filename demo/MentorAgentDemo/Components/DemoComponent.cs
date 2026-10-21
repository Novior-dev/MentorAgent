using System.Globalization;
using MentorAgentDemo.Identity;
using MentorAgentDemo.Localization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace MentorAgentDemo.Components;

/// <summary>
/// The base of the demo's layout and pages: it renders them again when the visitor switches the language of the
/// pages or the role. A page has no parameter that changes with either, so without this it would keep the old text.
/// </summary>
public abstract class DemoComponent : ComponentBase, IDisposable
{
    [Inject] protected DemoLanguage L { get; set; } = default!;
    [Inject] protected DemoAuthenticationStateProvider Users { get; set; } = default!;

    protected bool Italian => L.Italian;

    protected DemoUser CurrentUser => Users.Current;

    /// <summary>The culture for amounts and dates: Italian, or English with the euro.</summary>
    protected CultureInfo Culture => CultureInfo.GetCultureInfo(Italian ? "it-IT" : "en-IE");

    protected string Money(decimal amount) => amount.ToString("C", Culture);

    protected string Day(DateOnly date) => date.ToString("d MMM yyyy", Culture);

    protected override void OnInitialized()
    {
        L.Changed += Refresh;
        Users.AuthenticationStateChanged += OnUserChanged;
    }

    private void Refresh() => _ = InvokeAsync(StateHasChanged);

    private void OnUserChanged(Task<AuthenticationState> _) => Refresh();

    public virtual void Dispose()
    {
        L.Changed -= Refresh;
        Users.AuthenticationStateChanged -= OnUserChanged;
        GC.SuppressFinalize(this);
    }
}
