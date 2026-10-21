# MentorAgent demo

A small expense-claims app with MentorAgent inside: a Blazor Server app on .NET 10 that compiles the library
from this repository. Its pages take the style of the MentorAgent site and come in English and Italian.

## Run it

You need the .NET 10 SDK and a key for OpenAI or Azure OpenAI.

```bash
git clone https://github.com/Novior-dev/MentorAgent.git
cd MentorAgent/demo/MentorAgentDemo
dotnet user-secrets set "Demo:ApiKey" "<your key>"
dotnet run
```

Open the address that `dotnet run` prints. The key lives only in the user secrets of the project, never in a file
of the repository. Without a key the app starts anyway: the assistant stays off, and every page says what is
missing.

### The model

| Setting | Default | Meaning |
|---|---|---|
| `Demo:Provider` | `OpenAI` | `OpenAI` or `AzureOpenAI` |
| `Demo:Model` | `gpt-4.1-mini` | The model; on Azure OpenAI, the deployment name |
| `Demo:Endpoint` | — | Azure OpenAI only: the resource's endpoint |
| `Demo:ApiKey` | — | The key, in the user secrets only |

The first three are in `appsettings.json`. Any of them can also go in the user secrets or on the command line,
for example `dotnet run -- --Demo:Model=gpt-4.1`.

## What is in it

- **Pages:** the home page, the list of expenses, the detail of one expense, a form for a new expense, the expense
  policy, [*What to try*](#what-to-try), and *Usage*, MentorAgent's dashboard of tokens and costs, for the
  Administrator only. The data is kept in memory and seeded at startup, so a restart brings the seed back.
- **Users and roles:** there is no sign-in. The selector at the top switches between three made-up users:
  Giulia Rossi (Employee), Marco Bianchi (Manager) and Sara Conti (Administrator).
- **Languages:** the EN/IT button changes the demo's pages. The widget and the assistant stay in the language of
  `MentorAgent:Language` in `appsettings.json`, `English` by default. For an Italian widget and assistant, set it
  to `Italian` there, or start with `dotnet run -- --MentorAgent:Language=Italian`. MentorAgent has one language
  for the whole application, read at startup.
- **Theme:** the button next to it switches between light and dark.

## The tour

The first time you open the assistant in a browser, a short tour shows what it can do here: six steps, in the
language of the pages. Each step after the welcome offers to open the page it is about, and three of them carry a
question you send with one tap. The last one leads to [*What to try*](#what-to-try). The **?** button in the
assistant's header replays the tour.

MentorAgent generates its own tour once for the whole application, from the pages and the tools. The demo's pages
change language for each visitor, so the demo writes its tour by hand in `Assistant/DemoTour.cs`, an
`IMentorTour` registered as a scoped service before `AddMentorAgent`, which then keeps it instead of its own.
`options.EnableOnboardingTour = true` turns the tour on. The widget's own words, such as *Next* and *Step 3 of
6*, follow `MentorAgent:Language`.

## Questions to try

Open the assistant in the corner. The home page lists the same questions, in both languages. For every feature
of the demo, open [*What to try*](#what-to-try).

| Ask | What it shows |
|---|---|
| *Which expenses are still waiting for approval? Show them in a table.* | An action that reads the app's data, and the answer as a table |
| *Show me expense 1003.* | A card with buttons |
| *Approve expense 1002.* | The confirmation banner before a change: a Manager or the Administrator confirms and it runs; an Employee is refused |
| *Ask the Auditor to check expense 1003.* | A specialist agent, the Auditor, that checks a claim |
| *How much can I spend on a hotel night?* | An answer from the expense policy, with the section it cites as a chip that opens it |
| *Highlight expense 1008 in the table.* | An action on the page, on *Expenses* |

**A receipt.** Download the made-up receipt from the home page, open *New expense*, attach the receipt in the
assistant with the paper clip and ask: *Fill in the form from this receipt.*

## What to try

The *What to try* page (`/try`, *Cosa provare* in Italian) lists the fifteen features of the demo. For each one it
says what it does, gives a question to ask with a **Copy** button (twelve of them have one), links the page where
it helps, and quotes the lines of the demo's code that turn it on, with the name of the file.

The features are in `Localization/TryCatalog.json`, in English and Italian, embedded in the assembly, so the
published app needs no extra file. A test of the library, `Novior.MentorAgent.Tests/Docs/DemoPagesTests.cs`,
checks that every quoted line is still in its file, in the same order.

## MentorAgent in your own app

The demo references the library's projects, so it always runs the code of this repository. In your own
application, reference the package instead, together with the SDK of the provider you use:

```xml
<PackageReference Include="Novior.MentorAgent" Version="1.1.0" />
<PackageReference Include="Azure.AI.OpenAI" Version="2.9.0-beta.1" />
<PackageReference Include="Microsoft.Extensions.AI.OpenAI" Version="10.6.0" />
```

Then follow the [main README](../README.md).

## License

The demo's code and style are under the Apache License 2.0, like the library. "MentorAgent" and the MentorAgent
logo are trademarks and are not licensed: see [TRADEMARKS.md](../TRADEMARKS.md) and [NOTICE](../NOTICE).
