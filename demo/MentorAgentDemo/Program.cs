using MentorAgentDemo;
using MentorAgentDemo.Assistant;
using MentorAgentDemo.Components;
using MentorAgentDemo.Domain;
using MentorAgentDemo.Identity;
using MentorAgentDemo.Localization;
using Microsoft.AspNetCore.Components.Authorization;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Rag;

var builder = WebApplication.CreateBuilder(args);

// A clone has no launchSettings.json, so `dotnet run` starts in Production. There the host reads neither the user
// secrets, where the model key belongs, nor the static files of the library and of Blazor (_framework/blazor.web.js)
// until the app is published. Both are wanted when the demo runs from source.
// The environment and the command line are added again after the secrets, so they still win over them, as in the
// usual order: `dotnet run -- --Demo:Model=gpt-4.1` overrides a Demo:Model kept in the user secrets.
if (!builder.Environment.IsDevelopment())
{
    builder.Configuration.AddUserSecrets<Program>(optional: true);
    builder.Configuration.AddEnvironmentVariables();
    builder.Configuration.AddCommandLine(args);
}
builder.WebHost.UseStaticWebAssets();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// The demo's own services: the data, the made-up users and the language of the pages.
builder.Services.AddSingleton<ExpenseStore>();
builder.Services.AddScoped<DemoAuthenticationStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<DemoAuthenticationStateProvider>());
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthorizationCore();
builder.Services.AddScoped<DemoLanguage>();

// The assistant's side of the demo. The classes with [MentorAction] methods are tools only once they are in DI, and
// a document source is MentorAgent's only when it is registered before AddMentorAgent.
var assistantLanguage = AssistantLanguage.From(builder.Configuration);
builder.Services.AddSingleton(assistantLanguage);
builder.Services.AddScoped<ExpenseActions>();
builder.Services.AddScoped<AuditorAgent>();
builder.Services.AddSingleton<PolicyHandbook>();
builder.Services.AddSingleton<IMentorRagSource>(sp => sp.GetRequiredService<PolicyHandbook>());

// The model. Without a key the app still starts: MentorAgent is not registered, and the pages say what to set.
var model = DemoModel.From(builder.Configuration);
builder.Services.AddSingleton(model);

if (model.IsConfigured)
{
    // The widget's tour, written by hand in the language of the pages. Registered before AddMentorAgent, which then
    // keeps it instead of generating its own; scoped, because it reads the visitor's DemoLanguage.
    builder.Services.AddScoped<IMentorTour, DemoTour>();

    builder.Services.AddMentorAgent(options =>
    {
        options.ChatClient = model.CreateChatClient();
        options.ScanAssemblies = [typeof(Program).Assembly];

        options.AppName = "MentorAgent Demo";
        options.AppDescription =
            "Expense claims of a small team: employees submit them, managers approve or reject them, " +
            "an administrator looks after the whole application.";

        // One language for the whole application, read here at startup. The EN/IT button of the demo changes
        // its pages only; set MentorAgent:Language to Italian in appsettings.json for an Italian assistant.
        options.Language = assistantLanguage.Language;

        // The site's colours.
        options.PrimaryColor = "#1D5BD6";
        options.BotName = "MentorAgent";

        // Answers when asked; the proactive level is not used in the demo.
        options.MentorshipLevel = MentorshipLevel.Standard;

        // A short tour on the first visit, with a question to try at each step: DemoTour.
        options.EnableOnboardingTour = true;

        // A write runs only after the person confirms it, with a banner that says what changes, in the
        // assistant's language. Roles are on the actions (RequiredRoles).
        options.RequireConfirmation = true;
        options.ConfirmationMessage = DemoConfirmation.Write;

        // Tables in the answers, and cards with buttons from show_expense.
        options.EnableRichResponses = true;
        options.EnableGenerativeCards = true;

        // The expense desk's procedures, in Skills/expense-desk/SKILL.md, loaded when the model needs them.
        options.EnableSkills = true;

        // What the assistant learns about each person, kept in memory for this run of the demo.
        options.UseMemoryContext = true;

        // A receipt as a picture: the model reads it and fills the claim.
        options.EnableImageInput = true;

        // Unsafe messages are stopped, and questions that are not about the app are declined.
        options.EnableSafetyCheck = true;
        options.RefuseOutOfScope = true;

        // The expense policy as documents, cited as [1], [2] with a chip that opens the section.
        options.UseRag = true;
        options.ShowRagSources = true;
        options.RagResultCount = PolicyHandbook.Sections.Count;

        // At most 10 messages a minute for each person.
        options.RateLimitPerUser = 10;
        options.RateLimitWindowSecs = 60;

        // OpenAI's list prices per million tokens, for the usage page.
        options.ModelPricing = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-4.1"] = new(InputPer1M: 2.00m, OutputPer1M: 8.00m),
            ["gpt-4.1-mini"] = new(InputPer1M: 0.40m, OutputPer1M: 1.60m),
        };
    });
}

var app = builder.Build();

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/error", createScopeForErrors: true);

app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
