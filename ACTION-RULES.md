# Action rules

These rules say how MentorAgent decides which methods of your application become actions, what they are
called, and how roles and confirmations add up. They describe what the library does today.

- Every rule has a stable name and at least one test that fails if the library stops following it.
- A rule changes only with a note in the [CHANGELOG](CHANGELOG.md), and the table says in which version
  it changed.
- A name is never reused. A rule that is dropped stays in the table, marked "retired in x.y.z".

## Areas

The first part of each name is its area:

- `scan`: which methods become actions;
- `kind`: the kind of class they belong to (the coordinator's actions, a specialist, a skill, a team);
- `name`: the names the model sees;
- `enabled`: whether an action is on: shipped off in the code, turned on or off from outside it;
- `confirm`: when an action asks the user to confirm;
- `roles`: who may run an action;
- `describe`: the description the model reads for an action;
- `declarative`: the actions a definition file (`Novior.MentorAgent.Declarative`) gives its agent;
- `surface`: what is published on `/mcp` and on the A2A card.

## How confirmations and roles add up

- A confirmation is asked only when `RequireConfirmation` is on and at least one source wants it: what
  the action declares, the default for writes, or `RequiresApproval`. With a governor, its answer replaces these
  sources (`confirm.external-decides`).
- Roles and confirmation both apply, and roles come first: a user without the role is refused before
  anyone is asked to confirm.

## The rules

| Rule | What it says | Since | Changed in |
|---|---|---|---|
| `scan.assemblies-once` | Each assembly in `ScanAssemblies` is read once. An assembly listed twice is read once, with a warning that names it. | 1.3.0 | |
| `scan.concrete-classes` | Only classes that are not abstract are read. Abstract classes, structs and interfaces give no action. | 1.3.0 | |
| `scan.public-instance-declared` | Only public instance methods declared on the class itself become actions. Static, internal, protected and private methods do not, and a method the class inherits is an action of its base class only. | 1.3.0 | |
| `scan.marker` | A method becomes an action with `[MentorAction]`, whatever its `Description`, or with a `[Description]` whose text is not blank. With neither, it is not an action. | 1.3.0 | |
| `scan.mentor-action-wins` | On a method with both `[MentorAction]` and `[Description]`, `[MentorAction]` decides: its description, its confirmation and its roles. | 1.3.0 | |
| `scan.override-keeps-action` | An override of an action is an action of the class that overrides it, with what the base method declares: `[MentorAction]` and `[Description]` both pass to the override. Class attributes (`[MentorAgent]`, `[MentorTeam]`, `[MentorPage]`) do not pass to a derived class. | 1.3.0 | |
| `kind.one-per-class` | A class is of one kind only, in this order: `[MentorTeam]`, then `[MentorAgent]`, then `[MentorSkill]`, then a plain class whose methods are the coordinator's actions. | 1.3.0 | |
| `kind.page-is-separate` | `[MentorPage]` records the page and does not change the class's kind. Its methods follow the other rules. | 1.3.0 | |
| `kind.agent-owns-methods` | The methods of a `[MentorAgent]` class are that specialist's own actions, not the coordinator's. | 1.3.0 | |
| `kind.skill-methods-level1` | The methods of a `[MentorSkill]` class are the coordinator's actions, registered once. A skill with no methods gives none. | 1.3.0 | |
| `kind.team-needs-members` | A `[MentorTeam]` class with no `[TeamMember]` property is left out, with a warning. | 1.3.0 | |
| `kind.member-tools-leave-level1` | A class listed in a `[TeamMember(Tools = …)]` is that member's toolbox. Its methods are not the coordinator's actions, and they are not on `/mcp` or the A2A card. A `[MentorSkill]` class keeps its methods as the coordinator's. | 1.3.0 | |
| `kind.member-tools-same-rules` | A member's tool classes follow the `scan.*` rules, and each tool keeps what its attribute declares. | 1.3.0 | |
| `name.snake-case` | An action is named after its method. A trailing `Async` (exact case) is dropped, then the name becomes snake_case: every capital letter starts a new word (`GetHTTPStatus` becomes `get_h_t_t_p_status`), spaces and dashes become underscores, and digits stay. | 1.3.0 | |
| `name.team-tool` | A team is offered to the coordinator as one tool, named after the team's `Name` by the same rule (`Business Analysis Team` becomes `business_analysis_team`). | 1.3.0 | |
| `name.shared-warned` | Two methods with the same action name, compared without regard to case, are named in a warning at startup. One method found twice is not two. | 1.3.0 | |
| `name.called-method-decides` | When methods share a name, the roles, the confirmation, its text and the page to open are those of the method actually called (BUG-188). | 1.3.0 | |
| `enabled.off-by-default` | An action declared `[MentorAction(Enabled = false)]`, or one MentorAgent registers off, is in the catalogue (`EnabledByDefault` false) and nowhere else. It is offered to no agent, described to no model, not listed on `/mcp` and not on the A2A card, and a call to it does not run (`Denied`, `disabled`), until an `IMentorActionGovernor` turns it on. | 1.3.0 | |
| `enabled.external-decides` | With an `IMentorActionGovernor` registered, its answer turns an action on or off, and `null` leaves the code's word. It is asked once as a session builds its tools (the coordinator's, the specialists', the team members'), on every `tools/list` of `/mcp`, and before every call, ahead of the roles. A session keeps the tools it was built with, and a call to an action turned off meanwhile is refused. A governor that throws, or does not answer within `ActionPolicyTimeout`, stops the action (`PolicyFailed`, `governor-failed`). There can be one: a second registration throws. | 1.3.0 | |
| `confirm.declared-decides` | An action that writes `RequiresConfirmation`, `true` or `false`, gets what it declares. | 1.3.0 | |
| `confirm.default-for-writes` | A `[MentorAction]` that does not write `RequiresConfirmation` asks for confirmation when `RequireConfirmationForWrites` is on (the default), unless it only reads: `ReadOnly = true`, or `IsReadOnlyAction` says so. | 1.3.0 | |
| `confirm.description-only-not-defaulted` | A method marked with `[Description]` alone never asks for confirmation by default. The startup warning lists it as not covered. | 1.3.0 | |
| `confirm.predicate-adds` | When `RequiresApproval` returns true for an action's name, the action asks for confirmation; the predicate is asked again at every call. Returning false never takes away a confirmation the action declares; the governor can (`confirm.external-decides`), and it can take away the predicate's too. | 1.3.0 | |
| `confirm.master-switch` | With `RequireConfirmation` off, no action of the application asks for confirmation, whatever it declares. The approval a hosted MCP server asks for comes from the provider and is still shown (BUG-137). | 1.3.0 | |
| `confirm.beats-read-only` | An action that asks for confirmation is not read-only, whatever `ReadOnly` or `IsReadOnlyAction` say. | 1.3.0 | |
| `confirm.unconfirmed-writes-warned` | At startup a warning names the writes that run without confirmation, in two lists: the `[MentorAction]` methods that opt out, and the methods marked with `[Description]` alone. | 1.3.0 | |
| `confirm.external-decides` | With an `IMentorActionGovernor` registered, its answer decides whether a call is confirmed, in place of the code's: `Required` asks even if the code does not, `None` does not ask even if the code does (declared, by default for writes, or by `RequiresApproval`), and `Code` leaves the code's word. Narrowing takes effect at once: a confirmation added counts from the call, as an action turned off does. Widening counts from the next session: a confirmation taken away, as an action turned on. A call is confirmed when the session's answer or the call's asks for it. In Native mode no auto-approval rule lets through a confirmation the governor requires: those rules are the code's. It does not take away an `IMentorActionPolicy`'s `Confirm` nor a hosted MCP server's approval, and with `RequireConfirmation` off nobody is asked. An action whose confirmation is taken away still counts as one that changes data. The decisions say when the governor changed whether a call is confirmed (`ConfirmationFromOutside`). | 1.3.0 | |
| `roles.any-of` | An action with `RequiredRoles` runs for a user who holds any one of them, checked with `ClaimsPrincipal.IsInRole` (role names are case-sensitive). With no roles there is no check. | 1.3.0 | |
| `roles.signed-in` | An action with `RequiredRoles` needs a signed-in user. With no `AuthenticationStateProvider`, or nobody signed in, it is refused. | 1.3.0 | |
| `roles.checked-before-confirmation` | Roles and confirmation both apply. Roles are checked first, so a user without the role is never asked to confirm (BUG-063), and checked again when the confirmation comes back. Turning confirmations off does not turn the role check off. With a governor, the roles checked are the ones it gives (`roles.external-replace`). | 1.3.0 | |
| `roles.offered-when-allowed` | The model is offered only the actions the user's roles allow, at every level. An action left out is still refused if it is called. | 1.3.0 | |
| `roles.external-replace` | With an `IMentorActionGovernor` registered, the roles it gives an action replace the code's. `null` leaves the code's, and an empty list requires none. They decide the offer (once, as a session builds its tools, at every level) and every call, checked against the governor's answer at the call, before the confirmation and again after it. On `/mcp` they decide which callers see and call the action, by the rule of the code's roles: an action withheld only for its roles is published once the governor requires none. The decisions say so (`RolesFromOutside`). | 1.3.0 | |
| `describe.external-replace` | With an `IMentorActionGovernor` registered, the description it gives an action replaces the code's in what a model reads: the tools of the coordinator, the specialists and the team members, the texts tool filtering ranks, and the MCP server's `tools/list`. `null` or a blank text leaves the code's. The action's name and argument schema never change, and the code's `ProactiveHint` still follows the description. It is asked once as a session builds its tools, and on every `tools/list`, never at a call. What the user sees keeps the code's description: the confirmation banner, the bar of the running action and the decisions' `DisplayName`. On a call to `/mcp` the policies are asked with the code's description. The A2A card, `IMentorActionCatalog` and the onboarding tour follow the code. | 1.3.0 | |
| `declarative.functions-match` | A `kind: function` entry of a definition reaches its agent only if the session has an action of that name. A name the application does not have, or an action that is off for the session, is taken away with a Warning and reported as a tool left out (`tool-not-found`), and the agent loads without it: an action that is off is described to no model through a definition that names it. | 1.3.0 | |
| `declarative.no-hosted-tools` | A definition cannot add a tool of its own. `webSearch`, `codeInterpreter`, `fileSearch` and `mcp` entries are taken away with a Warning and reported (`hosted-tool-removed`), and the agent loads with the application's own tools. If they cannot be taken away, the agent is not loaded (`foreign-tools`). | 1.3.0 | |
| `surface.coordinator-only` | `/mcp` and the A2A card publish only the coordinator's actions, never a specialist's or a team member's. | 1.3.0 | |
| `surface.mcp-withholds-gated` | `/mcp` does not publish an action that asks for confirmation, whether it is declared, by default or by `RequiresApproval`. With a governor, an action that asks for confirmation is published once the governor takes the confirmation away, and one it requires a confirmation for is withheld (`confirm.external-decides`). An action with roles is published only with `McpCallerPrincipal`, to the callers who hold one of its roles. An action that is off is not published, unless the governor turns it on for the request (`enabled.external-decides`). | 1.3.0 | |
| `surface.card-ungated-only` | The A2A card lists only the coordinator's actions that ask for no confirmation (declared, by default or by `RequiresApproval`), need no role, and whose class the application's container can create (BUG-177). A name that two actions share is left off when either of them is gated. The card follows the code only: an action shipped off is not on it, and one the governor turns on is not added. | 1.3.0 | |
| `surface.governor-session` | The governor is asked with the session's id (`MentorActionRequest.SessionId`) on every surface, the same id the gate's decisions carry. On `/mcp` it is a fingerprint of the MCP session, never the id. | 1.3.0 | |
