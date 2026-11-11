using MentorAgent_Helpdesk;
using MentorAgent_Helpdesk.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthorizationCore();

// The helpdesk: the tickets, the test sign-in and the actions the assistant can run.
builder.Services.AddHelpdesk();

// The model, from the "Assistant" section of the configuration. Without a key the app still starts: the assistant
// is not registered, and the page says what to set.
var model = AssistantModel.From(builder.Configuration);
builder.Services.AddSingleton(model);
if (model.IsConfigured)
    builder.Services.AddHelpdeskAssistant(model.CreateChatClient());

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
