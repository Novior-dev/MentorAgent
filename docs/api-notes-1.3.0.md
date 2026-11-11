# Novior.MentorAgent 1.3.0 — API notes for the release candidate

These notes are for the teams that test the 1.3.0 candidate before it is published. They cover three things the
release promises: the rules that decide which methods become actions, how YAML agents are loaded while the
application runs, and which options may be changed while it runs. Each one points to where the promise is written
and to what enforces it, rather than repeating it.

**The candidate rule.** Between the candidate and the release only the version changes: the same commit of code, the
same release notes. The candidate is built as `1.3.0` from the final commit, the one with the `[1.3.0]` section of
the CHANGELOG, from a real checkout of that commit; the release publishes the same code. The only differences
allowed are the commit hash in `ProductVersion` and the repository address in the nuspec and in SourceLink
(the candidate points to the private repository, the release to GitHub), plus the SBOM and the static web asset
manifests, which every build rewrites.

## 1. Which methods become actions — `ACTION-RULES.md`

The rules are in [`ACTION-RULES.md`](../ACTION-RULES.md), at the
root of the repository: 39 rules in 9 areas, each with a stable name such as `confirm.predicate-adds`.

| Area | What it decides |
|---|---|
| `scan` | Which methods become actions |
| `kind` | The kind of class: coordinator, specialist, skill, team |
| `name` | The names the model sees |
| `enabled` | Whether an action is on |
| `confirm` | When the user is asked to confirm |
| `roles` | Who may run it |
| `describe` | The description the model reads |
| `declarative` | The actions a definition file gives its agent |
| `surface` | What is published on `/mcp` and on the A2A card |

The file also explains how confirmations and roles add up.

**What is promised:**
- every rule has a stable name and at least one test that fails if the library stops following it;
- a rule changes only with a CHANGELOG entry, and the table says in which version;
- a name is never reused: a rule that goes away stays in the table, marked «retired in x.y.z».

**What enforces it.** Each rule's tests carry `[Trait("Rule", "<name>")]`. The library's build fails when a rule in
force has no test with its name, when a test names a rule that does not exist or is retired, when a name is
duplicated or badly formed, or when a rule added, changed or retired in the version being built is missing from
that version's release notes.

**New in 1.3.0:** the whole file. The rules `enabled.external-decides`, `confirm.external-decides`,
`roles.external-replace`, `describe.external-replace` and `surface.governor-session` are about
`IMentorActionGovernor`; the `declarative.*` rules come with the agent-source report (section 2).

## 2. YAML agents while the application runs

**When a source is asked.** An `IMentorAgentSource` is asked every time a coordinator is built: once per Blazor
circuit, MAUI scope or hub connection, on every `/mentor/chat` request and every A2A task, and once at start-up with
`WarmUpAtStartup`. For `Novior.MentorAgent.Declarative` this means the definitions are read again for every new
scope: an edited file reaches the sessions that start after it, without a restart, and the sessions already open keep
the agents they started with. The package README of `Novior.MentorAgent.Declarative` says so too.

**API (additions only):**

| Member | What it does |
|---|---|
| `IMentorAgentSource.LoadAsync(MentorAgentSourceContext, CancellationToken)` | Returns a `MentorAgentSourceResult`: the agents, and a `MentorAgentRejection(Name, ReasonCode, Detail)` for each definition left out or tool removed. Its default body returns the agents of `GetAgentsAsync`, so a source written before works as it is |
| `MentorAgentSourceContext.SessionId` | The session's id, the same one the action governor and the action observers receive |
| `IMentorAgentSourceObserver`, `AddMentorAgentSourceObserver<T>()` | Receives a `MentorAgentSourceReport` per source every time, also when the source loaded nothing. An observer that throws stops nothing |

**Reason codes** (`MentorAgentRejectionReasons`, stable): `invalid-definition`, `not-an-agent`, `foreign-tools`,
`invalid-configuration`, `source-failed` (a source that throws is reported with the exception's type only),
`tool-not-found`, `hosted-tool-removed`.

**What `Novior.MentorAgent.Declarative` does with them:**
- it reports every definition that does not load and every tool it removes;
- a `kind: function` entry that names no action of the session (unknown, or off for that session) is removed with a
  warning and reported as `tool-not-found`, and the agent loads without it;
- `webSearch`, `codeInterpreter`, `fileSearch` and `mcp` entries are removed and reported as `hosted-tool-removed`:
  a definition chooses only among the tools the application has registered, which are already behind the gate.

These are the rules `declarative.functions-match` and `declarative.no-hosted-tools` of `ACTION-RULES.md`.

## 3. Options that may change while the application runs

**The source of truth** is `MentorRuntimeOptions.Changeable` (`IReadOnlyDictionary<string, MentorOptionReadAt>`)
and `MentorRuntimeOptions.IsChangeable(string optionName)`. `MentorOptionReadAt` has three values:

| Value | Meaning |
|---|---|
| `Startup` | Changing it while the application runs is not supported |
| `Session` | Applies to the sessions that begin after the change |
| `Turn` | Applies from the next turn of every session |

**How to change one.** The options are one object per process, the value of `IOptions<MentorOptions>`. Assign the
property on that object; replace a collection rather than modifying it. A turn already running may still read the
old value.

**What enforces it.** Every `MentorOptions` property declares when it is read, and a new option cannot ship without
that decision. `Session` and `Turn` are promises: each has a test, marked `[Trait("RuntimeOption", "<name>")]`, that
changes the option on a running application. The package README's section «Changing options while the application
runs» is checked against the same list.

**As of 1.3.0, 16 options** (every other one is `Startup`):

| Read at | Options |
|---|---|
| `Turn` (10) | `Language`, `EnableGenerativeCards`, `ConfirmationMessage`, `ConfirmationTimeout`, `ActionPolicyTimeout`, `CheckAnnouncedActions`, `EnableActionFeedback`, `EnableSafetyCheck`, `RateLimitPerUser`, `RateLimitWindowSecs` |
| `Session` (6) | `MentorshipLevel`, `ProactiveNextSteps`, `EnableRichResponses`, `RefuseOutOfScope`, `MaxSessionMessages`, `ToolFilterMaxTools` |

The options added later in 1.3.0, `CoordinatorSampling` and `WarmUpChatModel` among them, are all `Startup`. When
this list and `MentorRuntimeOptions.Changeable` disagree, the code is right.
