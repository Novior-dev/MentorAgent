using MentorAgent_Helpdesk.Tickets;
using MentorAgent_Helpdesk.Identity;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.AI;
using Novior.MentorAgent.Extensions;

namespace MentorAgent_Helpdesk;

/// <summary>
/// The registrations of the helpdesk and of its assistant. <c>Program.cs</c> calls them with the configured model, the
/// tests with a scripted one: the assistant they check is the one the app runs.
/// </summary>
public static class HelpdeskSetup
{
    /// <summary>The tickets, the test sign-in, and the class whose <c>[MentorAction]</c> methods are the assistant's tools.</summary>
    public static IServiceCollection AddHelpdesk(this IServiceCollection services)
    {
        services.AddSingleton<TicketStore>();
        services.AddScoped<TestSignIn>();
        services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<TestSignIn>());

        // A class with [MentorAction] methods becomes the assistant's tools once it is in DI.
        services.AddScoped<TicketActions>();
        return services;
    }

    /// <summary>MentorAgent, with <paramref name="chatClient"/> as the model.</summary>
    public static IServiceCollection AddHelpdeskAssistant(this IServiceCollection services, IChatClient chatClient)
        => services.AddMentorAgent(options =>
        {
            options.ChatClient = chatClient;
            options.ScanAssemblies = [typeof(HelpdeskSetup).Assembly];

            options.AppName = "Helpdesk";
            options.AppDescription =
                "The support tickets of a small team: anyone can open one, the support agents assign and close them.";

            // An action that changes data runs only after the person confirms it in the widget. Roles are on the
            // actions (RequiredRoles): close_ticket is not even offered to someone without the Agent role.
            options.RequireConfirmation = true;
        });
}
