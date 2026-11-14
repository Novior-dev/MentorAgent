# Changelog

All notable changes to the MentorAgent packages are documented in this file: Novior.MentorAgent,
Novior.MentorAgent.Abstractions, Novior.MentorAgent.Blazor, Novior.MentorAgent.Server and
Novior.MentorAgent.Declarative. The five packages are released together and share one version number.

The format is based on [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning 2.0.0](https://semver.org/spec/v2.0.0.html). A bullet that concerns one
package only starts with that package's name. `BUG-nnn` identifiers are stable references to the
maintainers' defect log; the S1–S4 tag next to one is its severity there (S1: data loss, security
hole or crash; S2: a documented feature does not work, or following the documentation produces a broken
result; S3: wrong behaviour in an edge case, or documentation that contradicts the code; S4: cosmetic). Besides the standard sections, **Known issues** lists defects still open in that
version.

MentorAgent succeeds the [BlazorMentor](https://www.nuget.org/packages/BlazorMentor) package, published from
0.1.0-preview.1 (2026-05-17) to 0.5.0-preview (2026-06-01), deprecated on nuget.org in favour of MentorAgent
and not covered here; 1.0.0-preview is the first release under the MentorAgent name. Up to 1.0.0-rc.15 the
packages were published as MentorAgent, MentorAgent.Abstractions, MentorAgent.Blazor, MentorAgent.Server and
MentorAgent.Declarative; from 1.0.0 their IDs start with `Novior.`, and the sections below 1.0.0 use the
names of their time.

<!--
  This file is the source of every package's release notes: at pack time the section for the package's
  version becomes its <releaseNotes>, falling back to [Unreleased] while a version is being prepared
  (Directory.Build.targets). At release: rename [Unreleased] to "[x.y.z] - yyyy-MM-dd", add a new empty
  [Unreleased] above it, add the link reference at the bottom, then commit, tag and build.
-->

## [Unreleased]

## [1.3.1] - 2026-11-14

### Security

- **Novior.MentorAgent:** a user could run an action an `IMentorActionPolicy` did not offer them (BUG-233, S1),
  in 1.2.0 and 1.3.0. The policies were asked at `MentorActionStage.Offer` only about the coordinator's actions, so
  a specialist, a team member and an agent of an `IMentorAgentSource` were offered such an action and could call it;
  and a call to an action left out of the request — the coordinator's on Path A, whose function-invoking client still
  holds it — was decided by the policies' answer at `MentorActionStage.Call` alone. A policy that decides whether the
  user may use an action only at the offer, and looks at the record alone at the call, as the README's example did,
  let it run. The policies now decide the offer of every agent's tools, asked once a turn at the agent's first
  request with its `AgentKind` and `AgentName`; a call to an action they did not offer that agent in the turn is
  refused as `Denied`, `not-offered`, without asking them about the call and before any banner; the README's example
  asks the coarse question at the call too. Roles and the governor were not affected. Without this version, answer
  at `MentorActionStage.Call` the question you answer at `MentorActionStage.Offer`.

### Changed

- **Novior.MentorAgent:** with an `IMentorActionPolicy` registered, specialists, team members and the agents of an
  `IMentorAgentSource` are offered fewer tools: those the policies offer the user, as the coordinator is. A policy is
  asked at `MentorActionStage.Offer` with `AgentKind` `Specialist`, `TeamMember` or `AgentSource` as well, once a turn
  for each tool of an agent that works in the turn, and no longer at `MentorActionStage.Call` for a call to an action
  it did not offer in the turn. `IMentorActionObserver` sees that call as `Denied`, `not-offered`, not
  `not-authorized`. No public API changes.
- The support policy: only the latest minor version gets fixes, in grouped patch versions built from its release
  branch, and a security fix ships as soon as it is ready; every fix of a patch is also in the next minor version.
  Earlier minor versions get no more fixes, security fixes included: upgrade to the latest minor version, which is
  always compatible with the earlier ones. The security fixes promised for 1.0.x, 1.1.x and 1.2.x are withdrawn; a
  new major version will state its support window when it is released. See *Versioning and support* in the README
  and SECURITY.md.

## [1.3.0] - 2026-11-05

### Added

- ACTION-RULES.md lists the rules by which a method becomes an action, how actions are named, and how roles and
  confirmations add up. Each rule has a name that does not change and at least one test that fails when the rule does;
  a rule that comes in, changes or is retired is named in the release notes of its version. The rules of 1.3.0:
  `scan.assemblies-once`, `scan.concrete-classes`, `scan.public-instance-declared`, `scan.marker`,
  `scan.mentor-action-wins`, `scan.override-keeps-action`, `kind.one-per-class`, `kind.page-is-separate`,
  `kind.agent-owns-methods`, `kind.skill-methods-level1`, `kind.team-needs-members`, `kind.member-tools-leave-level1`,
  `kind.member-tools-same-rules`, `name.snake-case`, `name.team-tool`, `name.shared-warned`,
  `name.called-method-decides`, `confirm.declared-decides`, `confirm.default-for-writes`,
  `confirm.description-only-not-defaulted`, `confirm.predicate-adds`, `confirm.master-switch`,
  `confirm.beats-read-only`, `confirm.unconfirmed-writes-warned`, `roles.any-of`, `roles.signed-in`,
  `roles.checked-before-confirmation`, `roles.offered-when-allowed`, `surface.coordinator-only`,
  `surface.mcp-withholds-gated` and `surface.card-ungated-only`.

- An action can ship off, and the application can turn its actions on and off from outside its code.
  `[MentorAction(Enabled = false)]` keeps an action in the catalogue (`MentorActionInfo.EnabledByDefault` is false)
  and nowhere else: no agent is offered it, no model reads about it, the MCP server does not list it, the A2A card
  does not show it, and a call to it does not run. An `IMentorActionGovernor`, registered with
  `AddMentorActionGovernor<T>()`, answers for each action whether it is on, and can turn on one the code ships off:
  registering one gives whoever implements it the power to widen what the code allows. It is asked once as a session
  builds its tools, on every `tools/list` of the MCP server and before every call, with the session's id; a session
  keeps the tools it was built with, and a call to an action turned off meanwhile is refused. There can be one: a
  second registration throws. A governor that throws, or does not answer within `ActionPolicyTimeout`, stops the
  action. The A2A card follows the code only, so an action turned on from outside is not on it. New reason codes:
  `disabled` (`Denied`) and `governor-failed` (`PolicyFailed`). New rules: `enabled.off-by-default`,
  `enabled.external-decides` and `surface.governor-session`; `surface.mcp-withholds-gated` and
  `surface.card-ungated-only` now also leave out an action that is off.

- The governor can also give an action its roles: `IMentorActionGovernor.GetRequiredRolesAsync` answers in place of
  `[MentorAction(RequiredRoles = ...)]`, with `null` for the code's roles and an empty list for none. The answer
  decides whom the action is offered to, the check at every call, before a confirmation and again after it, and which
  MCP callers see and call it: an action the MCP server withheld only for its roles is published once the governor
  requires none. `MentorActionDecision.RolesFromOutside` says when the roles came from the governor. New rule:
  `roles.external-replace`; `roles.checked-before-confirmation` now checks the governor's roles when it gives them.

- The governor can also decide whether the user confirms an action: `IMentorActionGovernor.GetConfirmationAsync`
  answers `MentorConfirmationChoice.Code` for the code's confirmation, `Required` to ask even if the code does not,
  and `None` not to ask even if the code does — declared, by default for writes, or by `RequiresApproval`. A
  confirmation added counts from the call, one taken away from the next session, and in Native mode no auto-approval
  rule lets a required one through. A policy's `Confirm` still asks, a hosted MCP server's approval still comes from
  its provider, and with `RequireConfirmation` off nobody is asked. An action whose confirmation is taken away still
  counts as one that changes data. The MCP server publishes an action that asks for confirmation once the governor
  takes it away, and withholds one it requires. `MentorActionDecision.ConfirmationFromOutside` says when the governor
  changed whether a call is confirmed. New rule: `confirm.external-decides`; `confirm.predicate-adds` and
  `surface.mcp-withholds-gated` now say what the governor can take away.

- The governor can also rewrite the description the model reads: `IMentorActionGovernor.GetDescriptionAsync` answers
  in place of the code's, with `null` or a blank text for the code's. It is asked as a session builds its tools and
  on every `tools/list` of the MCP server, and the answer is what the coordinator, the specialists and the team
  members read, what tool filtering ranks, and what `tools/list` shows; the action's name and argument schema never
  change, and the code's `ProactiveHint` still follows the description. What the user sees keeps the code's
  description: the confirmation banner, the bar of the running action and `MentorActionDecision.DisplayName`. On a
  call to the MCP server the policies are asked with the code's description. New rule: `describe.external-replace`.

- An `IMentorAgentSource` says what it loaded and what it left out, and when it is asked is a promise. A source is
  asked whenever a coordinator is built: at least once per session — once per Blazor circuit, MAUI app scope or hub
  connection, and at every request on `/mentor/chat` and every task over A2A — and `MentorAgentSourceContext.SessionId`
  names the session, as `MentorActionRequest.SessionId` does. MentorAgent now calls `IMentorAgentSource.LoadAsync`,
  which returns a `MentorAgentSourceResult`: the agents, and a `MentorAgentRejection` (`Name`, `ReasonCode`, `Detail`,
  `Tool`) for each definition left out or tool taken away. Its default body returns the agents of `GetAgentsAsync`, so
  an existing source works unchanged. An `IMentorAgentSourceObserver`, registered with
  `AddMentorAgentSourceObserver<T>()`, receives a `MentorAgentSourceReport` for each source every time a coordinator
  is built, also when the source loaded nothing; a source that throws is reported as `source-failed`, with the
  exception's type and never its message. The reason codes, in `MentorAgentRejectionReasons`: `invalid-definition`,
  `not-an-agent`, `foreign-tools`, `invalid-configuration`, `source-failed`, `tool-not-found` and
  `hosted-tool-removed`. **Novior.MentorAgent.Declarative** reports every definition it does not load and every tool
  it takes away. New rules: `declarative.functions-match` and `declarative.no-hosted-tools`.

- `MentorRuntimeOptions.Changeable` lists the options that can be changed while the application runs, and from when
  a change applies (`MentorOptionReadAt`). From the next turn of every session: `Language`, `RateLimitPerUser`,
  `RateLimitWindowSecs`, `ConfirmationMessage`, `ConfirmationTimeout`, `ActionPolicyTimeout`, `EnableActionFeedback`,
  `EnableGenerativeCards`, `EnableSafetyCheck` and `CheckAnnouncedActions`. From the sessions that begin after the
  change: `MentorshipLevel`,
  `ProactiveNextSteps`, `EnableRichResponses`, `RefuseOutOfScope`, `ToolFilterMaxTools` and `MaxSessionMessages`. To
  change one, assign it on the `IOptions<MentorOptions>` value, replacing a collection rather than changing it in
  place. Every other option is read before the application starts: set it there. Each option listed has a test that
  changes it on a running application, and every option declares when it is read, so a new one cannot ship without
  the decision.

- After a turn in which the coordinator called no tool, MentorAgent asks a model whether its reply announces an action
  ("I'm registering the note…") or reports one as done when nothing ran (`MentorOptions.CheckAnnouncedActions`, on by
  default). If it does, the coordinator gets one more request in the same turn, with the same tools, told to call the
  tool now or to say that it was not done. The question names the tools the model was sent, and the answer names the
  one that does the action: the second request then says that this tool is there to call now. The instruction is that
  request's last message, read after the reply it is about, and never stored. A call to an action of the application
  the agent was not given (one that is off for the session) runs nothing, and the turn is checked as one in which
  nothing ran. The call is never required and passes the gate as any other, the reply
  already shown is not held back, and the second reply is not checked again. The question goes to
  `ClassifierChatClient`, or to `ChatClient` without one: a few hundred tokens after every reply that called no tool,
  so a small deployment there is worth configuring. Its verdict is logged at Information for every reply it checks,
  with the time and the tokens it took. On Path A with the history kept by MentorAgent; not with
  `UseServiceManagedHistory` nor with a pre-built `Agent` (BUG-220).

- A call the model makes to an action of the application that the coordinator was not given in the turn — one that
  is off, or another agent's — reaches no gate and runs nothing. `IMentorActionObserver` now receives its one
  decision, `Denied` with the new reason code `not-offered`, and the call's span says the same, in Blocking and in
  Native mode, with `CheckAnnouncedActions` on or off. A call the coordinator was given keeps the decisions of its
  gate, and only those.

- One instance of the application can tell which tenant a request belongs to. `IMentorTenantResolver`, registered with
  `services.AddMentorTenantResolver<T>()` (one per application), names it from the caller, the surface and the
  request's services; `IMentorTenant.Id` reads it, in an action, a policy or the governor, and
  `IMentorTenant.GetIdAsync()` resolves it first in a scope that had no turn yet. It is asked when a turn
  starts — in a Blazor circuit, on the hub, on `/mentor/chat`, in an A2A task — and for every `tools/list` and
  `tools/call` of the MCP server, where each call reads its own. A request is never served without a tenant: a resolver
  that throws, or answers `null`, empty or white space, refuses it before the model or the action, and a turn whose
  tenant is not the one its conversation began with is refused too. Without a resolver there is one tenant, as before,
  and `IMentorTenant.Id` is `null`.

- With an `IMentorTenantResolver`, two tenants on one instance share no memory, no rate limit and no conversation.
  `MentorMemoryService.GetUserIdAsync()` — the key of the memory and of `RateLimitPerUser` — is the tenant's own
  (`tenant:<id>/<the user's key>`), the shared anonymous bucket included, and so is the MCP server's per-caller
  allowance. A session snapshot names its tenant and is restored for that tenant only: one of another tenant, one that
  names none where a resolver is registered, and one that names a tenant where none is, are refused before the
  conversation changes (a 400 on `/mentor/session`). The history MentorAgent keeps is a coordinator's own, as before; a
  single `ChatHistoryProvider` instance with a resolver registered is reported at start-up, since it serves every tenant
  — `ChatHistoryProviderFactory` runs in the conversation's scope, where `IMentorTenant` reads the tenant. Without a
  resolver every key and every snapshot is as before.

- With an `IMentorTenantResolver`, every metric names its tenant: the `mentoragent.tenant` tag is on the four counters
  (`mentoragent.turns`, `mentoragent.tool_calls`, `mentoragent.errors`, `mentoragent.guardrail_blocks`) and on the
  `mentor.turn` span, the MCP server's calls included, so the whole instance is read per tenant from OpenTelemetry. The
  token & cost dashboard and `GET /mentor/admin/metrics` show the figures of the reader's tenant only: tokens, cost,
  turns, errors, guardrail blocks and top actions. A reader whose tenant the resolver does not name sees none (a 403
  on the endpoint). `IMentorMetrics.GetSnapshot(string? tenant)` and `IMentorMetricsStore.QueryAsync(string? tenant,
  CancellationToken)` are new, with defaults that keep existing implementations working. A custom `IMentorMetrics`
  shows a tenant nothing rather than the instance's figures (a 204 on the endpoint). A store is asked for a tenant only
  when it implements the new overload. A tenant's figures are kept in RAM since the process started; the store saves
  and seeds the instance's. Without a resolver the series, the dashboard and the endpoint are as before.

- With an `IMentorTenantResolver`, retrieval and the logs know the tenant. `MentorRagRequest.TenantId` gives a RAG
  source the turn's tenant; MentorAgent filters nothing, so a source that holds several tenants' documents returns
  that tenant's. A source that implements only the `(query, maxResults)` overload never sees it, and a Warning says
  so at its first search with a tenant. Every log line of a turn, the application's own code in the turn included,
  and of a call to the MCP server is written in a log scope with one pair, `mentoragent.tenant`, the key of the
  metrics and the span; a provider that includes scopes shows it. Without a resolver nothing changes.

- `MentorOptions.RagNoResultsNotice` (off by default) tells the model when the knowledge-base search of a turn kept no
  document — none returned, or every one below `RagMinScore`: a question the documents would cover is answered "not
  found in the documents" rather than from memory, and a message that needs no document, such as a greeting or an
  action, is answered as usual. The text is MentorAgent's, in the language of the turn, in that request's
  instructions only. It is not given after a search that failed, nor without a source.

- A knowledge-base search that fails can be monitored. With `EnableObservability` it is counted on
  `mentoragent.rag_failures`, tagged with the source (`mentoragent.rag.source`, its type's full name) and the
  exception's type (`mentoragent.error.type`), never its message, and with `mentoragent.tenant` when a resolver is
  registered; a Stop during the search is not counted. Each search has a `mentor.rag.search` span, with the source,
  and the status Error when it failed. The Warning names the source: "The search of … failed — RAG context skipped.".

- `IMentorRagSource.OnConversationChangedAsync` tells a source that the session's conversation changed, so one that
  keeps state for it clears it: `MentorConversationChange.Reason` is `Reset` (New conversation), `Restored` (a saved
  conversation took its place) or `Replaced` (a restored conversation the service no longer knew was replaced,
  BUG-110), with the session's id and its tenant. The default does nothing, so an existing source works unchanged. It
  is called with `UseRag` on, on the instance the session searches with, bounded by `SafetyCheckTimeout`; a source
  that throws or does not answer in time is logged as a warning, and the conversation changes anyway.

- `MentorVectorStoreRagSource<TRecord>`, a knowledge-base source over any collection of
  `Microsoft.Extensions.VectorData` (pgvector, Qdrant, Azure AI Search, an in-memory one), registered with
  `services.AddMentorVectorStoreRag(sp => collection, new MentorVectorStoreRagOptions<TRecord> { Content = … })`.
  The options say what of a record is the document, its title, its link and its metadata, and which records a request
  may see (`Filter`, given the `MentorRagRequest` with its tenant and user). Scores are read as similarities whatever
  the collection's distance function: with `CosineDistance`, the Postgres default, the source uses 1 − distance, so
  `RagMinScore` keeps the nearest documents. The function comes from `DistanceFunction` or from the record's
  `[VectorStoreVector]` attribute; when neither names one, or it is a Euclidean, Manhattan or Hamming distance,
  registering the source fails. The message is embedded once per turn with `MentorOptions.EmbeddingGenerator` and
  shared with routing, tool filtering and memory, or with a generator of the source's own; without either, the
  collection embeds the text itself. `DefaultMinScore` is 0.5. With a tenant resolver and no `Filter`, the first
  search with a tenant is logged as a warning. `Novior.MentorAgent` now references
  `Microsoft.Extensions.VectorData.Abstractions` 10.10.0, which Agent Framework already brought.

- A provider that ignores `tool_choice: "required"` is reported. `RequireLookupForRecordQuestions` and
  `RequireActionForChangeRequests` require the first call of the turn with `ChatToolMode.RequireAny`; a provider that
  answers such a request without any call — Ollama ignores `tool_choice` — makes them do nothing, and the first time
  it does a warning says so, once per provider client: "The provider answered a request that required a tool call
  (tool_choice: required) without calling one: …". A call to a tool the request did not offer is still a call, and a
  streamed reply is looked at as it goes by, nothing held back. The README has a recipe for llama-server and vLLM.

- `CoordinatorSampling` (`MentorSamplingOptions`): the coordinator's sampling — `Temperature`, `TopP`, `TopK`,
  `MaxOutputTokens`, `FrequencyPenalty`, `PresencePenalty`, `Seed` — on every request of its turns, the forced first
  call and the follow-ups of the tool loop included. The option unset, and each value left null, is the provider's
  own; in configuration, `Mentor:CoordinatorSampling:Temperature` and so on. The classifiers call the provider directly
  and keep their own settings: those that ask for temperature 0 still do, the others keep the provider's default, as
  before. A negative temperature, a top-p outside (0, 1], or a top-k or output limit below 1 stops the application at
  startup. Stop sequences are not offered: one that also occurs in a tool call's arguments would cut the call short.

- `WarmUpChatModel`: with `WarmUpAtStartup`, the warm-up also sends the coordinator's model one minimal request, so
  the first turn does not pay for opening the connection, the provider's empty prompt cache or a local model still
  loading. Every start costs one request per client — `ChatClient`, and `StrongChatClient` when it is set — with the
  coordinator's instructions, tools and sampling, no call allowed (`ChatToolMode.None`), one short user message and at
  most 16 output tokens. It goes straight to the provider client: no history, no memory, no turn on the dashboard;
  the warm-up's log line gives its time and tokens. A request that fails is a warning, and the application carries
  on; without `WarmUpAtStartup` the option has no effect, and a startup warning says so.

- `MentorEvaluator.RunConversationsAsync`: whole conversations (`EvalConversation`, `EvalTurn`) through the mentor
  itself — the orchestrator, streaming, with its tools, filters, roles, RAG, memory and the history from one turn to
  the next — each in a session of its own. A turn can require tools (`ExpectTools`: "at turn 2 it must call X"),
  forbid others (`ForbidTools`), and set a token budget, a limit on the time to the first token and an expectation
  for the judge. Its result adds `ToolsCalled`, read from the tool gate, `TimeToFirstToken`, `TurnDuration`,
  `Conversation` and `Turn`; its tokens are those of its own session, exact while other sessions talk. **Every
  confirmation is answered no by default: an evaluation does not write data on its own.**
  `MentorEvalOptions.ApproveConfirmations` answers yes, and `PrepareSession` sets the conversation's user or page
  before its first turn. `RunAsync` is unchanged; `SystemInstructions`, `Checks` and `Evaluators` stay its own.

- `EvalTurn.ExpectRefusal`: a conversation turn can expect the mentor to refuse — `EvalRefusal.OutOfScope`
  (`RefuseOutOfScope`), `EvalRefusal.Guardrail` (the safety check, the host's `InputGuardrail`, or the model refusing
  to answer) or `EvalRefusal.Any`. That refusal, shown to the user as an error, then passes the turn and the
  conversation goes on; a turn that was answered, refused the other way or failed, fails, and its notes say which. A
  safety check that could not run, a message too long, the rate limit and a missing tenant are not refusals: they stay
  errors. Without the expectation a refusal fails the turn as before. `MentorEvalResult.Refusal` reports the refusal a
  turn showed, expected or not.

- A `dotnet new` template in the repository, `templates/mentoragent-blazor`: a Blazor Server helpdesk with a
  MentorAgent assistant and three actions — `list_open_tickets`, `assign_ticket` with a confirmation, `close_ticket`
  with a confirmation and the `Agent` role — the model from the configuration with its key in the user secrets, and a
  test sign-in with two users, one without the role. Its test project runs the assistant with a scripted model through
  `RunConversationsAsync`: a support agent who confirms closes a ticket, without a yes the ticket stays open, and
  without the role the action is not offered. It is installed from a clone of the repository, not from NuGet:
  `dotnet new install templates/mentoragent-blazor`, then `dotnet new mentoragent-blazor -n Helpdesk`.

### Changed

- An `IMentorRagSource` that answers with no list (`null`) fails at once, with a message that says a source finding
  nothing returns an empty list, instead of a `NullReferenceException` later on. The turn goes on without documents,
  as before.

- **Novior.MentorAgent.Declarative:** a `kind: function` entry that names no action of the session — unknown to the
  application, or off for the session — is taken away from the agent, with a warning, and the agent loads without it.
  The Agent Framework used to give the agent a function of that name with nothing behind it: the model could call it,
  nothing ran, and the agent stopped without a word in the log. An action that is off is therefore described to no
  model through a definition that names it either.

- Every package is now checked at pack time against the public API of 1.2.0, so 1.3.0 cannot break code built
  against 1.2.0 by accident. An intended break is listed in this file as Breaking.

- The family moves onto the Microsoft Agent Framework 1.27.0 line (`Microsoft.Agents.AI` and
  `Microsoft.Agents.AI.Workflows` 1.27.0). **Novior.MentorAgent.Declarative:** `Microsoft.Agents.AI.Declarative` moves
  to 1.27.0-rc1, on the same line. The two breaking changes of Agent Framework 1.27 are in parts MentorAgent does not
  use: `AddFoundryResponses` for hosted Foundry applications, and the redirect callbacks of `DefaultHttpRequestHandler`.

- The packages' description and tags say what MentorAgent is: AI agents in a .NET application under your rules —
  roles, confirmations, cost control. The tags of `Novior.MentorAgent`, `.Server` and `.Blazor` now include
  `ai-governance`, `governance`, `authorization`, `roles`, `approval` and `human-in-the-loop`; the `mentor` tag, which
  found only namesakes, is gone from all five packages.

### Fixed

- When a restored conversation the service no longer knew was replaced by a new one (BUG-110), the knowledge-base
  search kept the recent messages of the conversation that was gone: the first search of the new one was given the
  message of the turn that had failed. They are now cleared, as on a reset or a restore.

- The turn's vector of the message (`IMentorQueryEmbedding`) was kept on its text alone (BUG-229): a knowledge-base
  source that embeds the message with a model of its own was handed the vector of `MentorOptions.EmbeddingGenerator`,
  or routing, tool filtering and memory were handed the source's, whichever asked first in the turn. Each generator
  now gets the vector it makes, still computed once per turn, compared by reference; at most four generators are
  remembered, the oldest dropped first, so one built for every call cannot make the cache grow.

### Security

- **Novior.MentorAgent.Server:** BUG-230 (S1): `GET /mentor/chat` ran a turn for any request the browser sent with the
  sign-in cookie, and with cookie authentication (`SameSite=Lax`) that includes a link clicked on another site: one
  link ran a turn as the signed-in user, with the link author's question, even with `RequireAuthorization`. Both
  `GET` and `POST /mentor/chat` now answer `403` before the turn when the browser's Fetch Metadata says another site
  started the request (`Sec-Fetch-Site` `cross-site` or `same-site`, `Sec-Fetch-Mode` not `cors`), and log a warning
  `chat.cross-site-refused` without the question. Your own page's calls, clients without Fetch Metadata and `cors`
  requests work as before. With a `SameSite=None` cookie, or for browsers that send no Fetch Metadata, add a check
  of your own: the Server README says how.

## [1.2.0] - 2026-10-27

### Added

- **Novior.MentorAgent.Abstractions:** `IMentorStateService.OnActionStarted` gives the tool's technical name next to
  the text shown, in a `MentorActionStarted`: `OnActionExecuting` carries only the text shown, so whoever recorded
  the actions from it could not tell for sure which tool ran. It is raised where the turn runs, not by the WebAssembly
  client, and the interface implements it as an event nobody raises, so an implementation written for 1.1.0 still
  compiles. The documentation of `OnActionExecuting` now says it carries the text shown, not the tool name.

- **Novior.MentorAgent.Abstractions, Novior.MentorAgent:** `IMentorActionObserver` receives every decision of the tool
  gate, on every surface and for every agent; `AddMentorActionObserver<T>()` registers one. A `MentorActionDecision`
  carries the outcome (`MentorActionOutcome`), a reason code that stays the same across versions
  (`MentorActionReasons`), the tool, the agent, the surface, the session, turn and call ids, the user and who
  confirmed — never the arguments. A decision never waits for an observer: each has a queue of its own and 5 seconds a
  call.

- **Novior.MentorAgent.Abstractions:** an action that asks for confirmation knows who confirmed it and when:
  `MentorActionConfirmation.Current`, read inside the action's method, carries the confirmation's id, the tool, the
  time and who confirmed — the caller of `POST /mentor/approve`, or the session's user in a circuit and in the MAUI
  app — in Blocking and in Native mode. It is `null` for an action nobody confirmed. The approval event came when
  the action was already on its way, so a ledger of the writes could not say who approved one.

- **Novior.MentorAgent:** `MentorOptions.ConfirmationTimeout` bounds how long a confirmation waits for its answer,
  counted from when the banner is shown. Once it has passed with no answer, the confirmation counts as rejected: the
  action does not run, its decision is `Rejected` with the new reason code `confirmation-timeout`, the span's
  `mentoragent.tool.confirmation` is `timed-out`, and the model is told nobody confirmed it in time, not that the user
  declined. An answer that comes later is ignored, as one after Stop is: `POST /mentor/approve` answers 404. It applies
  to a confirmation a policy asks for too, in Blocking and in Native mode, and for specialists and team members. Zero or
  a negative value, the default, is no limit, as before.

- **Novior.MentorAgent, .Abstractions, .Blazor, .Server:** two tools for article 50 of the EU AI Act. A notice that
  tells people they are talking to an AI assistant, under the widget's header for the whole conversation
  (`ShowAiDisclosure`, `AiDisclosureMessage`, on `MentorOptions` and `MentorAgentBlazorOptions`), with a default text
  in the ten languages of `Language`; and a machine-readable marker on every reply the model writes:
  `data-ai-generated="true"` on the reply in the widget, `"aiGenerated": true` on the `chunk` and `completed` frames of
  `/mentor/chat`, the hub event `ReplyStarted` before each reply, whose argument is a `MentorReplyStarted`
  (`{ "aiGenerated": true }`), and `aiGenerated` in the metadata of the reply to an A2A task. Turning the notice off
  leaves the marker. The results of the MCP server, the spoken replies and the images and files of the provider's
  hosted tools carry no marker. Which words to show, and whether the article applies, is the application's to decide.

- **Novior.MentorAgent.Abstractions:** `MentorTheme.Auto` follows the visitor's system setting
  (`prefers-color-scheme`): the light theme, `Minimal`, when it asks for light, which is also what a browser reports
  when the system states no preference, and `Default` when it asks for dark. It follows the system, not the page: on
  a page that is always light, a visitor whose system is dark sees the dark widget. No script is involved, and the
  default theme stays `Default`.

- **Novior.MentorAgent, .Abstractions, .Blazor, .Server:** a language per page. `<ChatWidget Language="…" />` gives
  the widget a language of its own for its texts, its voice and its onboarding tour, and every turn it sends carries
  it: the assistant answers in it, and what MentorAgent writes for that turn follows it. The language travels as
  `MentorUserMessage.Language`, in the hub's new `SendUserMessage`, as `language` on `/mentor/chat` and on `GET
  /mentor/tour?language=`. `IMentorOrchestrator` and `IMentorTour` get default methods, so implementations written for
  1.1.0 compile. A change of `MentorOptions.Language` while the application runs applies from the next turn. A2A and
  MCP requests stay on `MentorOptions.Language`.
- **Novior.MentorAgent:** `MentorOptions.SessionContextPlacement`. With `LastUserMessage` the context of each request
  — the page, its data, its address, the date and time — goes at the start of the user's last message instead of the
  end of the instructions, which then stay the same through the session: a model that caches the prompt by its prefix,
  a local engine above all, no longer reads the whole conversation again on every new minute or page. The stored
  conversation keeps the user's words. Path A with local history only; the default, `Instructions`, changes nothing.

### Changed

- **Novior.MentorAgent, .Abstractions, .Blazor:** the widget now shows, by default, under its header: "You are talking
  to an AI assistant: its replies are generated automatically.", in the configured `Language`, for the whole
  conversation. Set `AiDisclosureMessage` to your own words, or `ShowAiDisclosure` to `false` to show none.

- **Novior.MentorAgent.Server:** the hub sends `ReplyStarted` before each reply. A client that does not register it
  keeps working, and SignalR logs a warning for every reply ("Failed to find handler" in .NET, "No client method with
  the name" in JavaScript): register it, even with a handler that does nothing — `connection.on("ReplyStarted", () =>
  {})`, or `hub.On<MentorReplyStarted>("ReplyStarted", _ => { })`. The WebAssembly client of Novior.MentorAgent.Blazor
  registers it.

- Every package is now checked at pack time against the public API of 1.1.0, so 1.2.0 cannot break code built
  against 1.1.0 by accident. An intended break is listed in this file as Breaking.

- The family moves onto the Microsoft Agent Framework 1.26.0 line (`Microsoft.Agents.AI` and
  `Microsoft.Agents.AI.Workflows` 1.26.0) and `Microsoft.Extensions.AI` 10.11.0. **Novior.MentorAgent.Declarative:**
  `Microsoft.Agents.AI.Declarative` moves to 1.26.0-rc1, on the same line. In Agent Framework 1.26
  `AgentSessionStore.DeleteSessionAsync` is abstract: a host with an `AgentSessionStore` of its own implements it.
  1.26 also refuses two tool approval requests with one id; MentorAgent gives each call of a response an id of its
  own (BUG-225), except with `UseServiceManagedHistory`.

- With Agent Framework 1.25 or later, when control returns to an agent of the handoff workflow behind `route_to_specialist`,
  the workflow repeats the user's request to it instead of leaving it a tool result alone: whoever reads the
  workflow's messages sees one more after each return. The answers do not change.

- `ModelContextProtocol` moves to 2.3.0: the MCP client in Novior.MentorAgent, and
  `ModelContextProtocol.AspNetCore` 2.3.0 in **Novior.MentorAgent.Server**. The SDK's HTTP server is stateless by
  default from 2.0; the MCP endpoint asks for the stateful transport, so a client keeps its `Mcp-Session-Id` and the
  endpoint answers as before, a `GET` without a session with 400 included.

- An MCP server of the host that lists a tool without `inputSchema` is no longer connected: ModelContextProtocol 2.0
  refuses such a tool where 1.x gave it an empty schema, and the server's whole tool list is lost with it. The
  warning names the server, and its exception names `inputSchema`; the assistant goes on without that server.

- The A2A SDK moves to 1.0.0-preview3: `A2A` in Novior.MentorAgent and `A2A.AspNetCore` in
  **Novior.MentorAgent.Server**.

- **Novior.MentorAgent:** a call refused for its roles is logged once, at Warning, in Blocking and in Native mode
  alike. In Native mode a user who was not signed in was refused before the banner with a Debug line only, and in
  Blocking mode a missing role wrote two Warnings. A confirmation cancelled by Stop or by the end of the session is
  now logged at Debug in Native mode too, and a tool call's Debug line is written once instead of twice.

- The `mentor.tool.{toolName}` and `mentor.mcp.tool.{toolName}` spans carry the outcome
  (`mentoragent.tool.outcome`), the reason code (`mentoragent.tool.reason`), the confirmation
  (`mentoragent.tool.confirmation`) and the surface (`mentoragent.surface`), and a failed call sets the span's
  status to error. In Native mode a call refused, rejected or stopped before it runs now has its span too.

- **Novior.MentorAgent.Server:** the MCP server's line for each `tools/call` tells apart a call refused for its roles
  (`denied-role`), an action the server never publishes (`withheld`) and a name nothing defines (`unknown`): the
  three were all `unknown`. A call the client cancels is `cancelled`. The caller is still told the same for every name
  it may not use.

- **Novior.MentorAgent.Abstractions, Novior.MentorAgent, .Server:** `IMentorActionPolicy` lets the application's own
  code decide whether a user may use an action and whether a call may run — a permission per company or per record, a
  rule on the arguments — on every surface: the coordinator on both paths and in both modes, the specialists, team
  members and agent sources, the MCP servers the application consumes, the MCP server and A2A tasks.
  `AddMentorActionPolicy<T>()` registers one. `IsAllowedAsync` decides what is offered and is asked again before every
  call; `BeforeActionAsync` answers `Proceed`, `Confirm` or `Stop`, and the strictest answer of all the policies wins.
  A policy that throws, or does not answer within the new `MentorOptions.ActionPolicyTimeout` (5 seconds), stops the
  call. The gate's decisions gain the outcomes `Blocked` and `PolicyFailed` and `MentorActionDecision.Detail`.

- **Novior.MentorAgent.Abstractions, Novior.MentorAgent:** `IMentorActionCatalog.GetActions()` lists the
  application's actions as MentorAgent sees them — the coordinator's, the specialists' and the team members' — with
  their roles, whether a call asks for a confirmation now, whether they only read, whether and to whom the MCP server
  publishes them (`MentorMcpExposure`) and whether the A2A card lists them, decided at every call by the rules of the
  MCP server and of the card.

- **Novior.MentorAgent:** a change of default. With `MentorshipLevel.Proactive` the prompt no longer asks for
  suggested next steps at the end of an answer, and asks for none: told to offer only what the request's tools,
  pages, skills and specialists can do, the model still offered steps the application does not have (BUG-074, S3).
  Measured on 15 turns of the same build, the closing offers went from 15 to 2, and the offers of something the
  application does not have from 4 to 0. The new `MentorOptions.ProactiveNextSteps` brings the old rule back, word for
  word, when set to `true`. The rest of the level, the explanations, the warnings and the one sentence before
  acting, and the other levels do not change.

- **Novior.MentorAgent:** the package README opens with what MentorAgent is today: AI agents for any .NET
  application, with the chat widget for Blazor and .NET MAUI, and any other frontend through
  `Novior.MentorAgent.Server`; it links the documentation at docs.mentoragent.net. The package description and tags
  say the same. The repository's front page on GitHub is now a short README.

- SECURITY.md says when a vulnerability report is answered (within 5 working days, best effort), how a fix is
  announced (a GitHub Security Advisory and the release notes) and how long each version gets security fixes: 3
  months after the next minor version, 6 months after the next major version; 1.0.x until 21 January 2027.
  SUPPORT.md and the README say the same.

- The README says how to run MentorAgent on more than one instance, and the policy by which it follows Agent
  Framework, Microsoft.Extensions.AI, ModelContextProtocol and A2A.

### Fixed

- **Novior.MentorAgent:** with Agent Framework 1.23 or later, an agent offered two tools with the same name — two
  level-1 methods, or one name from two MCP servers — failed every turn with "Duplicate tool name". Each agent is now
  offered one tool per name: the first, which is the one a call by that name already ran. A warning names every copy
  and the one kept. A name the coordinator shares with a specialist stays with both (BUG-195, S2).

- **Novior.MentorAgent.Declarative:** with Agent Framework 1.23 or later, `MentorDeclarativeOptions.ConfigurationSection`
  gave a definition no values: the framework loads configuration only for the keys a definition names through `Env`
  and the host lists as allowed. The package now loads the section itself, one Power Fx variable per key, named by its
  full configuration path, as before (BUG-196, S2).

- **Novior.MentorAgent, .Server:** on A2A 1.0.0-preview3 a turn delegated to a peer came back empty, with no error. A
  preview3 server refuses a streamed request unless its agent declares streaming, and the SDK's client reads the
  refusal as an empty stream; the agent MentorAgent registered with the SDK declared nothing. It now declares
  streaming, as the published card did. Towards any peer, `A2AChatClient` streams only when the peer's card declares
  streaming and otherwise asks once without a stream, and an empty stream logs a warning naming the peer (BUG-197, S2).

- **Novior.MentorAgent:** a shared MCP server that connected but did not answer `ping` was opened and dropped again
  in a loop with no limit, and the turn never reached the model. A session that does not answer `ping` right after it
  was opened now counts as a failed connect: the server is left out for the failure window, like one that is down,
  and a warning says it connected but does not answer ping (BUG-198, S3).

- **Novior.MentorAgent:** the coordinator's model was offered every action, those the user had no role for included:
  asked for one, it announced it and the gate refused it. It is now offered, and counts, only the actions the user may
  run, decided at the start of every turn on both paths (BUG-200, S3), and with `RequireActionForChangeRequests` such
  an action is no longer one the first call can be required to make (BUG-201, S3). A turn that leaves an action out
  says so in its instructions, naming no action and no role; a call made anyway is refused, as before.

- **Novior.MentorAgent, .Server:** the MCP server read the `RequiresApproval` predicate once, when it built its tool
  catalogue at the first request: an action the predicate gated after that stayed in `tools/list` and ran for a caller
  nobody could ask. The predicate is now read for every `tools/list` and every `tools/call`; an action it holds back is
  not listed and, called, is refused as withheld (BUG-202, S2).

- **Novior.MentorAgent:** in `MentorHitlMode.Native` the approval markers are decided when the coordinator is built,
  so a tool the `RequiresApproval` predicate gated after that ran with no confirmation, and with an auto-approval rule
  its decision read `auto-approved` though no rule was asked. The gate now asks for the confirmation of such a call,
  with the same banner and the same `POST /mentor/approve` as in Blocking mode, and a task from another agent refuses
  it with `nobody-to-confirm` (BUG-203, S2).

- **Novior.MentorAgent:** a Stop pressed after the user confirmed an action, before the action started, did not stop
  it. Nothing read the turn's token between the answer and the run, so an action that does not read it itself ran, in
  Blocking and in Native mode, and for a specialist's action too. The gate now reads it right before the action runs:
  the action does not run, its decision is `Stopped` with `stop`, and the model is told the user stopped the request.
  In Native mode the answers are no longer sent once Stop has come, so the next turn closes the request as never run
  instead of as one that may have (BUG-204, S2).

- **Novior.MentorAgent, .Server:** a caller that cancelled an A2A task (`tasks/cancel`) was answered `Working`, and the
  task never ended `Canceled`. A caller waiting for the reply saw the turn go on, its actions included, and the task
  end `Completed`; a streamed or `returnImmediately` task stopped, but stayed `Working` for good. Cancelling now stops
  the task's turn as Stop does — nothing starts after it, and the call in progress is `Stopped` with `stop`, not
  `session-ended` — and the task ends `Canceled`, in all three cases. The A2A SDK cancels the turn before it tells the
  agent, so MentorAgent puts its own request handler in front of the SDK's; `MapMentorAgentA2A()` logs a warning when
  a version of the SDK cannot be wrapped, and a cancelled task still ends `Canceled` then (BUG-199, S2).

- **Novior.MentorAgent:** specialists, team members and the agents of an `IMentorAgentSource` were offered every tool
  they hold, those the user had no role for included, and the gate then refused the call. They are now offered, and
  told, only the tools the user may run, by the coordinator's rule, in both modes; a request that leaves a tool out
  carries the coordinator's notice (BUG-205, S3).

- **Novior.MentorAgent, .Server:** the onboarding tour was written from every action of the application, those that
  need a role included: it could suggest one its reader may not use, and `GET /mentor/tour`, which answers anyone,
  signed in or not, could describe actions the MCP server describes to nobody without the role. The tour is still one
  for every visitor, generated once; it is now written from the actions that need no role (BUG-206, S3).

- **Novior.MentorAgent:** the onboarding tour was generated on the request of the first visitor who asked for it. A
  visitor who closed the page while the model wrote it, or whose `GET /mentor/tour` was cut short, ended the
  generation, and the built-in tour, with no question to try, was kept for every visitor until the process restarted.
  The generation now depends on no visitor's request: one who stops waiting only stops waiting, and the tour the model
  writes is the one everyone gets (BUG-207, S4).

- **Novior.MentorAgent:** in `HitlMode.Native` the banner of a coordinator's action took its text — the description
  and the order of the arguments — from the first method found with the tool's name, not from the method the call
  runs, though the 1.1.0 notes said the banner followed that method in both modes (BUG-188). Since an agent is
  offered one tool per name (BUG-195), the two differ when the first method's class is not registered and the
  coordinator is offered a later one: the user was asked to confirm what the other method does. The banner now reads
  as the method the call runs in Native mode too (BUG-208, S3).

- **Novior.MentorAgent.Abstractions:** the stylesheet and script links `ChatWidget` writes kept `?v=10` from the
  first release on, while both files changed in every release. Neither file is fingerprinted, and a host serving them
  as classic static files sends no `Cache-Control`, so a returning visitor's browser could keep the previous
  release's copies after an upgrade: new elements unstyled, and what the older script lacks failing silently. The
  links now carry the version the package was built as, with its commit (`?v=1.2.0+…`). A host that writes the two
  links itself — a standalone WASM `index.html`, a surface without `ChatWidget` — sets `?v=` to the package version,
  `?v=1.2.0` for this release, and changes it on every upgrade (BUG-209, S3).

- **Novior.MentorAgent:** a Stop pressed while a request was delegated to a remote agent over A2A stopped the local
  turn and told the peer nothing: the peer finished the task, actions that ask for no confirmation included. A turn
  that leaves a delegated task before its answer now cancels it on the peer (`CancelTask`), waiting two seconds at
  most for the peer to take it; a Stop that comes before the peer's first event, which names the task, keeps the
  request open up to two seconds for it. A peer whose card declares no streaming is now asked with
  `returnImmediately`, and its task read with `GetTask` until it ends, so that it can be cancelled too (BUG-210, S2).

- **Novior.MentorAgent:** a Stop during the first message of a session, while a remote agent's card was being read,
  was logged as the agent not answering ("did not answer: The operation was canceled") and kept the agent out of
  reach for a minute, and the session's coordinator was built without it for as long as the session lasted. A Stop
  there now ends the build: nothing is remembered against the agent, and the next message builds the coordinator
  with it (BUG-211, S3).

- **Breaking — Novior.MentorAgent:** a tool result became a generative card by its shape alone: any object with a
  non-empty string `kind`. A record of the application's with such a property, a support ticket with a link a
  customer wrote in it, was drawn as a card, the link a button, and its other fields never reached the model. A
  result is now read for cards only when the method that produced it is declared to return them: `MentorCard`, or a
  sequence of them, directly or through a task. A method that returned cards as `object`, as a `JsonElement` built
  by hand or as a type of its own shows them as text: declare `MentorCard` or `IEnumerable<MentorCard>`. A `MentorCard` an
  `OnToolResult` hook returns is still a card; an MCP tool's result never is (BUG-212, S3).

- **Novior.MentorAgent.Abstractions:** under a Content-Security-Policy without `style-src 'unsafe-inline'`, such as
  `default-src 'self'`, the Dark and Minimal themes and a `PrimaryColor` were dropped, and `<MentorDashboard />` lost
  the colours of its charts and the width of its bars. The themes are now stylesheets of the package, a `PrimaryColor`
  is set with `style.setProperty` once the widget's script runs, and the dashboard uses classes and SVG: both work
  under `default-src 'self'` with any option. A host's `html:root` rule no longer wins over the five variables a
  `PrimaryColor` sets (BUG-213, S3).

- **Novior.MentorAgent.Abstractions:** IntelliSense described `MentorTheme.Default` as "Light theme with blue accents"
  and `Minimal` as having no shadows. `Default` is the baseline's dark glass, a navy panel at 55% opacity, which on a
  light page shows as dark grey with the page behind it; the light theme is `Minimal`. The documentation now says
  which is which (BUG-214, S4).

- **Novior.MentorAgent.Abstractions:** a Markdown link in a reply, `[text](https://…)`, was shown as raw text, brackets
  and parentheses included, and could not be clicked. It is now a link that opens in a new tab, with
  `rel="noopener noreferrer nofollow"`, when its address is an absolute `http` or `https` one; any other scheme,
  `javascript:` and `data:` included, and a relative address stay text. The model's HTML is still always encoded
  (BUG-215, S3).

- **Novior.MentorAgent.Abstractions:** numbered steps with a line of text between them started again from 1 after the
  line. A numbered list now starts at its first step's number, and a line or a code block indented under a step stays
  inside it (BUG-216, S3).

- **Novior.MentorAgent.Abstractions:** a message holding the text the Markdown renderer uses as a placeholder for a
  code block, `@@BMCODE7@@` for instance, threw out of the render: on Blazor Server the circuit ended, and the host's
  page stopped responding until a reload. The text may come from the user, or from the model repeating data others
  wrote. It is now shown as it is (BUG-217, S1).

- **Novior.MentorAgent:** with a `Language` other than English, the instructions MentorAgent writes for the model —
  its example sentences, the grounding rule, the instructions in tool results — were in English, and a model could
  copy one into its reply ("Understood, l'email è stata annullata"). They now follow `Language`, in all ten languages;
  in English they are unchanged (BUG-221, S3).

- **Novior.MentorAgent:** with `RefuseOutOfScope`, a short reply to the assistant's question was judged on its own:
  on the demo the assistant asked "Vuoi vedere i dettagli…?", the user answered "Sì", and the turn was refused as out
  of scope, 5 times of 5 in Italian ("Yes" passed 3 of 3). The scope question is now shown the end of the
  assistant's last reply, in the same classifier call, and the verdict is kept for the message and that reply
  together, so the same "Sì" after another question is judged again. The safety question still judges the message
  alone, and the first message of a conversation is asked as before (BUG-223, S2).

- **Novior.MentorAgent:** the notice of a turn that leaves out an action the user may not run (BUG-200) was English
  in every language, and in Italian the model offered the action and, after "Sì", used another one or made up its
  result. It now follows the language of the turn, under its English heading (English unchanged), and ends with
  three rules: no offer of such an action, a yes does not make it available, no other tool in its place. On the demo
  the card of `show_expense` is now shown, its Approve button only for a manager or an administrator (BUG-224, S2; a
  residue is listed under *Known issues*).

- **Novior.MentorAgent:** in Native mode a response with two calls of one id broke the conversation until it was
  reset ("An item with the same key has already been added"). A call whose id the response or the conversation
  already holds now gets the next free `<id>_n`, on the coordinator and the agents under it; not with
  `UseServiceManagedHistory` (a warning says so) nor on path B. Two turns in a row that fail the same way on the
  requests left open log an error that suggests a new conversation (BUG-225, S2).

- **Novior.MentorAgent:** an A2A caller told the task at once (`ReturnImmediately`) could lose who it is: the SDK
  answers it as soon as the task is submitted, its request ends while the turn runs on, and the turn read the caller
  from that request only then. An authenticated peer became anonymous, its role-gated actions were refused, its
  memory and rate limit went under another key. The caller is now read when the task starts (BUG-226, S2).

- **Novior.MentorAgent.Server:** the Server README's option tables left out `MentorOptions.ConfirmationMessage`,
  although the 1.1.0 notes said every option was in a reference table of both READMEs or among the Server README's
  options not listed on purpose (BUG-163). It is now in the table of the confirmation options, and a test checks that
  the tables of both READMEs list every option of `MentorOptions` and every property of `[MentorAction]` (BUG-227,
  S4).

- **Novior.MentorAgent.Abstractions:** with the onboarding tour open, a change of the widget's `Language` translated
  the tour's buttons and left its steps in the language it had opened in. An open tour now fetches its steps in the
  new language and stays on the step it was at, or on the last one when the new language has fewer (BUG-228, S4).

- **Novior.MentorAgent:** `RequireLookupForRecordQuestions` had no effect without semantic tool filtering, and on the
  demo, which has no embedding model, a question about pending expenses asked in Italian was answered about one time
  in three with a list the model made up. Without the filter, the first call of a record question must now call one of
  the declared reads the user may run, or hand the request over; with the filter nothing changes. The demo turns the
  option on (BUG-222, S2).

### Known issues

- **Novior.MentorAgent:** BUG-074 (S3) is closed by default: since 1.2.0 `MentorshipLevel.Proactive` asks for no
  closing offers unless `ProactiveNextSteps` is on. With it on, the assistant can still offer next steps the
  application does not have — 4 of 15 closing offers on the headless server sample, measured on 1.2.0.
- **Novior.MentorAgent:** BUG-118 (S2) is closed for hosts that opt in to `RequireActionForChangeRequests`. Without it
  the model can still narrate an action it never performed — "Stock aggiornato" with no tool called, or "Ecco la
  pagina Prodotti" with no navigation — and with it a complete request the classifier does not recognise runs as
  without the option.
- **Novior.MentorAgent:** BUG-220 (S3): with `MentorHitlMode.Blocking` and a Chat Completions client, the model can
  announce an action that asks for no confirmation — "Procedo a registrare la nota…" — and end its reply without
  calling it: once in 12 turns on the Blazor Server sample, on 1.2.0. `RequireActionForChangeRequests` does not
  prevent it, since it requires only actions that ask for a confirmation. The user can repeat the request; where it
  matters, give the action `RequiresConfirmation = true`, so that with the option a complete request calls it first,
  behind the banner; or use `MentorHitlMode.Native` with a Responses client, where an earlier measurement saw the
  action called almost every time.
- **Novior.MentorAgent:** BUG-224 (S2) leaves a residue in a turn in a language other than English: when the role
  filter leaves an action out, the assistant can still offer it, and after the user's "yes" it can, rarely, call
  another action the user may run in its place — once in 10 Italian tries on the demo, a new expense claim instead of
  an approval. The confirmation that write actions ask for by default stops that call: keep `RequireConfirmation`
  and `RequireConfirmationForWrites` on.
- **Novior.MentorAgent:** BUG-106's residue is closed for hosts that opt in to `RequireLookupForRecordQuestions`;
  without it, a catalogue question can still be answered with an offer to look the value up.
- **Novior.MentorAgent, .Server:** a 1.1.0 host that moves the A2A SDK to 1.0.0-preview3 on its own — `A2A`, or
  `A2A.AspNetCore`, which brings it — cannot answer A2A tasks. NuGet allows it without a warning, since 1.1.0 asks
  for preview2 or later, but preview3 changed the signatures of four `TaskUpdater` methods the 1.1.0 handler calls:
  the handler fails with `MissingMethodException` when a task arrives. Keep `A2A` and `A2A.AspNetCore` at
  1.0.0-preview2 with 1.1.0, or move to 1.2.0.

## [1.1.0] - 2026-10-21

### Added

- **Novior.MentorAgent:** `MentorOptions.ConfirmationMessage` writes the text of the confirmation banner, in the
  application's own words or language. It gets the tool, its description, the arguments as the banner shows them,
  `Language` and the services of the conversation's scope. When it returns `null` the default text is shown; when it
  throws or returns only white space, the default text and a warning (BUG-186).

- Every package carries its software bill of materials: SPDX 2.2, at `_manifest/spdx_2.2/manifest.spdx.json`
  inside the `.nupkg`, with its SHA-256 beside it. It lists the package's files and the packages restored to build
  it, build-time tools included.

- **Novior.MentorAgent:** `MentorActionAttribute.RequiresConfirmationDeclared` is public and read-only: whether the
  attribute writes `RequiresConfirmation`, true or false. With `RequireConfirmationForWrites` on, an action that
  declares nothing asks for confirmation and one that declares `RequiresConfirmation = false` does not, while
  `RequiresConfirmation` reads false for both; a component outside the library can now tell them apart.

### Security

- **Novior.MentorAgent.Blazor:** BUG-173 (S2): the package depended on `Microsoft.AspNetCore.SignalR.Client`
  10.0.3, whose `Microsoft.AspNetCore.SignalR.Common` is covered by advisory GHSA-334f-vvp4-7g6p
  (CVE-2026-70159, high): a client using stateful reconnect can block a server's broadcasts. It now depends on
  10.0.13, the patched release. With 1.0.0, add
  `<PackageReference Include="Microsoft.AspNetCore.SignalR.Client" Version="10.0.13" />` to the client project.
  MentorAgent turns stateful reconnect on neither in its hub nor in its client.
- **Novior.MentorAgent.Server:** the hub runs on the SignalR of the ASP.NET Core shared framework, not on a
  package: a host is patched by the ASP.NET Core runtime 10.0.13 or later.
- A package with a high or critical advisory (NuGet audit NU1903, NU1904), direct or transitive, now fails the
  Release build of the packages.

### Changed

- Every package is now checked at pack time against the public API of 1.0.0, so a 1.x release cannot break
  code built against 1.0.0 by accident. An intended break is listed in this file as Breaking.

- **Novior.MentorAgent, .Server, .Blazor, .Abstractions, .Declarative:** NOTICE says that "MentorAgent" and its
  logo are trademarks of Antonio Macrì and Emanuele Longo, to be assigned to Novior, not licensed under the Apache
  License 2.0, and points to the trademark policy, TRADEMARKS.md, in the GitHub repository.

### Fixed

- **Novior.MentorAgent:** the README's *Migrating from the MentorAgent.\* packages* listed the build diagnostic
  `MENTOR001` among what stays the same. That check, and its `MentorAgentSkipOpenAIVersionCheck` switch, were
  removed in 1.0.0-rc.15: the core package has no build target, and there is no switch to keep (BUG-159, S3).

- **Novior.MentorAgent.Server:** three sentences of the README did not match the code. `IncludeWorkflowExceptionDetails`
  puts the exception message of a failing handoff or group chat agent into the reply, not a stack trace.
  `EnableSuggestions` and the widget options do take effect in a MAUI Blazor Hybrid app, which runs the Blazor
  widget; only a MAUI app with its own native UI ignores them. And the `Authorization` header is missing from a
  WebSocket or Server-Sent Events connection because a browser cannot send one, not because the protocol forbids
  it; the Blazor README said the same and is corrected too (BUG-160, S4).

- **Novior.MentorAgent.Server:** the README's React example for image input did not compile: a `<textarea>` outside
  any component and an `await` at the top level. It is now a component that keeps the attachments in its state and
  sends them from an `async` handler, passing `null` when there are none (BUG-161, S3).

- **Novior.MentorAgent.Server:** the README said that `POST /mentor/cancel` answers `404` when Stop is pressed just as
  the turn ends, and that it is not an error; its React, Angular and Vue components ignored it. The endpoint answers
  `200` whenever the connection exists, with or without a turn running, and `404` only when the connection is closed
  or unknown, which is the case a client must not hide. The README and the three components now say so (BUG-162, S3).

- **Novior.MentorAgent:** the option reference tables of the README and the Server README left out options that only
  their own sections described: `SkillSources`, `SkillFilter`, `SkillsRefreshInterval`, `AutoApprovalRules` and
  `MaxAutoApprovalIterations` in the README; `EnableRichResponses`, `IsReadOnlyAction`, `EnableOutputSafetyCheck`,
  `RequireLookupForRecordQuestions`, `RequireActionForChangeRequests` and `McpCallerPrincipal` in the Server README.
  Every public option of `MentorOptions` is now in a reference table of both, or among the Server README's options
  not listed on purpose. The `[MentorAction]` tables mark `Description` as required, and the Server README's lists
  `ReadOnly` (BUG-163, S4).
- **Novior.MentorAgent:** with a pre-built `Agent` (Path B), `MemoryAutoCapture` and a `ClassifierChatClient`, memory
  had two writers: the post-turn extraction ran through the classifier, one call per message, and the `remember`
  tool was sent too. The log and the documentation said the extraction does not run on Path B. The tool is now left
  out whenever the extraction runs, and the build log names the writer: on Path B the extraction with a
  `ClassifierChatClient`, the `remember` tool without one (BUG-174, S3).
- **Novior.MentorAgent:** a `[MentorTeam]` with `HandoffTo` was approved when `[TEAM:APPROVED]` appeared anywhere
  in the discussion, so a member's "yes" followed by another's "no" still told the coordinator to execute the
  actions. The team is now approved only if its final message carries `[TEAM:APPROVED]` and no member wrote
  `[TEAM:REJECTED]`; a discussion that ends without a verdict is not approved. Without a `[TeamTerminationCondition]`
  the discussion now ends at the first verdict, instead of running all `MaxIterations` rounds; a host's condition
  still decides alone when to stop. The members' instructions keep `[TEAM:REJECTED]` for a definitive no and ask for
  changes without a token (BUG-175, S3).

- **Novior.MentorAgent, .Server, .Blazor, .Abstractions, .Declarative:** the XML documentation that IntelliSense
  shows no longer contradicts the code: anonymous callers share the memory key `"anonymous"` only with
  `AnonymousIdentity = Shared`; an empty `AllowedTools` allows every tool, like `null` (the README says so too);
  `MentorTheme.Custom` renders like `Default`; `IMentorAgent` is optional and validates nothing; the hub's events list
  `Cards`, and `MapMentorAgentServer` lists `/mentor/session` (BUG-164, S4).

- **Novior.MentorAgent:** the README did not say that with `UseRag` the search runs on every message, before the
  model is called, so its time (an embedding call, the search, a reranker) is added to every reply; nor that the
  absence of a keyword pre-gate relies on `RagMinScore` being on the source's scale. It says both now, and so does
  the comment in `RagContextProvider` (BUG-165, S4).

- **Novior.MentorAgent:** the XML documentation of `RequiresApproval` said the predicate is evaluated once per
  tool while the coordinator is built. It is asked again on every call to a tool in Blocking mode and on every turn
  the tool filter matches a tool, so a predicate that reads a file or a database pays for it each time. The
  documentation now says when it is asked and that it must stay a cheap, pure function of the tool name
  (BUG-176, S4).

- **Novior.MentorAgent:** the documentation of `ConfigureChatClientPipeline` said only that the pipeline goes
  outermost. It is built once per coordinator, so once per circuit on Blazor Server and once per SignalR connection
  on the hub: a limit placed there does not limit the process. And only the coordinator's own calls go through it:
  the auxiliary calls — classifier, safety checks, out-of-scope refusal, memory extraction, the lookup and action
  checks — use `ClassifierChatClient ?? ChatClient`, and specialists, team members and the handoff router use
  `ChatClient`. The READMEs and the XML documentation now say both (BUG-166, S4).

- **Novior.MentorAgent:** the READMEs said that `navigate_to` waits for `SignalReady()` on a `HasUIActions` page
  only in a Blazor Server circuit. It waits wherever the page runs in the same process as the turn, so in a MAUI
  Blazor Hybrid app too; turns over the hub, SSE or A2A still do not wait. The READMEs now say so (BUG-171, S4).

- **Novior.MentorAgent:** on Path B (a pre-built `Agent`), `EnableSafetyCheck` and `EnableOutputSafetyCheck`
  without a `ClassifierChatClient` now log a warning when the coordinator is built: neither check can run, so every
  message and every reply goes through unchecked. Both checks run on `ClassifierChatClient`, or on `ChatClient` when
  none is set, so on Path B a `ClassifierChatClient` turns them on; the README and the XML documentation said they
  required a `ChatClient` (BUG-167, S3).

- **Novior.MentorAgent:** the README and the XML documentation of `HitlMode` said that `Native` costs one
  model call more per approved action, and that `Blocking` is cheaper. Both modes make the same model calls:
  `Native` adds one agent run per round of confirmations, and its reply arrives in two segments (BUG-168, S3).

- **Novior.MentorAgent.Blazor:** the README said twice that the server must enable `EnableOnboardingTour` too,
  and the widget's log said so when the tour had no steps. `MapMentorAgentServer()` maps `GET /mentor/tour`
  whatever the server's flag says: the client's flag alone decides whether the tour is shown. The README and the
  log now say so (BUG-169, S3).

- **Novior.MentorAgent:** the README has an example of `RequiredRoles` with Windows groups (Negotiate, `DOMAIN\Group`)
  and with Microsoft Entra ID (app roles, or the `groups` claim's ids). The XML documentation of `EnableObservability`
  said the embedding generator is traced too, and the README "the chat client calls": only the coordinator's model
  calls are traced, and both now say how to trace the rest (BUG-170, S4).

- **Novior.MentorAgent.Server:** the A2A agent card listed the actions of classes not registered in the DI container,
  which no turn can run, and repeated a skill id when two actions share a tool name. It now lists only the actions a
  turn can call, once per name, and leaves a name out when any action carrying it is gated (BUG-177, S3).

- **Novior.MentorAgent.Server:** the warning that lists the actions withheld from the MCP server was written on the
  first MCP request, not at startup as the README says. `MapMentorAgentMcp()` now writes it when it maps the
  endpoint; the tools are still built by the first request, and which of them a caller sees is still decided per
  request (BUG-178, S3).

- **Novior.MentorAgent:** on Path B (a pre-built `Agent`) the `[MentorAgent]` specialists, the `[MentorTeam]` teams
  and the `RemoteAgents` are not built, because they run on `options.ChatClient`, and nothing said so. A warning now
  names each of them when the coordinator is built, and the remote agents' cards are no longer fetched on Path B,
  where no turn could reach them (BUG-179, S3).

- **Novior.MentorAgent:** the two log lines that open and close the discovery were in Italian ("Discovery avviata",
  "Discovery completata"). They now read "Discovery started on …" and "Discovery completed — …" (BUG-180, S4).

- **Novior.MentorAgent:** a team's discussion reached the coordinator as one run-on text ("… is solved.I agree"), so
  it could not tell who said what. Each message is now a paragraph of its own, headed by its author. The team tool's
  description gets a full stop before the handoff note when the team's `Description` has none (BUG-181, S4).

- **Novior.MentorAgent:** two methods carrying the same tool name were reported only with tool filtering on. A model
  calls a tool by name, so an agent offered both reaches only one of them, and up to 1.0.0 one method's attributes
  decided the other's call (BUG-188). A warning now names every shared tool name and its methods when the actions
  are first read, with tool filtering on or off (BUG-182, S4).

- **Novior.MentorAgent:** the signed-in user's roles told to the model came from `ClaimTypes.Role` claims only, while
  the gate decides with `IsInRole`, which reads each identity's own `RoleClaimType`. With roles in a `roles` claim —
  Microsoft Entra ID, or JwtBearer with `MapInboundClaims = false` — an action passed the gate while the model was
  told no role. The model is now told the roles `IsInRole` answers to, from every identity of the user; a Windows
  identity's group SIDs are left out (BUG-183, S3).

- **Novior.MentorAgent:** the RAG citation chips knew one number per marker, "[1] [2]". An answer that cited "[1, 2]"
  or "[1-2]", as models also write, showed no chip at all. A list or a range now raises a chip for each document it
  names; a range with an end that names no document, such as "[0-9]" in a code sample, cites nothing (BUG-184, S3).

- **Novior.MentorAgent:** the instruction that tells the model how to cite the RAG documents was in English whatever
  `Language` said. It is now written in the configured language, in each of the ten, with the same `[1], [2]`
  markers the citation chips read (BUG-185, S3).

- **Novior.MentorAgent:** the confirmation banner showed each argument whole, control and bidirectional characters
  included, in the order of the model's JSON. Each value is now cleaned and cut at 200 characters, and the arguments
  follow the order of the method's parameters (BUG-186, S2).

- **Novior.MentorAgent:** with `HitlMode.Native`, a confirmation that a Stop or a failure left open was answered
  from memory only. A snapshot saved in that state and restored in another scope, or after a reset, still held the
  open request, and every later turn failed as in BUG-116 (a known issue since 1.0.0-rc.15). The open requests and
  their answers now travel with the snapshot, and the first turn after the restore answers them (BUG-187, S3).

- **Novior.MentorAgent:** two methods carrying one tool name shared one set of rules. The roles, the confirmation,
  the banner's text and the page to open came from the first method found with the name, level 1 first, so a
  level-1 method without roles opened a specialist's role-gated action of the same name to every user. The gate now
  applies the rules of the method the call runs, at every level and in both HITL modes (BUG-188, S2).

- **Novior.MentorAgent, .Abstractions, .Blazor:** the README taught `PageContext.Clear()` in a page's `Dispose()`.
  Blazor disposes the previous page after it has initialized the next one, so from one page with UI actions to
  another, the previous page's `Clear()` wiped the actions the next page had just registered, and the assistant
  answered that the action did not exist. `IMentorPageContext.Clear(pageName)` clears only what that page
  registered, or everything when it is still the current page: call it in `Dispose()` with the page's name.
  `Clear()` still clears everything (BUG-192, S2).

- **Novior.MentorAgent.Abstractions:** in a table inside an assistant bubble, a number stood one digit per line. The
  bubble lets a word break anywhere, so the table's columns could shrink to one character. The cells now break
  between words only, a cell holding only a number, an amount, a date or a time stays on one line, and a table wider
  than the bubble scrolls inside it instead of widening the conversation (BUG-193, S4).

- **Novior.MentorAgent:** `navigate_to` to a page with `HasUIActions` could return before the new page had registered
  its actions. The page being left had called `SignalReady()` when it opened, and that signal ended the wait at once.
  `navigate_to` now takes it back before asking for the navigation, so it waits for the new page's `SignalReady()`;
  to the page already shown it does not wait, since Blazor does not initialize that page again (BUG-194, S3).

## [1.0.0] - 2026-10-13

### Changed

- **Breaking — all packages:** the packages are now `Novior.MentorAgent`, `Novior.MentorAgent.Abstractions`,
  `Novior.MentorAgent.Blazor`, `Novior.MentorAgent.Declarative` and `Novior.MentorAgent.Server`; the `MentorAgent.*`
  ids end at 1.0.0-rc.15. The namespaces follow (`using Novior.MentorAgent.Extensions;`), the widget's static files
  are served from `_content/Novior.MentorAgent.Abstractions/`, and the log categories start with
  `Novior.MentorAgent`, so a `"MentorAgent"` filter under `Logging:LogLevel` no longer matches. The five packages
  depend on each other at their exact version. Unchanged: the types and extension methods (`AddMentorAgent`,
  `MentorOptions`, …), the routes, the telemetry source, meter and metric names, the session snapshot format, the
  `MentorAgentMcp` CORS policy, `MentorAgent.css`, `MentorAgent.js` and `window.MentorAgent`. The README's
  *Migrating from the MentorAgent.\* packages* walks through the move.

### Fixed

- **Novior.MentorAgent:** four Warnings outside the tool gate still carried a whole exception, whose text comes from the
  host's code: a UI action that threw, with a `ChatClient` or a pre-built `Agent`, and an `OnToolResult` or a
  `McpCallerPrincipal` that threw on the MCP server. As in the gate, they name the exception's type; the whole
  exception is logged at Debug (BUG-152, S3).
- **Breaking — Novior.MentorAgent.Server:** `AddMentorAgentMcp()` or `AddMentorAgentA2A()` called before `AddMentorAgent()`,
  or with `McpServerEnabled` / `A2AServerEnabled` turned on after `AddMentorAgent()` read them (a `Configure` or
  `PostConfigure` of `MentorOptions`), registered nothing and said nothing; the map call failed later, in the
  second case naming a call the host had made. The host now stops at start-up with the reason and the fix, and
  one that ran without the server because of it no longer starts (BUG-153, S3).
- **Novior.MentorAgent:** the README and the rc.15 notes said that the warning about writes that run without
  confirmation comes at startup. It comes the first time the actions are read: at startup only with
  `WarmUpAtStartup` or `MapMentorAgentA2A()`, otherwise with the first conversation or the first MCP or tour
  request. With `[Description]` alone it also names the methods that only read, until `IsReadOnlyAction` or
  `[MentorAction(ReadOnly = true)]` declares them. The README now says both (BUG-154, S3).
- **Novior.MentorAgent.Server:** the README's minimal SignalR client called Stop, the confirmations and the session
  save and restore without the hub's credentials: with cookies from another origin, or a bearer token, those
  calls were anonymous and did nothing. It now sends them, like the complete components. The README also no
  longer says that the MCP and A2A servers are built into `MentorAgent` (BUG-155, S3).
- **Novior.MentorAgent:** the READMEs, the XML documentation of `AddMentorAgent()` and the start-up warning for a
  callback that throws listed `AppDescription` and `AgentCard` among the options read at registration.
  Only `AppName` is, as the MCP server's name; the A2A card is built from the real options when
  `MapMentorAgentA2A()` runs (BUG-156, S4).
- **Novior.MentorAgent:** the README's MAUI step 3, pasted into the MAUI Blazor Hybrid template, did not compile: it
  lacked `using System.Reflection;` and `using Microsoft.Extensions.Configuration;`, and `AddUserSecrets` needs
  `Microsoft.Extensions.Configuration.UserSecrets`, which neither the template nor `MentorAgent` brings. The step
  now names both and the package, and says that `AddUserSecrets` needs the project's `UserSecretsId` and finds
  the secrets only on the development machine (BUG-157, S3).
- **Novior.MentorAgent.Abstractions:** its README said that the core package depends on
  `ModelContextProtocol.AspNetCore`; since rc.15 that transport is in the Server package, and the core
  references `ModelContextProtocol` (BUG-158, S3).

## [1.0.0-rc.15] - 2026-10-07

### Added

- **MentorAgent.Server:** `AddMentorAgentMcp()` and `AddMentorAgentA2A()` register the MCP server and the A2A
  agent, each only when its option (`McpServerEnabled`, `A2AServerEnabled`) is on, as `AddMentorAgent` did up
  to rc.14. `AddMentorAgentServer()` calls both, so a headless host changes nothing. `MapMentorAgentMcp()` /
  `MapMentorAgentA2A()` without them stop the host at startup with an error that names the method to add.
- **MentorAgent:** a RAG source can see who is asking, from where and after what. MentorAgent now calls
  `IMentorRagSource.SearchAsync(MentorRagRequest, CancellationToken)`, whose request carries the message,
  `RagResultCount`, the last three turns of the conversation, the signed-in `ClaimsPrincipal`, and the
  current page's name and data. Its default implementation calls `SearchAsync(query, maxResults)`, so every
  existing source compiles and behaves as before.
- **MentorAgent.Abstractions:** `MentorRagResult.Metadata`, key/value facts about a document (a date, a
  version). The model sees them under the document's title. They also travel with the citation chips: hub
  clients receive `metadata` in `RagSourcesReady`. It is an init property, so the four-value constructor is
  unchanged.
- **MentorAgent:** `IMentorRagSource.DefaultMinScore` lets a source declare the relevance threshold for its own
  scale. It applies when the host has not set `RagMinScore`, in code or through configuration. A vector source
  that declares one, say 0.6, now gets its documents to the model with the default options; before, the
  default of 2 discarded every cosine score. The wrong-scale warning now says which threshold discarded
  everything: the option, the source's or the default.
- **MentorAgent:** `MentorOptions.ChatReducer` replaces the reducer of the built-in in-memory history, and
  `MentorChatReducer` (namespace `MentorAgent.Core`), the new default, can be given to a history provider of
  your own. A `ChatReducer` that cannot apply — next to a provider you supply, or with
  `UseServiceManagedHistory` — logs a warning.

### Changed

- The five packages are licensed under the Apache License 2.0 from this version, with a `NOTICE` file next to
  `LICENSE`. Versions up to and including 1.0.0-rc.14 keep the MIT License.
- Package metadata: the authors are Emanuele Longo and Antonio Macrì, the copyright holders named in `NOTICE`;
  the project page is https://mentoragent.net.
- **Breaking — MentorAgent:** the MCP server and the A2A agent moved to **MentorAgent.Server**, together with
  `MapMentorAgentMcp()` and `MapMentorAgentA2A()` (`EndpointRouteBuilderExtensions`, same namespace
  `MentorAgent.Extensions`). `AddMentorAgent` no longer registers them. The reason: their packages,
  `ModelContextProtocol.AspNetCore` and `Microsoft.Agents.AI.Hosting.A2A.AspNetCore`, require
  `Microsoft.AspNetCore.App`, which does not exist on Android, iOS or Mac Catalyst. A MAUI app that referenced
  MentorAgent built only for Windows (`NETSDK1082`). Now the MAUI Blazor Hybrid template, unchanged, builds
  for `net10.0-android`, and it no longer needs its `Microsoft.Extensions.Logging.Debug` pin raised (`NU1605`).
  MentorAgent now references `ModelContextProtocol` 1.3.0 and `A2A` 1.0.0-preview2, for the MCP and A2A
  clients. MentorAgent.Server references `ModelContextProtocol.AspNetCore` 1.3.0 and `A2A.AspNetCore`
  1.0.0-preview2.
  **Migration.** A host that calls `AddMentorAgentServer()` changes nothing in its code. A Blazor Server host
  that serves MCP or A2A without the hub adds the `MentorAgent.Server` package and calls
  `builder.Services.AddMentorAgentMcp()` and/or `AddMentorAgentA2A()` after `AddMentorAgent()`; its usings
  stay as they are. Code compiled against rc.14 must be rebuilt, because the type changed assembly.
  A host no longer receives these packages transitively from MentorAgent, so it references the ones it uses:
  `Microsoft.Agents.AI.Hosting`, `Microsoft.Agents.AI.Hosting.A2A`, `Microsoft.Agents.AI.Hosting.AspNetCore`,
  `Microsoft.Agents.AI.Hosting.A2A.AspNetCore`, `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.Logging.Console`
  and the other `Microsoft.Extensions.Configuration.*` / `Logging.*` packages that came with it (BUG-119, S2).
- **MentorAgent:** the READMEs now place MCP and A2A where the code has them: in MentorAgent.Server.
- **Breaking — MentorAgent:** writes ask for confirmation by default. A `[MentorAction]` that declares neither
  `ReadOnly = true` nor `RequiresConfirmation` is treated as an action that changes data. It shows the
  confirmation banner, in the Blocking flow and in `HitlMode.Native` alike, like one declared
  `RequiresConfirmation = true` (`RequireConfirmationForWrites`, new, on by default). Being gated, it is also
  withheld from the MCP server (`tools/list`) and from the A2A agent card, and it is no longer among the
  ungated actions the zero-match fallback may send on a message that matches nothing. With
  `RequireActionForChangeRequests` on, more actions can now be required as the first call, because only
  actions that ask are ever required. **Only `[MentorAction]` methods are covered**: a method marked with
  `[Description]` alone asks for nothing and stays on `/mcp`, as before; mark it `[MentorAction]` to have the
  default. **Migration:** declare `ReadOnly = true` on the actions that only read and
  `RequiresConfirmation = false` on the writes that must run unasked, or set
  `options.RequireConfirmationForWrites = false` for the rc.14 behaviour. At startup a warning lists the writes
  that run without confirmation, in two lists: the `[MentorAction]` methods opted out, and the
  `[Description]`-only methods not declared read-only with `IsReadOnlyAction`.
- **Breaking — MentorAgent:** no AI provider SDK comes with the package any more: it no longer references
  `Azure.AI.OpenAI` 2.9.0-beta.1, `Azure.Identity` and `Microsoft.Extensions.AI.OpenAI [10.6.0]`, which the
  library never used. It references `System.ClientModel` 1.10.0, the version those packages brought, because it
  reads the HTTP status of a provider's `ClientResultException`. A host now adds its provider at the version it
  chooses — for Azure OpenAI, `Azure.AI.OpenAI` and `Microsoft.Extensions.AI.OpenAI` (`AsIChatClient()`), plus
  `Azure.Identity` for `DefaultAzureCredential`. With the Azure **Responses** API keep `OpenAI` at 2.10
  (`Microsoft.Extensions.AI.OpenAI [10.6.0]`). On 2.11 or later, `GetResponsesClient()` from `Azure.AI.OpenAI`
  2.9.0-beta.1 throws when it is called: `MissingMethodException: Method not found: 'Void
  OpenAI.Responses.ResponsesClient..ctor(System.ClientModel.Primitives.ClientPipeline, OpenAI.OpenAIClientOptions)'`.
  Chat Completions needs no hold: on `OpenAI` 2.14 its client is built where the Responses one throws (BUG-120,
  S2).
- **Breaking — MentorAgent:** the built-in history keeps tool calls and their results. Up to rc.14 it was
  trimmed by `MessageCountingChatReducer`, which drops every message carrying a function call or result: from
  the next turn on, the model no longer saw the data a tool had read, and answered a follow-up by guessing or by
  calling the tool again. The default is now `MentorChatReducer`: it counts every message, tool calls and
  results included, against `MaxSessionMessages`, trims whole turns, oldest first, and never sends a result
  without the call that produced it. In a lab run of 84 turns the turns answered correctly went from 85% to
  95%, and tokens grew by about 9%. With the same `MaxSessionMessages` a session holds fewer turns (BUG-121,
  S2). **Migration:** nothing to change. For the rc.14 behaviour set
  `options.ChatReducer = new MessageCountingChatReducer(50)` (experimental, `MEAI001`).
- **Breaking — MentorAgent:** the options are validated when the host starts (`ValidateOnStart`). An ASP.NET
  Core or worker host with an invalid configuration (no `ChatClient` or `Agent`, empty `ScanAssemblies`, both
  `ChatHistoryProvider` and `ChatHistoryProviderFactory`…) now stops at start with the reason. Up to rc.14 it
  started, listened and failed on the first request, although the READMEs said "throws at startup". A MAUI app,
  which has no such start, still gets the error the first time MentorAgent is used (BUG-123, S4).
  **Migration:** a host that started with an invalid MentorAgent configuration fixes it.
- **Breaking — MentorAgent:** a class listed in `[TeamMember(Tools = ...)]` is that member's toolbox only. Up to
  rc.14 the scan also made its methods coordinator actions, published on `/mcp` and on the A2A card, because
  the class is in DI as the README asks (BUG-125, S3). A `[MentorSkill]` class keeps its coordinator tools, by
  the skill's own role. Member tools are also discovered by the same rules as every other action: public
  instance methods declared on the class itself, with `[MentorAction]` or a non-empty `[Description]`. A
  member no longer gets inherited or static methods, or a method with an empty `[Description]` (BUG-126, S3).
  **Migration:** at startup an Information line names each class taken from the coordinator, the members
  that list it and its methods (`… is listed in the Tools of …`). An action the coordinator must offer too
  goes on a class not listed in `Tools`; a member tool inherited from a base class is declared on the listed
  class.
- **Breaking — MentorAgent.Server:** `POST /mentor/approve` answers `404` when no turn waits for the answer any
  more: it was answered already, the turn was stopped, or the connection that raised it has closed. Up to rc.14
  every call got a `200`, so a client could not tell an approval that ran from one that reached nothing.
  **Migration:** a client that treats any non-2xx answer as an error treats `404` as "nothing left to answer"
  and closes its dialog. The WebAssembly client of MentorAgent.Blazor already does.
- **Breaking — MentorAgent:** `IMentorStateService.OnApprovalResponse` is no longer raised for an answer that
  found no turn waiting: given after Stop, after the connection or the circuit closed, or a second time. Up to
  rc.14 it was, in process and in the WebAssembly client, so a custom confirmation dialog that closed only on
  this event closed in those cases too; now it stays open (BUG-129). **Migration:** close a custom dialog
  when its buttons are clicked and on `OnBusyChanged(false)`, as the README's *Custom HITL confirmation
  dialog* example does; keep `OnApprovalResponse` for an audit trail of the decisions that were acted on.
- **Breaking — MentorAgent:** on the hub, on `/mentor/chat` and over A2A, MentorAgent takes the caller from the
  request — the hub connection's user, or `HttpContext.User` — before asking the host's
  `AuthenticationStateProvider`, which now answers only where no MentorAgent transport runs, such as a Blazor
  circuit (BUG-131). A host whose own provider added claims or roles to the user no longer has them seen
  there. **Migration:** add them with an `IClaimsTransformation`, which ASP.NET Core applies to
  `HttpContext.User` and so to the hub connection's user too.
- **Breaking — MentorAgent:** memory and `RateLimitPerUser` key a signed-in user by `NameIdentifier`, as
  before, else by `sub`, `oid` or the name with the claim in the key (`sub:…`, `name:…`), like the owner checks
  (BUG-142). A user known by name alone in rc.14 (Windows authentication) gets a new key. **Migration:** in
  your memory store, rename those users' keys from `<name>` to `name:<name>`.
- **MentorAgent:** README — `ReadOnly` and `IsReadOnlyAction` say what `readOnlyHint` means on `/mcp`: some MCP clients
  skip their own confirmation for such a tool, so a write declared read-only by mistake runs from them with nobody
  asked. The same in the MentorAgent.Server README's MCP section.
- **MentorAgent:** README — `OnActionExecuting` carries the text shown to the user (the action's `Description`, or a
  routing, team or hosted-tool label), not the tool name; `OnActionCompleted` carries the tool name, `OnActionFailed`
  the tool name and the error. The same in the MentorAgent.Server (`ActionExecuting`) and MentorAgent.Blazor READMEs.
- **MentorAgent:** README — a tool result is a card by its shape, not by its .NET type: an object with a non-empty
  string `kind`, or an array of them. Records of your own with a `kind` property are drawn as cards; the README says
  how to avoid it.
- **MentorAgent:** README — the banner of the five READMEs says this is a release candidate for 1.0.0, not a
  public preview. The *Extensibility* table lists `IMentorAgentSource`, `IMentorQueryEmbedding`,
  `ClassifierChatClient` and `SkillSources`/`SkillFilter`, and its history-provider row points to
  `ChatHistoryProviderFactory`: `ChatHistoryProvider` is one instance shared by every user.
- **MentorAgent:** `MentorOptions` XML documentation matches the code: `RequiresApproval` gets tool names
  (MCP ones unprefixed, never a remote agent), `DashboardRole` guards the metrics endpoint only, routing per
  `RoutingStrategy`, `AgentCardUrl` is a base URL (`agent-card.json`), no `ITextSearch` wrapper, an English
  `RagSystemPromptTemplate`.
- **MentorAgent.Server:** the A2A comments say a task runs as the request's user; an anonymous caller is not held
  by `RateLimitPerUser` across tasks.

### Removed

- **Breaking — MentorAgent:** the `MENTOR001` build check (`buildTransitive/MentorAgent.targets`) and its
  `MentorAgentSkipOpenAIVersionCheck` switch. They stopped every build that resolved `OpenAI` 2.11 or later — a
  project on the current Agent Framework, which never calls `GetResponsesClient()`, included (BUG-120).

### Fixed

- **MentorAgent:** README — the Requirements table lists the package's actual references, and its Blazor row
  no longer contradicts the server package (any ASP.NET Core app with MentorAgent.Server). The MENTOR001 section
  is replaced by *Provider package versions*, which says which call fails and on which versions instead of "the
  process dies before serving a request".
- **MentorAgent:** the warm-up (`WarmUpAtStartup`) is registered always and decided on the real options when
  the host starts. It was registered only if the registration-time snapshot asked for it, and that snapshot
  swallowed whatever the `AddMentorAgent` callback threw: a callback that failed before
  `WarmUpAtStartup = true` lost the warm-up without a word. Such an exception is now logged as a warning at
  start, naming the options decided at registration (BUG-122, S3).
- **MentorAgent:** the warm-up starts on `ApplicationStarted`, after a web host is listening, as its
  documentation said. It started inside `StartAsync`, before the other hosted services.
- **MentorAgent:** README — *When the options are read and checked*: the `AddMentorAgent` callback runs twice,
  and `EnableObservability`, `ObservabilitySourceName`, `McpServerEnabled`, `A2AServerEnabled`, `AppName`,
  `AppDescription` and `AgentCard` are read at registration, so a `Configure<MentorOptions>` registered later
  does not reach them (BUG-124, S3). The same note is in the MentorAgent.Server README and in the XML
  documentation of `AddMentorAgent`.
- **MentorAgent:** a team member is told *"These tools are read-only."* only when every one of its tools is
  declared read-only (`ReadOnly = true` or `IsReadOnlyAction`) and none asks for confirmation. Every member
  was told so, whatever its tools did (BUG-125).
- **MentorAgent:** README — *Which methods become tools*: inherited and static methods are not discovered, and
  a method with `[Description]` alone has no roles and no confirmation, so with tool filtering it can be sent
  when a message matches nothing (BUG-127, S4). The same in the MentorAgent.Server README.
- **MentorAgent.Server:** a confirmation outlived the connection that raised it. When the owner's hub
  connection closed, the hub forgot who had raised it but the turn kept waiting: anybody who knew the action id
  could approve it with `POST /mentor/approve`, with no owner left to check, and the action ran. Now the end of
  a connection — and of a Blazor Server circuit — cancels the turn running in it, with its confirmation
  (BUG-128, S2).
- **MentorAgent:** `OnApprovalResponse` is raised only when the answer reached the turn that was waiting for it.
  After Stop the request stayed pending, so a late Confirm raised `OnApprovalResponse(…, true)` for an action
  that never ran — in process, and in the WebAssembly client, which raised it before sending the answer. Now
  the request leaves the pending list with its wait, and the WebAssembly client raises the event once the
  server has accepted the answer (BUG-129, S3).
- **MentorAgent.Server:** the owner of a connection is recognised without a `NameIdentifier` claim.
  `/mentor/approve`, `/mentor/cancel` and `/mentor/session` read `NameIdentifier` alone, and so did the key that
  binds a session snapshot to its owner: with Windows authentication, or JwtBearer with
  `MapInboundClaims = false`, the owner was refused (`403`) its own confirmations, Stop and session. Now they
  read `NameIdentifier`, then `sub`, then `oid`, then the identity's name, as the MCP rate limit does. A name
  never matches another user's identifier. Snapshots saved by rc.14 for a user with `NameIdentifier` still
  restore (BUG-130, S3).
- **MentorAgent.Server:** in a Blazor Web App the hub, `/mentor/chat` and A2A know who is calling. MentorAgent
  read the user from the host's `AuthenticationStateProvider`; with `AddRazorComponents()` that is Blazor's,
  which throws outside a circuit, and the server's bridge, registered with `TryAdd`, lost to it. Every
  `RequiredRoles` action was refused to everyone with an error logged on each call, and memory and
  `RateLimitPerUser` followed a connection instead of a person. A Blazor Server app serving A2A without the hub
  had no bridge at all. Now each transport records its caller and MentorAgent reads it first; the host's
  registration is left as it is, whatever the order of `AddRazorComponents()` and `AddMentorAgentServer()`.
  When the server's own provider is the one registered, it also takes a circuit's user from Blazor
  (`IHostEnvironmentAuthenticationStateProvider`), as Blazor's own does (BUG-131, S2).
- **MentorAgent:** a signed-in user with only `sub` or `oid` (JwtBearer with `MapInboundClaims = false`) was
  keyed per session, so memory and `RateLimitPerUser` did not follow the person (BUG-142, S3).
- **MentorAgent.Server:** README — *CORS* and the JWT example under *Authentication* show the whole pipeline:
  `UseCors` → `UseAuthentication` → `UseAuthorization` → `Map…`. A `Program.cs` that protected the hub and did not
  call `UseAuthentication()` and `UseAuthorization()` itself got them from ASP.NET Core ahead of `UseCors`: the
  browser's preflight for `/mentor-hub/negotiate` was answered `401` without CORS headers, and a client on another
  origin never connected (BUG-132, S2). The MentorAgent.Blazor README says the same under *Stop and confirmations*.
- **MentorAgent.Server:** README — the complete React, Angular and Vue components send the hub's identity on
  approve, cancel and session (a `getToken` function, or cookies with `credentials: 'include'`), and show an error
  when the server refuses an answer or a Stop. They sent a bare `fetch`: with an authenticated hub the server
  answered `401`, the dialog closed and the turn stayed waiting, with nothing on screen (BUG-133, S2). The Angular and
  Vue components also take an absolute hub URL (`MENTOR_HUB_CONFIG`, `useMentorHub(hubUrl, getToken)`) and send
  approve and cancel to the hub's origin.
- **MentorAgent.Server:** README — the three components free the UI when the connection drops (`onreconnecting`,
  `onclose`) and restore the saved conversation in `onreconnected`; the events table lists the three callbacks. A
  connection lost mid-turn left the UI busy, even after the client reconnected: the new connection never sends
  `BusyChanged(false)` for that turn (BUG-134, S3).
- **MentorAgent.Server:** README — the complete React component shows the assistant's replies. It added an empty
  bubble for every reply: it reset the streaming buffer before React ran the update that read it (BUG-136, S2). The
  three components no longer add an empty bubble when a segment closes before a confirmation.
- **MentorAgent.Blazor:** README — the client setup example has `using MentorAgent.Abstractions.Models;`. Without
  it `MentorLanguage` and `MentorTheme` did not compile (`CS0103`) (BUG-135, S3).
- **MentorAgent:** README and XML documentation — `RequireConfirmation = false` turns off the confirmations of your
  actions, MCP-client servers and `RequiresApproval`, not the approvals a hosted MCP server asks for
  (`RequireApproval`, `AlwaysRequireApprovalTools`). The README and the XML documentation said it disabled every
  confirmation dialog (BUG-137, S3).
- **MentorAgent:** README — voice input: your server receives only the transcript, but some browsers, Chrome among
  them, send the audio to their maker's speech service. The README said nothing left the page except the transcript
  (BUG-138, S3). The same in the MentorAgent.Blazor README.
- **MentorAgent:** with `HitlMode.Blocking`, Confirm ran a `RequiredRoles` action for a user who had lost the role
  while the banner waited: the roles were read only before the banner. They are read again after the answer, as in
  Native mode, also in specialists and teams (BUG-143, S3).
- **MentorAgent:** after Cancel on a specialist's confirmation, reached through `route_to_specialist`, the answer
  could report a problem that never happened: the coordinator got only the specialists' words, and a team with
  `HandoffTo` had just told it to execute. The delegation's result now starts with `DECLINED BY THE USER`, names the
  declined action and any that ran beside it, and carries the rule of a declined action; an approved team's result
  says a declined confirmation is the user's decision (BUG-144, S3).
- **MentorAgent:** README — Blazor Web App: `@rendermode="InteractiveServer"` on `<ChatWidget />`, the simplest
  setup offered for every Auto app, needs *Interactivity location: Per page/component*. With *Global* it throws
  `NotSupportedException` once the layout runs in WebAssembly, and the WebAssembly template fails with HTTP 500 until
  its server adds `AddInteractiveServerComponents()` and `AddInteractiveServerRenderMode()`. The README also sent the
  CSS/JS links to an `index.html` a Web App does not have: prerendered, the widget writes them itself; otherwise they
  go in `App.razor` (BUG-140, S2). The same in the MentorAgent.Blazor README.
- **MentorAgent.Blazor:** README — the client project of a Blazor Web App registers no `HttpClient`, and without one
  the WebAssembly runtime did not start (`CannotResolveService`); the README said the template's was enough. The
  client snippets now register one (BUG-139, S2). The same in the MentorAgent and MentorAgent.Server READMEs.
- **MentorAgent.Blazor:** in WebAssembly a relative `HubUrl`, the default `/mentor-hub` among them, never reached
  the server: the .NET SignalR client parsed it as `file:///mentor-hub` and the widget did not connect (`TypeError:
  Failed to fetch`). It is now resolved against the page's base address, as a browser does (BUG-145, S2).
- **MentorAgent:** a `[MentorTeam]` stopped discussing after its first activations in a session: every run shared
  one group-chat manager, whose iteration count was never reset, so once `MaxIterations` was spent the team was
  activated with no member speaking and returned nothing. Each run now gets its own manager (BUG-146, S2).
- **MentorAgent:** a tool that threw showed the exception's text in the chat even with `OnException` set, and a
  failure after the action (`OnToolResult`, the cards, a screen update) reported an action that had run as failed,
  so the model could run it again. Only the tool's own exception is a failure now. With `OnException` set the chat
  shows the app's message or a localized "The action could not be completed.", never the exception's text; the
  model still gets the app's message, else the exception's. An `OnToolResult` that throws withholds the result, as
  on the MCP server. A Stop during the action is not logged as a failure. The widget no longer shows the text of a
  send that failed (BUG-147, S2).
- **MentorAgent:** the gate's Warnings named the user and carried a tool's whole exception. They name a fingerprint
  (`caller#…`, the same as the MCP audit line) and the exception's type; the whole exception is logged at Debug
  (BUG-148, S3).
- **MentorAgent:** with a `Language` other than English the input box of a server-hosted widget still read "Type
  a message...": `MentorOptions.InputPlaceholder` defaulted to that text and hid the translation. It is now
  `string?` with no default, as in MentorAgent.Blazor, and an unset placeholder uses the localized one. In
  English the default text is now "Write a message..." (BUG-149, S3).
- **MentorAgent.Abstractions:** the widget's stylesheet loaded Sora and JetBrains Mono from Google Fonts, a
  request to a third party on every page that rendered the widget, refused under `default-src 'self'` and absent
  offline. The two fonts now ship in the package (`wwwroot/fonts`: variable WOFF2, Latin and Latin Extended, SIL
  Open Font License 1.1), which is about 110 KB larger, and the stylesheet requests nothing from another origin.
  The avatar's size and a table column's alignment moved from style attributes to the stylesheet, so with the
  default options the widget needs no `style-src 'unsafe-inline'`; `Theme` Dark or Minimal and `PrimaryColor`
  still write a `<style>` block, and `MentorDashboard` uses style attributes (BUG-150, S2).
- **MentorAgent:** the error for a missing model suggested `new OllamaChatClient(...)`, a type in no package
  the library brings, and `AIProjectClient` without its package. Each example now names its package; Ollama
  goes through its OpenAI-compatible endpoint (BUG-151, S3).
- **MentorAgent.Declarative:** README — the install command lacked `--prerelease`, which `dotnet add package`
  needs while only prereleases exist; so did the two `MentorAgent.Server` commands in the MentorAgent README.
  The *License* sections point to the package's `LICENSE` (BUG-141, S3).

### Known issues

- **MentorAgent:** BUG-074 (S3): with `MentorshipLevel.Proactive`, the assistant can still offer next steps the
  application does not have — 3 in 15 turns on the Blazor Server sample after the rc.14 rule, 5 before ("add it
  to the cart", "the available deals", "plan a reorder"). Left open for 1.0.0 by the product owner's decision:
  the closing offers keep their current wording.
- **MentorAgent:** BUG-118 (S2) is closed for hosts that opt in to `RequireActionForChangeRequests` (added in
  1.0.0-rc.14). Without it the model can still narrate an action it never performed — "Stock aggiornato" with no
  tool called, or "Ecco la pagina Prodotti" with no navigation — and with it a complete request the classifier
  does not recognise runs as without the option.
- BUG-106's residue is closed for hosts that opt in to `RequireLookupForRecordQuestions` (added in 1.0.0-rc.14);
  without it, a catalogue question can still be answered with an offer to look the value up.
- **MentorAgent:** with `HitlMode.Native`, a confirmation closed by Stop is closed only in memory: a snapshot saved
  in that state and restored still carries the open request, which can make the next turn fail as in BUG-116.

## [1.0.0-rc.14] - 2026-10-03

### Added

- **MentorAgent:** `[MentorAction(ReadOnly = true)]` and `MentorOptions.IsReadOnlyAction` (by tool name, for
  `[Description]` methods) declare the actions that only read. MentorAgent takes the host's word for it: such an
  action is published to MCP clients with `readOnlyHint`. A confirmation-gated action is never read-only.
- **MentorAgent:** `MentorOptions.RequireLookupForRecordQuestions`, off by default (BUG-106, the residue). When a
  classifier model says a message asks for a value of the application's records, the first model call must call
  a declared read-only action that the tool filter selected for that message, or hand the request to a
  specialist (`ChatToolMode.RequireAny`). One classifier call (`ClassifierChatClient` when set, bounded by
  `SafetyCheckTimeout`) per turn whose selection holds a declared read — about one second of median latency
  on the ApiServer sample, two to three where a read now happens that did not; a classifier that fails or
  answers anything else leaves the turn as without the option. Measured on the ApiServer sample (with its
  rc.14 changes: products looked up by name, the seed's IDs) against the previous build and samples: the
  Dell's price from a read 19/19 against 17/20, the iPad Air stock 20/20 against 19/20, a product that does
  not exist answered without an invented figure 10/10 on both. Needs `EnableToolFiltering`, an `EmbeddingGenerator` and
  declared reads; a startup warning says when it has no effect.
- **MentorAgent:** `MentorOptions.RequireActionForChangeRequests`, off by default (BUG-118). When the classifier says
  a message asks to carry out one of the confirmation-gated actions the tool filter selected for it, and gives every
  input that action needs, the first model call must call one of them, or hand the request to a specialist or a
  team — so the confirmation banner comes before any sentence; when it says the message asks for a page, the first
  call must be `navigate_to`. The classifier is shown each action's required inputs: a request that leaves one out
  ("Annulla l'ordine 1004" when `cancel_order` needs a reason), only describes the entry ("the last order"), gives a
  name where the input is an ID, or needs the current value ("add 5 units") is not forced, and the assistant may ask.
  Only actions that will show a banner are ever required (declared confirmation, with `RequireConfirmation` on; with
  `HitlMode.Native` no `AutoApprovalRules` rule is consulted for the required call), and never one whose input
  schema cannot be shown faithfully (a `$ref`, a combination of schemas, parameters none of which is marked
  required). A page request is matched against the application's `[MentorPage]` pages; a request from another agent
  is never required to navigate. One classifier call per turn while the application has
  a page or the message selected such an action — the same call as `RequireLookupForRecordQuestions` when both are
  on. Classifier measured on Azure gpt-4.1 before release, three repetitions per message: with the two actions a
  stock or price request usually selects listed, 207/207 on 69 development messages and 60/60 on 20 written after
  the last change; with all nine of the ApiServer's gated actions listed, 204/207 and 58/60, the misses complete
  requests left unforced; an incomplete request or a read question was never labelled ACTION. Live on the ApiServer sample (3 Oct), the
  same build with the option off and on, alternating: asked "Imposta lo stock dell'iPad Air a 30", the assistant
  said it was done with no tool called 11 times in 25 with the option off, and showed the banner 25/25 with it on
  (15/15 on the final build); "Portami alla pagina prodotti" navigated 5/10 off, 10/10 and 8/8 on; requests missing
  a value were asked about every time; reads and greetings unchanged. Needs `EnableToolFiltering`
  and an `EmbeddingGenerator`; a startup warning says when it has no effect or covers page requests only.

### Security

- **MentorAgent:** BUG-103 (S1): with `McpCallerPrincipal` set and roles in a claim type other than
  `ClaimTypes.Role` (a `roles` claim, as Entra ID tokens and JwtBearer with `MapInboundClaims = false`
  carry), a signed-in caller with no roles listed and called a role-gated action once an entitled caller
  had asked first. The tool list was cached per caller under a key built from `ClaimTypes.Role` claims,
  while the decision used `IsInRole`. The list is now decided on every request, with the resolver asked
  once and `ClaimsPrincipal.IsInRole` as the only test, for `tools/list` and `tools/call` alike.
- **MentorAgent.Server:** BUG-110 (S3): `GET /mentor/session` handed the client the conversation in
  clear; an edited snapshot was restored as history, and another user's snapshot restored into one's own
  connection. The snapshot is now protected with ASP.NET Core Data Protection under a purpose naming the
  connection's user: opaque, tamper-evident, and restorable only for that user (on a host without
  sign-in every visitor is the same anonymous user). `POST` checks the owner before reading the body.

### Changed

- **Breaking — MentorAgent:** `MapMentorAgentMcp()` no longer attaches the `MentorAgentMcp` CORS policy
  (BUG-108). MCP clients that are not browsers need none. For a browser-based client call `app.UseCors()`
  and `MapMentorAgentMcp(configure: e => e.RequireCors("MentorAgentMcp"))`; a host that replaced that
  policy as the rc.13 README suggested needs the `configure` line too. A `MentorAgentMcp` policy the host
  registers before `AddMentorAgent` is no longer replaced by the any-origin one.
- **Breaking — MentorAgent.Server:** the `/mentor/session` body is opaque (see Security). Snapshots saved
  by clients before rc.14 are refused with `400`: save the conversation again. Restoring after a restart
  or on another instance needs the Data Protection key ring persisted and shared.
- **MentorAgent:** the classifier behind `RequireLookupForRecordQuestions` is asked at temperature 0, the
  temperature its wording was measured at, like the other classifiers; it ran at the provider's default. No
  difference was measured on the rc.14 messages (141/141 at either temperature). With reads alone it is asked the
  rc.14 question, unchanged; `RequireActionForChangeRequests` (see Added) adds its labels only when that option
  is on. The turn that answers an approval a Stop left open (BUG-116) is no longer forced: the stopped action is
  declared there for its answer, a required call could be satisfied by calling it again, and on a message that
  matched nothing the carried action was left out of the forced request while its answer was in it.
- **MentorAgent:** tool filtering: when no action clears `ToolFilterMinScore`, the three best-ranked
  actions that need no confirmation and no role are sent anyway (an MCP tool only when its server marks it
  `readOnlyHint`), within `ToolFilterMaxTools`. About 80 input tokens per model call on such turns,
  greetings included. Give every action that changes data `RequiresConfirmation` (BUG-106).
- **MentorAgent:** the coordinator's prompt states record values only from a tool, the page data or the
  documents, and the web-search capability no longer names prices (BUG-106).
- **MentorAgent:** `RateLimitPerUser`, `OnToolResult` and the dashboard's top actions now cover MCP
  `tools/call` (BUG-107). MCP calls have an allowance of their own, separate from chat messages, per
  `NameIdentifier`, `sub` or `oid`; a caller with none is counted by remote address when
  `IHttpContextAccessor` is registered, otherwise all such callers share one. Each call writes one
  Information line with a fingerprint of the caller.
- **MentorAgent:** `RefuseOutOfScope` refusals count in `GuardrailBlockCount` and
  `mentoragent.guardrail_blocks`. With `RefuseOutOfScope` and the safety check on, the hosted-tool scope
  check asks its own question on turns where a hosted tool is about to be declared (BUG-105).
- **MentorAgent:** compaction is not installed with `UseServiceManagedHistory` (it had nothing to
  compact). A reset of the conversation writes one Information line.
- **MentorAgent.Abstractions:** the widget's "New conversation" button is disabled from the moment a
  message is sent until its reply ends, and while a confirmation is pending; a confirmation banner still
  up when the turn ends (Stop) is dismissed.
- **MentorAgent:** A2A: a navigation, a UI action, a memory write or a skill no longer counts as grounding,
  so such a reply is put once more like a tool-less one (BUG-106).
- **MentorAgent:** tool filtering: a message that matches no action carries the actions the previous message
  selected — one message back only, never what was itself carried, a confirmation-gated action never alone. The
  answer to the assistant's own question ("Sì, procedi", the reason it asked for) used to arrive without the
  action the question was about (OSS-01). A new or restored conversation carries nothing.
- **MentorAgent:** `MentorshipLevel.Proactive`: a next step may be offered only when a tool of the current
  request, a page, a skill, a specialist or a team can carry it out — otherwise none, and never an export, an
  e-mail, a ticket, a reminder or an alert, or parcel tracking (BUG-074). With tool filtering on, the prompt no
  longer presents the number of actions as the tools at hand.

### Fixed

- **MentorAgent:** BUG-104 (S1): with `EnableCompaction` on and a local history (the built-in provider,
  `ChatHistoryProviderFactory` or `ChatHistoryProvider`), the question of every turn that called a tool
  or ran a UI action was not stored — the Agent Framework's compaction marked it as history in place
  (microsoft/agent-framework#8441) — and stored messages were stamped with its attribution. Compaction now
  works on copies. Questions lost on earlier versions cannot be recovered from their snapshots.
- **MentorAgent:** BUG-105 (S2): `RefuseOutOfScope` refused price questions about the application's own
  catalogue (4 of 4 measured). It judged messages against the hosted-tool cost rule and saw page names
  only. It now asks a question of its own, which defaults to answering, with the pages' descriptions; the
  classifier's labels are read by position (`Contains("OUT")` matched "about" and "without").
- **MentorAgent:** BUG-106 (S2): a catalogue question no action matched was sent with the core tools only,
  and the reply invented a price range or said the catalogue lacked it. See Changed.
- **MentorAgent:** BUG-107 (S2): MCP `tools/call` skipped `OnToolResult`, the per-user rate limit and the
  tool metrics: a host that redacts results served them unredacted to MCP callers. A hook that throws now
  withholds the result (`isError`).
- **MentorAgent:** BUG-108 (S2): the README's MCP steps, followed as written, answered 500 to every
  request on a pipeline without `app.UseCors()`. See Changed.
- **MentorAgent.Blazor:** BUG-109 (S3): "New conversation" before the first message on WebAssembly raised
  the Blazor error bar (the hub was not connected yet).
- BUG-110 (S3): `/mentor/session` and `RestoreSessionAsync`: a non-integer marker answered 500; a
  snapshot saved by a host that keeps its history another way (Responses vs Chat Completions, another
  history provider) was accepted, then failed every turn or restored empty; a restored conversation the
  service no longer knew failed every turn until a reset — now, when the first turn after the restore
  fails because the provider says it does not know the conversation, a new one starts (a content filter, a
  missing deployment, a 429 or a 5xx keep it). WebAssembly's `NotSupportedException` points to
  `/mentor/session`.
- **MentorAgent.Server:** BUG-111 (S3): the package README now carries BUG-102's rule (keep the
  transcript in the session) with its recipe, and no longer says a reset clears history.
- **MentorAgent:** BUG-114 (S3): over A2A, the judge of a second reply without data withheld policies that
  contain a number ("meals up to 50 €") as `NOT GROUNDED`, and passed "the catalogue does not list the price"
  (nobody looked). Measured on Azure gpt-4.1, fourteen replies × 3: 25/42 before, 42/42 now.
- **MentorAgent:** BUG-115 (S2): after the user declined a confirmation the reply could invent why the action had
  not run ("the order is probably already being processed", for an order that was Pending). The localized text
  said "the operation was cancelled" — for a request to cancel an order — and the Native flow sent no instruction
  at all. Both modes now send the same structured result, stating that nothing was checked or changed and that no
  other reason or record state is to be given; `confirm.denied` no longer uses the verb for cancelling (10
  languages).
- **MentorAgent:** BUG-116 (S2): with `HitlMode.Native`, Stop pressed while a confirmation waited made every
  later turn fail (`ToolApprovalRequestContent found … no matching ToolApprovalResponseContent`) until "New
  conversation". The open request is now answered, as not run, at the start of the next turn; no extra model
  call. Also covered: a second Stop, or a failure, before that answer is stored; a run that fails after the user
  confirmed (the action may have run: reported as of unknown outcome, never run again); the approval-round cap.
  On that turn the tool filter and the router decide on the new message. A snapshot saved in that state and
  restored, here or elsewhere, still carries the open request.
- **MentorAgent:** BUG-117 (S2): a provider error that arrived inside the stream — a 429 on the Responses
  API, a hosted tool that failed — on a run that wrote no text ended the turn with nothing on screen: no reply,
  no `Error` event. It now takes the path of a thrown provider error (the localized message, `OnException`, the
  error metrics). Text written after the last tool call is kept with no error under it; a sentence written
  before a tool call is not an answer. A model refusal reads as a block (`guardrail.blocked`), not as an error.

### Known issues

- **MentorAgent:** BUG-074 (S3): with `MentorshipLevel.Proactive`, the assistant can still offer next steps the
  application does not have — 3 in 15 turns on the Blazor Server sample after the rc.14 rule, 5 before ("add it
  to the cart", "the available deals", "plan a reorder").
- **MentorAgent:** BUG-118 (S2) is closed for hosts that opt in to `RequireActionForChangeRequests` (see Added).
  Without it the model can still narrate an action it never performed — "Stock aggiornato" with no tool called,
  or "Ecco la pagina Prodotti" with no navigation — and with it a complete request the classifier does not
  recognise runs as without the option.
- BUG-106's residue is closed for hosts that opt in to `RequireLookupForRecordQuestions` (see Added); without
  it, a catalogue question can still be answered with an offer to look the value up.

## [1.0.0-rc.13] - 2026-09-30

### Added

- THIRD-PARTY-NOTICES.md in every package: the notices for the Feather (MIT) and Lucide (ISC) icons
  inside MentorAgent.Abstractions' components, which were missing.

### Changed

- Tool filtering (`EnableToolFiltering`): when every action above `ToolFilterMinScore` needs
  confirmation (`RequiresConfirmation`, the `RequiresApproval` predicate, or an approval-required MCP
  server), the three best-ranked actions that do not need it are sent with them, within
  `ToolFilterMaxTools`. A lone match that needs no confirmation is still sent alone. This is the
  structural fix for BUG-099, below.
- Each package's release notes now come from this changelog, and every package contains it.

### Removed

- **Breaking:** `MentorRemoteAgent.RequiredRoles`. It was never enforced: setting it only logged a
  warning, and any user could reach the agent. To restrict a remote agent, authorise on the remote side —
  protect the peer's `/a2a` endpoint and give its actions roles — or do not configure the agent on hosts
  whose users may not use it.
- **Breaking (dependencies):** MentorAgent no longer depends on `Microsoft.Agents.AI.Foundry` and
  `Azure.AI.Projects`, which the library never used. To keep using `AIProjectClient` or `FoundryEvals`,
  add `<PackageReference Include="Microsoft.Agents.AI.Foundry" Version="[1.17.0-preview.260804.1]" />`
  to your project; a newer version pulls OpenAI 2.11 and the build stops with `MENTOR001`.

### Fixed

- BUG-099 (S1): with `EnableToolFiltering` on, a read question could reach the model with a lone write
  action, and a question about stock set the stock to 0. 1.0.0-rc.12 mitigated it with confirmation on
  writes; now a confirmation-gated action never reaches the model alone.
- BUG-101 (S3): an MCP `tools/call` naming a tool the server does not list — a confirmation-gated action
  it withholds, or a name that never existed — was logged as an unhandled exception with a stack trace,
  and the caller got "An error occurred invoking '…'". It is now answered "Unknown tool: '…'" with
  `isError`, the same for both kinds of name, and nothing is logged as an error.
- BUG-102 (S3): a custom history provider that kept the transcript in a field carried the previous
  conversation into a new one (`ResetSessionAsync`, the widget's reset button, the hub's `ResetSession`),
  because the provider lives as long as the circuit or connection. The README now says to keep the
  transcript in the session, with a `ProviderSessionState<T>` recipe. The built-in provider was not
  affected.

## [1.0.0-rc.12] - 2026-09-23

### Added

- **MentorAgent:** `MentorOptions.ToolFilterMinTools` (default `0`, which keeps tool selection exactly as in rc.11). With `EnableToolFiltering` on, once one action clears `ToolFilterMinScore`, at least this many of the best-ranked actions are sent, whatever their score. It is opt-in because it cuts both ways: a floor can stop a lone write action from being offered for a read question, but it can also add a candidate the model then wrongly prefers (see BUG-099 under Known issues).

### Fixed

- **MentorAgent:** BUG-100 (S2): a confirmation-gated action (`RequiresConfirmation`, or the `RequiresApproval` predicate) called during an A2A task held the task open until the calling agent gave up, because both the blocking and the native approval path waited for an answer nobody could give. Such an action is now refused at once in both HITL modes, and the model is told it has to be performed from the application itself. The A2A agent card already left these actions out.
- **MentorAgent:** BUG-098 (S2): on a turn served without a Blazor circuit (for example an A2A task on a Blazor Server host), an action with `NavigateTo` ran, the automatic navigation then threw while reading `NavigationManager.Uri`, and the action was reported as failed — so the model ran it again and the caller was told it had not happened. A turn from another agent no longer auto-navigates, and a page URL that cannot be read no longer turns a completed action into a failure. An exception thrown by your own `OnToolResult` hook still reports an action that already ran as failed, so keep that hook from throwing.
- **MentorAgent:** BUG-097 (S3): on a Blazor Server host with memory or `RateLimitPerUser` on, every A2A task logged a warning that the `AuthenticationStateProvider` threw, and a role-gated tool the model reached for was logged as an Error with a stack trace and answered "Error during authorization check". The provider is still asked, so a headless host keeps an authenticated peer's identity; when it cannot be read on a task from another agent, the task is treated as anonymous (logged at Debug) and a gated tool is refused as for any anonymous caller.

### Known issues

- **MentorAgent:** BUG-099 (S1, mitigated; present since tool filtering was introduced): with `EnableToolFiltering` on (off by default), the filter sends only the actions whose score clears `ToolFilterMinScore`, so a read question can end up with a single write action above the line — observed as a question about stock levels answered by calling the stock-update action with a quantity of 0. Mark every action that changes data with `[MentorAction(RequiresConfirmation = true)]`: a person is asked first, and over A2A the action is refused (BUG-100). `ToolFilterMinTools` is an additional, opt-in lever. A structural fix (declaring actions read-only) is planned for a later version.
- **MentorAgent:** BUG-074 (S3): with `MentorshipLevel.Proactive`, the assistant can offer next steps the application does not have.

## [1.0.0-rc.11] - 2026-09-23

### Added

- **MentorAgent:** `MentorOptions.SafetyCheckTimeout` (default 15 seconds; zero or negative means no limit, and Stop still cancels the check) and `MentorOptions.SafetyCheckFailure` (`MentorSafetyCheckFailure.Allow`, the default and the previous behaviour, or `Block`, which refuses the message with a localized "can't check it right now, try again" or withholds the reply). Both apply to the input and the output check, built-in or custom; a timeout logs one line naming what to change. Nothing changes for a host that sets neither option (BUG-084).

### Changed

- **MentorAgent:** `MapMentorAgentMcp()` and `MapMentorAgentA2A()` now take their default path from `McpServerPath` and `A2AServerPath` (the `path` parameter is now `string? path = null`); previously nothing read those options (BUG-091, S3). If you set either option to a non-default value and map the endpoint without an explicit path, the endpoint now moves to the configured path.
- **MentorAgent:** the output safety check now runs on `ClassifierChatClient`, like the input check, and is metered under that client's model id (BUG-096, S3).

### Fixed

- **MentorAgent:** BUG-082 (S2): on a host with remote A2A agents, a local specialist's answer could reach the user presented as the remote agent's, with no remote call made. Every delegated answer now states its source to the coordinator: a local specialist (with a note that no remote agent was contacted, and an instruction to call once more with `specialist` set if the user meant one), the remote agent ("that system's data, not this application's"), or a named remote agent that never took part ("NOT X'S ANSWER", plus a warning for the operator). Every configured peer counts as remote, including one whose card could not be fetched when the session was built, and the router's list marks remote entries. Hosts without remote agents, and turns that arrived over A2A, are unchanged.
- **MentorAgent:** BUG-083 (S2, present in rc.10): a Blazor Server host could not answer another agent — every A2A task it received completed with an empty message, because a turn served over A2A has no circuit and reading `NavigationManager.Uri` threw. The read is guarded, and the A2A handler now fails a task that produced no text instead of completing it empty. Headless hosts (MentorAgent.Server) were not affected.
- **MentorAgent:** BUG-084 (S2): the safety checks had a fixed 15-second limit and failed open, so on a slow model the input check was skipped on every message; Stop could not interrupt the input check; and a custom `InputGuardrail` that honoured its cancellation token threw out of `SendMessageAsync`, leaving the widget busy for good. See the new options above; an error now keeps a single stack trace per turn.
- **MentorAgent.Blazor:** BUG-086 (S2): on WebAssembly, answering a confirmation and pressing Stop used the host's `HttpClient` with a relative URL and no token, so a signed-in user's requests were refused by the owner check. They now go to the hub's origin with the hub's bearer token (`AccessTokenProvider`).
- **MentorAgent:** BUG-087 (S2): `ConfigureChatClientPipeline` is now built with the host's services; the documented `b => b.UseLogging()` made every coordinator build fail.
- **MentorAgent.Abstractions:** BUG-088 (S2): the buttons of a `MentorCardView` rendered inside a `ChatWidget.CardTemplate` did nothing; they now reach the widget's action dispatcher.
- BUG-089 (S2): UI actions registered by a hub client (WebAssembly, React, MAUI) now keep their parameter hint, and the WebAssembly page context generates the same hints as Blazor Server.
- **MentorAgent:** BUG-090 (S3): `navigate_to` no longer waits for `SignalReady` on hub, SSE and A2A turns, where no page can send it; each navigation to a page with `HasUIActions` used to cost the full `ReadyTimeout` and a warning.
- **MentorAgent:** BUG-092 (S2): `[MentorAction(ProactiveHint = ...)]` was stored and never read; it now reaches the model, appended to the tool description as "Guidance: ..." (it is not used by semantic tool filtering).
- **MentorAgent:** BUG-093 (S2): `SkillsRefreshInterval` had no effect because every session built its own cache; the composed skill sources are now cached once per process.
- **MentorAgent.Abstractions:** BUG-095 (S2): a `ChatInput` composed without `ChatWidget` threw on the first message (an unguarded JavaScript call), which ended the Blazor Server circuit.
- Documentation: corrections across the five READMEs, among them attribute examples that did not compile, SignalR enums arriving as numbers, the anonymous-identity and rate-limit text (per session since rc.5), and how to protect `/mcp` and `/a2a` and what to expect from them. The XML docs and the server's startup warning now match the `PerSession` default.

### Security

- **MentorAgent.Declarative:** BUG-094 (S2): `webSearch`, `codeInterpreter`, `fileSearch` and `mcp` tool entries in a YAML definition created provider-hosted tools outside MentorAgent's gate, against the package's guarantee that a definition file cannot add a capability the application does not already have. They are now removed with a warning; only `kind: function` entries are kept. **Breaking:** a definition that relied on those entries loses them; provider-hosted tools are configured by the host application (`MentorOptions.HostedTools`), not by a definition file.

### Known issues

- **MentorAgent:** BUG-074 (S3): with `MentorshipLevel.Proactive`, the assistant can offer next steps the application does not have.

## [1.0.0-rc.10] - 2026-09-19

### Added

- **MentorAgent:** a MentorAgent A2A caller now sends its own name for the peer as A2A message metadata (`mentoragent.addressedAs`), and a MentorAgent peer reads it into the turn's context so it knows that name refers to itself. The value comes from another machine and goes into a prompt, so only a single token of letters, digits, `-`, `_` and `.` (at most 64 characters) is accepted; anything else is dropped whole.

### Fixed

- **MentorAgent:** BUG-081 (S2): a turn served over A2A could state figures no tool had returned. The A2A turn notice is now a procedure: call the tool first, take every number, name, date or status from a tool result of this turn, and if no tool has it, reply only that the application cannot provide it. As a structural backstop, when an A2A-served turn ends without the coordinator calling any tool, the reply is discarded and the request is run once more with a tool call required on that attempt's first model call; if that attempt is also tool-less, the classifier model is asked whether the reply states a value from the application's live records, and unless the answer is a clear no the caller receives "NOT GROUNDED: ..." instead of the value. There are never more than two attempts, the extra attempt is paid only by tool-less A2A requests, and a tool-less reply that states no data is returned unchanged.
- **MentorAgent:** BUG-081 (second part): a peer did not know that the caller's name for it, carried inside the request, meant itself, and answered that it had no access to that system. The notice now says so, backed by the `mentoragent.addressedAs` metadata above.

### Known issues

- **MentorAgent:** BUG-074 (S3): with `MentorshipLevel.Proactive`, the assistant can offer next steps the application does not have.

## [1.0.0-rc.9] - 2026-09-19

### Fixed

- **MentorAgent:** BUG-080 (S2): two hosts that list each other as remote A2A agents could delegate one request back and forth without end, each hop a paid turn. The internal `[Specialist requested: NAME]` marker that `route_to_specialist` adds for the local router (since rc.8) crossed the wire and was read by the peer as its own instruction; the A2A client now strips it on both the streaming and the non-streaming path. In addition, a turn that arrived over A2A is no longer offered the host's remote agents: they are absent from the coordinator's prompt, the router's prompt and the description of `route_to_specialist`, and one Debug line names what was withheld. The host's own specialists, declarative agents, teams and tools still serve the request.

### Known issues

- **MentorAgent:** chaining A → B → C through a MentorAgent host is not supported in 1.0: a turn that arrives over A2A is not offered the host's own remote agents.
- **MentorAgent:** BUG-073 (residual): on a Chat Completions host, the cold first turn of a process can still narrate a handoff without calling `route_to_specialist`; the rc.7 fix lowers the rate, it does not remove the behaviour.
- **MentorAgent:** BUG-074 (S3): with `MentorshipLevel.Proactive`, the assistant can offer next steps the application does not have.

## [1.0.0-rc.8] - 2026-09-18

### Added

- **MentorAgent:** `route_to_specialist`, the coordinator's delegation tool, takes an optional second argument, `specialist`, naming the specialist to use when the user named one or one clearly fits; when it is omitted the router chooses. Additive.

### Changed

- **MentorAgent:** a turn that arrives over A2A now tells the model that no human is present: carry the request out and reply with the result, with no follow-up questions, offers or announcements in place of action. Hub and SSE turns are unchanged. (Tightened in rc.10, BUG-081.)
- **MentorAgent:** the router at the entry of the handoff workflow now has a short prompt of its own (about 400 tokens) instead of the coordinator's entire prompt, which was paid a second time on every delegated turn (part of BUG-076).

### Fixed

- **MentorAgent:** BUG-078 (S2): the A2A server had never completed a round trip with any client. The agent card advertised protocol binding `HTTP+JSON` while `MapMentorAgentA2A` maps the SDK's JSON-RPC endpoint (it now says `JSONRPC`); the handler changed a task's status without submitting the task first; and it disposed a DI scope in a way that threw after the turn had already run and been billed. On the client side, `A2AChatClient` read only Message events inside the handoff workflow, so it could not read a MentorAgent server's answer, which arrives as the final status update of a task; it now reads a final Task or StatusUpdate too (Working statuses are skipped).
- **MentorAgent:** BUG-076 (S2): the router was told to call `route_to_specialist`, the tool it runs inside, so it narrated a handoff instead of performing one, and the narration was returned as the specialist's answer. The router now has a router's prompt (who the specialists are; call exactly one handoff function; never answer; never say you are forwarding). The tool returns what the specialists said, as text; when only the router spoke, the result is "NOT DELEGATED ..." and the operator gets a warning; a specialist that raised is reported as "DELEGATION FAILED", with the error in the warning.
- **MentorAgent:** BUG-077 (S3): model calls made under the coordinator — the router, Level-2 specialists, Level-3 team members and agents built by an `IMentorAgentSource` — ran on the raw host client and were never metered; they now run inside the metering wrapper. Because Azure's first streamed chunk carries an empty (not null) model id, the wrapper now takes the first non-empty id and falls back to the client's own deployment, so a `ClassifierChatClient` on its own deployment gets its own dashboard row.
- **MentorAgent:** BUG-079 (S3): specialists had no rule against inventing data. Generated specialist prompts and every team member's prompt now carry a grounding rule: state only facts a tool returned; if nothing provides what is asked, say so, and never estimate or invent a number, name, date or status. Custom `[MentorAgent(Instructions = ...)]` prompts are left exactly as written — a custom prompt replaces the rule too, so add it to your own instructions.
- **MentorAgent:** BUG-075 (S3), second pass: on hosts with memory on, the safety classifier is now told that the secrets its rules protect are the system's, and that what users tell the assistant about themselves is theirs to give and to ask back; asking the assistant to remember a private code, and asking for it back, are no longer classified unsafe. The model itself may still decline to keep something its user calls private, and says why.

### Known issues

- **MentorAgent:** BUG-074 (S3): with `MentorshipLevel.Proactive`, the assistant can offer next steps the application does not have.

## [1.0.0-rc.7] - 2026-09-16

### Fixed

- **MentorAgent:** BUG-073 (S2): the coordinator was told it could delegate to its specialists but never how — `route_to_specialist` appeared nowhere in the prompt and its description named no agent — so the model announced handoffs and ended the turn without calling anything. The capabilities block now says how to delegate and forbids announcing an unperformed handoff, and the tool's description names every specialist it reaches: attribute-declared, source-supplied and remote.
- **MentorAgent:** BUG-075 (S3): asking the assistant to remember a personal fact was classified unsafe on a host with memory on. Remembering what the user asks to keep is now named to the classifier as a configured capability (completed in rc.8).
- **MentorAgent:** BUG-072 (S3): a signed-in user whose `AuthenticationStateProvider` threw was silently keyed as anonymous — and, since rc.5, as a different anonymous in every circuit. The fail-closed fallback stays; it is now logged once per session, without a stack trace per visitor.
- **MentorAgent:** BUG-068 (addendum): the "Remote agent 'X' connected" line was still logged at Information for every circuit; it now follows the once-per-process rule.

### Known issues

- **MentorAgent:** BUG-074 (S3): with `MentorshipLevel.Proactive`, the assistant can still offer actions the application does not have. The rule is in the prompt; a structural fix is left open as a product decision.

## [1.0.0-rc.6] - 2026-09-14

### Fixed

- **MentorAgent:** BUG-068 (S3): the configuration summary was not written once per process, as the rc.5 notes stated — a second browser circuit reprinted the skills catalogue, external agents, hosted image model, Azure image-header note, hosted MCP server, hosted tool list and declarative handoff at Information. It is now written once per process; later builds log it at Debug instead of dropping it. Degradation warnings stay per session.
- **MentorAgent:** BUG-069 (S2): an agent supplied by an `IMentorAgentSource` (a declarative YAML specialist, or a host's own) joined the handoff graph but was never named in the coordinator's instructions, so the assistant said it did not exist. Source-supplied agents are now listed with their descriptions beside the attribute-declared ones.
- **MentorAgent:** BUG-070 (S4): every skill on the published A2A agent card carried a flattened name (for example `Getallordersforanalysis`); skill names are now readable.
- **MentorAgent:** BUG-071 (S4): a turn whose input safety check failed first printed two stack traces instead of one.

## [1.0.0-rc.5] - 2026-09-13

### Added

- **MentorAgent:** `MentorOptions.ClassifierChatClient`: the client MentorAgent uses for its own one-word decisions (input safety, hosted-tool scope, post-turn fact extraction). Point it at a small deployment, since those calls sit in front of the reply. Defaults to `ChatClient` and is metered under its own model id.
- **MentorAgent:** `MentorOptions.AnonymousIdentity` (`MentorAnonymousIdentity.PerSession` by default, or `Shared`): who a signed-out visitor is, for memory and for the rate-limit counter. See Changed.
- **MentorAgent:** `MentorOptions.RefuseOutOfScope` (off by default): refuse a message that is not about this application, using the scope verdict the classifier already produces. Free when `EnableSafetyCheck` is on, because both questions travel in one call.
- **MentorAgent:** `MentorOptions.WarmUpAtStartup` (off by default): build one coordinator when the application starts, so the first visitor does not pay for the catalogue embeddings, the shared MCP sessions and the remote agent cards.
- **MentorAgent:** `MentorMcpServer.Shared` (`bool?`, default `null`): one connection per process, or one per session. When unset, a server reached over `ServerUrl` is shared and a server started from `Command` is not, because a local child process can hold state for the person who started it.
- **MentorAgent:** `MapMentorAgentMcp(path, configure)` and `MapMentorAgentA2A(path, configure)` take an optional `Action<IEndpointConventionBuilder>`, so the host can apply `RequireAuthorization()` or other conventions to `/mcp` and `/a2a`. Both previously discarded their convention builders.
- **MentorAgent:** `IMentorQueryEmbedding`: the current message's embedding, computed once per turn. Inject it into your `IMentorRagSource` instead of embedding the query a second time.

### Changed

- **MentorAgent:** **Breaking:** signed-out visitors are now isolated per session by default (`AnonymousIdentity = PerSession`); previously every unauthenticated session shared the literal key `anonymous`, so one visitor's facts were recalled for the next and one visitor could spend everybody's rate-limit allowance. Single-user hosts that want the old behaviour — MAUI, desktop, kiosk, where it is what makes memory survive a restart — must set `AnonymousIdentity = MentorAnonymousIdentity.Shared`. A warning is logged when a host opts back into the shared bucket and a second session reaches it.
- **MentorAgent:** faster turns and lower cost per visitor: routing, tool filtering and memory relevance share one embedding per text per turn instead of embedding the same sentence two to four times; the tool catalogue and routing exemplars are embedded once per process instead of once per browser circuit; the input safety check and the hosted-tool scope check ask both questions in one classifier call; the composer is handed back when the answer is complete, not after the post-turn fact extraction; and MCP sessions and A2A agent cards are shared per process, with a failure backoff for the card cache. Measured time to first character fell from 2.6–3.0 s to 1.6–2.0 s on the same question.
- **MentorAgent:** RAG citation chips show only the documents the answer actually cited: documents are numbered in the prompt and the model is asked to cite them; if it cites none, no chips are shown.
- **MentorAgent:** `MentorshipLevel.Proactive` asks for one sentence before acting instead of a plan, and may offer only next steps the application can actually perform.
- **MentorAgent:** the A2A agent card now publishes the application's ungated actions as skills, its input/output modes and its streaming capability; it previously carried only a name and a description.
- **MentorAgent:** one provider failure now produces one stack trace instead of one per component that met it, and the coordinator's configuration summary is logged once per process instead of once per session (completed in rc.6 and rc.7, BUG-068).

### Fixed

- **MentorAgent:** BUG-062 (S3): text written before a tool call was glued to the text after it, and a Markdown table opening the second part stopped rendering as a table.
- **MentorAgent:** BUG-063 (S3): in `HitlMode.Native`, a user without the required role was shown the confirmation banner and refused only after approving it. The role gate now runs first, as it always did in Blocking mode.
- **MentorAgent:** BUG-065 (S4): the RAG "wrong scale" warning fired for thresholds that were on the source's own scale, and did so once per visitor.
- **MentorAgent:** BUG-066 (S3): the hosted-tool scope classifier was built on the raw `ChatClient`, so its billed calls appeared in no dashboard.
- **MentorAgent:** BUG-067 (S3): the safety classifier was never told what the application is equipped with (for example an MCP filesystem server or image input) and refused legitimate messages. It is now told, and a refused message is logged by fingerprint rather than by its text.

### Security

- **MentorAgent:** BUG-064 (S1): two anonymous visitors of the same site shared one memory. Anonymous visitors are now isolated per session by default (see Changed).

## [1.0.0-rc.4] - 2026-09-09

### Fixed

- **MentorAgent:** BUG-061 (S3): the provider error classifier recognised an unreachable image URL only in the Chat Completions wording, so on the Responses API (the client hosted tools require) the user saw the generic error instead of the specific message. The Responses API wording is now recognised too, keyed on the pair "error while downloading file" and "parameter: url", so that a failure on an operator-supplied file (such as a vector-store document) still falls through to the generic message.

## [1.0.0-rc.3] - 2026-09-04

### Added

- **MentorAgent.Blazor:** `MentorAgentBlazorOptions.AccessTokenProvider` supplies the bearer token for the hub connection, read on every connect and reconnect — the WebAssembly equivalent of the JavaScript client's `accessTokenFactory`. Return `null` while nobody is signed in; the connection then behaves as before. The hub connection is a singleton opened on the first widget render and the token is read only on connect and reconnect, so a sign-in that happens later takes effect on the next reload or reconnect: restore a persisted token before the host runs.
- **MentorAgent.Blazor:** `MentorAgentBlazorOptions.ConfigureConnection`, an escape hatch onto `HttpConnectionOptions` for anything the named options do not cover (a custom header, a transport restriction, cookies, `UseDefaultCredentials`).
- **MentorAgent.Server:** `MapMentorAgentServer(configureTransports: e => e.RequireAuthorization())` applies an endpoint convention to the hub and both `/mentor/chat` endpoints. Optional; omitting it changes nothing.
- **MentorAgent.Server:** a one-line startup warning when the SSE endpoint ends up with no authorization metadata while memory is on, and a README section listing every endpoint, what an anonymous caller can do with it and how to protect it.
- **MentorAgent.Server:** `GET /mentor/session?connectionId=...` returns the connection's session snapshot (204 when there is nothing to save, 404 for an unknown connection), and `POST /mentor/session?connectionId=...` restores one (400 for a snapshot that is not MentorAgent's, without touching the live conversation). This gives WebAssembly, React, Vue and MAUI clients a way to survive a reconnect, which starts a new conversation. Both verbs are restricted to the connection's owner (see Security).
- **MentorAgent:** `MentorOptions.ChatHistoryProviderFactory` (`Func<IServiceProvider, ChatHistoryProvider>`), invoked once per coordinator with that coordinator's service provider, so a provider can key its storage by whatever identifies the caller. Prefer it in any application with more than one user. Setting both it and `ChatHistoryProvider` throws in `MentorOptions.Validate()` at startup.
- **MentorAgent:** `MentorOptions.McpCallerPrincipal` resolves the MCP caller's `ClaimsPrincipal`; a role-gated action is then published over MCP only when that principal is authenticated and holds one of the action's roles. No resolver (the default), a null principal, an unauthenticated identity or a resolver that throws all withhold. Confirmation gates (`RequiresConfirmation`, the `RequiresApproval` predicate) are never satisfied by it, because there is no human on the far end of an MCP call. The published tool list is evaluated per caller identity.
- **MentorAgent:** provider errors the user can act on — an unreachable image URL (quoted back), a conversation past the context window, a throttled provider, a rejected image, a timeout — are now shown as specific localized messages instead of "An error occurred while processing the response". Everything else (an expired key, a missing deployment, a firewall rule) stays generic; provider text is never forwarded, `IncludeWorkflowExceptionDetails` keeps its meaning and default, and the full exception is still logged. Seven new localizer keys, in all ten languages, including the generic fallback, which was previously hard-coded in English.

### Changed

- **MentorAgent:** **Breaking:** the package now ships an MSBuild guard (`buildTransitive`) that fails the build with `error MENTOR001` when the resolved `OpenAI` package is 2.11.0 or later, instead of letting the application build and then die at startup with a `MissingMethodException` from `AzureOpenAIClient.GetResponsesClient()` (BUG-045, S1). The pins are now exact ranges (`Microsoft.Extensions.AI.OpenAI` `[10.6.0]`, `Microsoft.Agents.AI.Foundry` `[1.17.0-preview.260804.1]`). If the guard fires, remove your direct reference to `Microsoft.Extensions.AI.OpenAI` 10.7.0+ or `Microsoft.Agents.AI.Foundry` 1.18.0+ and let MentorAgent supply them, or pin them to those versions. Escape hatch: set the MSBuild property `MentorAgentSkipOpenAIVersionCheck=true` to override the check deliberately.
- **MentorAgent:** on a turn that calls a UI action, the sentence the model writes alongside the call now reaches the user (a consequence of the streaming fix, BUG-052).
- **MentorAgent:** the input safety classifier now receives the application's scope (`AppName`, `AppDescription`, discovered page names) and is told that reading the application's own business data is safe however broadly it is phrased; instruction-override attempts are still refused (BUG-047).
- **MentorAgent:** the scope text the hosted-tool domain gate derives now names the hosted capabilities that are enabled and states the decision rule as a constraint on the subject. A custom `HostedToolDomainScope` is left exactly as written (BUG-050).
- **MentorAgent.Abstractions:** the README gives every component a worked example (`MentorCardView`, `MessageBubble`, `ChatInput`, `OnboardingTour`, `ConfirmationBanner`, `WelcomePanel`, `RagSourcePanel`, the MCP/A2A badges, `TypingIndicator`) plus a "Composing your own chat surface" walkthrough.

### Fixed

- **MentorAgent.Server:** BUG-051 (S1): the hub method `SendMessage` takes `(string text, IReadOnlyList<MentorAttachment>? attachments)`, and SignalR binds arguments by count, so a C# default value never applies: the documented JavaScript quickstart, React hook, Angular service, Vue composable, .NET MAUI/console client and UI-actions walkthrough all invoked it with one argument and were rejected ("Invocation provides 1 argument(s) but target expects 2", surfaced to the client as "Failed to invoke 'SendMessage' due to an error on the server"). All six snippets are corrected. If you wrote a client from them, pass `null` (or `[]`) as the second argument; nothing changed on the server.
- **MentorAgent:** BUG-054 (S1): MentorAgent could not start at all on .NET MAUI (Blazor Hybrid), even with skills off: the skills builder read `IHostEnvironment.ContentRootPath`, which throws on MAUI, and the exception killed the whole Blazor renderer. The content root now falls back to `AppContext.BaseDirectory`, where a MAUI app's content files land, so a relative `SkillsFolder` still resolves.
- **MentorAgent:** BUG-053 (S2): the documented Blazor Hybrid (MAUI) render mode failed its first build with `NU1605`. The core README now has a Blazor Hybrid (MAUI) setup section, including the package pin that resolves the error.
- **MentorAgent:** BUG-047 (S2): with `EnableSafetyCheck` on, ordinary requests for the application's own records were refused depending on their wording (see Changed).
- **MentorAgent:** BUG-052 (S2): registering one UI action switched token streaming off for every turn on that page — the whole reply arrived in a single frame. The UI-actions loop now streams, withholding only calls to its own registered actions (filtered by name), so genuine tool calls of the outer pipeline pass through untouched.
- **MentorAgent:** BUG-050 (S3): with `HostedToolDomainCheck` on, the README's own image-generation example was refused as out of scope (see Changed).
- **MentorAgent:** BUG-044 (S3): the core README's first Azure OpenAI Responses API example, and the same form in the `MentorOptions` doc comment, did not compile; they now show `GetResponsesClient().AsIChatClient("deployment")`.
- **MentorAgent.Abstractions:** BUG-048 (S3): seven of the nine buttons of an open panel were icon-only and named by `title` alone, so screen readers announced them as unnamed buttons. Every icon-only control now also carries an `aria-label` from the same localizer key.
- **MentorAgent.Abstractions:** BUG-049 (S3): the MCP and A2A badge copy was hard-coded (Italian in `ChatWidget`, English in `McpStatusBadge`). Six new localizer keys in all ten languages (`mcp.connecting`, `mcp.allOnline`, `mcp.someOnline`, `mcp.offline`, `a2a.pill`, `a2a.agents`), read by both components.

### Security

- **MentorAgent.Blazor:** BUG-046 (S1): the WebAssembly client could not authenticate its hub connection, so the server saw every user as anonymous: every `RequiredRoles` action was blocked, `RateLimitPerUser` became one global limit, and all users shared the memory key `anonymous` — with `MemoryAutoCapture` on by default, personal facts extracted from one person's conversation reached the next person's prompt. Fixed by `AccessTokenProvider` and `ConfigureConnection` (see Added). Upgrade MentorAgent.Blazor and MentorAgent.Server together. A WebSocket handshake cannot send an `Authorization` header, so a JWT host must read the `access_token` query parameter for `/mentor-hub` (a `JwtBearerEvents.OnMessageReceived` recipe is in the Server README).
- **MentorAgent:** BUG-055 (S1): `MentorOptions` is a singleton, so an object assigned to `ChatHistoryProvider` served every circuit, connection and user in the process, and one user's conversation could be served to another. Use `ChatHistoryProviderFactory` in any multi-user application. `ChatHistoryProvider` is kept for single-user hosts and for providers that partition their own storage, and now logs at Debug that it is shared.
- **MentorAgent.Server:** BUG-060 (S1): the hub and SSE endpoints could not be protected through `MapMentorAgentServer()`, which discarded their endpoint builders — and an anonymous caller could run paid turns into one memory bucket and one rate-limit counter shared by all anonymous callers. Use `configureTransports` (see Added).
- **MentorAgent.Server:** BUG-058 (S1, present since before rc.2): `POST /mentor/approve` let anyone who learned an action id approve a gated action its owner had not answered, or deny one they wanted. The server now records which connection raised each pending confirmation and accepts an answer only from that connection's owner — the same ownership check as `/mentor/session` — and `POST /mentor/cancel` gets the same check. **Breaking:** on an authenticated host, approve and cancel requests must carry the same identity as the hub connection; an anonymous caller against an authenticated connection is refused with 401 and a different user with 403. Hosts with no authentication, SSE confirmations and in-process Blazor Server confirmations are unchanged.

## [1.0.0-rc.2] - 2026-08-24

### Added

- **MentorAgent:** `MentorOptions.SkillSources` accepts any Agent Framework `AgentSkillsSource` — a database, a remote catalogue, a per-tenant store or Microsoft's `AgentFileSkillsSource`. Its skills reach the assistant through the same `load_skill` / `read_skill_resource` tools, catalogue prompt and gate as MentorAgent's own. Sources are composed with the framework's aggregating, filtering, deduplicating and caching decorators; an application skill always wins a name collision (the duplicate is skipped with a warning). Sources need a model: MentorAgent uses `Agent` when set, otherwise a plain agent over `ChatClient`; with neither, sources are skipped with a warning. MentorAgent's own `SKILL.md` convention is unchanged — register the framework's file source as well if you want both dialects.
- **MentorAgent:** `MentorOptions.SkillFilter` (applies to source-supplied skills only, never to your own) and `MentorOptions.SkillsRefreshInterval` (adds caching of the sources; it had no effect until rc.11, because every session built its own cache — BUG-093).
- **MentorAgent:** `MentorOptions.AutoApprovalRules` (with `HitlMode.Native`): approve a tool call before any confirmation banner appears, through the framework's `ToolApprovalAgent` middleware. Enabling it also enables "don't ask again": a user's approval becomes a standing rule for the rest of the session. Opt-in — `HitlMode.Native` alone does not add the middleware. A rule waives the prompt, never the permission: `RequiredRoles` is still enforced. What is not auto-approved still travels as `ConfirmationRequired` and is answered with `POST /mentor/approve`.
- **MentorAgent:** `MentorOptions.MaxAutoApprovalIterations` caps how many times one turn may re-run because every surfaced approval was auto-approved; each re-run is a billable model call.

### Changed

- The family moves onto the Microsoft Agent Framework 1.18.0 line (`Microsoft.Agents.AI` 1.18.0) and `Microsoft.Extensions.AI` 10.7.0. `Microsoft.Agents.AI.Foundry` stays on 1.17.0-preview and `Microsoft.Extensions.AI.OpenAI` on 10.6.0 so that `OpenAI` stays at 2.10.0: 2.11.0 removes a constructor that `Azure.AI.OpenAI` 2.9.0-beta.1 still calls, and a host using Azure OpenAI hosted tools or service-managed history would die at startup with a `MissingMethodException` from `AzureOpenAIClient.GetResponsesClient()` (BUG-043, S1, fixed before this release). See Known issues.
- **MentorAgent.Declarative:** `Microsoft.Agents.AI.Declarative` moves to 1.18.0-rc1, on the same line as the core package.
- **MentorAgent:** a bare `GET /mcp` (mapped by `MapMentorAgentMcp()`) now answers 400 with a JSON-RPC error naming the missing `Mcp-Session-Id` header, where it previously answered 406 (MCP SDK behaviour).

### Known issues

- BUG-045 (S1): the `OpenAI` hold is a minimum, not a ceiling. A project that references `Microsoft.Extensions.AI.OpenAI` (10.7.0+) or `Microsoft.Agents.AI.Foundry` (1.18.0+) directly resolves `OpenAI` 2.11.0 or later, builds cleanly and dies at startup. Keep those two packages at 10.6.0 and 1.17.0-preview. (Fixed in rc.3 by exact pins and the `MENTOR001` build error.)

## [1.0.0-rc.1] - 2026-08-19

### Added

- `IMentorOrchestrator.SerializeSessionAsync` and `IMentorOrchestrator.RestoreSessionAsync` for saving and restoring a conversation. The equivalent `IMentorSessionManager` methods take the coordinator `AIAgent`, which no public API returns, so they could not be called (BUG-025, S2).
- `MentorModelUsage.RequestsWithoutUsage`: requests that were billed but reported no token counts (for example a turn the user stopped), disclosed instead of being counted as zero.
- **MentorAgent.Server:** two SSE frames: `{"type":"confirmationRequired","actionId":"...","tool":"...","message":"..."}` — answer it with `POST /mentor/approve?actionId=...&approved=true|false` on a second request while the stream is open, and the parked turn resumes — and `{"type":"navigation","url":"..."}` for `navigate_to` (BUG-037, S2). The README now documents the complete SSE frame set: `chunk`, `cards`, `navigation`, `confirmationRequired`, `completed`, `error`.

### Changed

- **MentorAgent.Server:** **Breaking:** the SSE frame `{"type":"completed"}` now means the turn is over. It previously fired when the streaming segment ended — including for a turn parked on a human confirmation, which was reported as completed with no action id to approve (BUG-038, S2). A client that treated `completed` as "the answer is finished" keeps working; a client that used it to decide the request had succeeded must now also handle `confirmationRequired`.
- **MentorAgent:** **Breaking:** session snapshots are now stamped (`{"mentorAgentSession":1,"session":...}`) and anything else is refused before the live session is touched; previously arbitrary JSON restored as an empty session that silently replaced the live conversation (BUG-026, S3). Snapshots written by 1.0.0-preview.5 are refused: discard them rather than migrating.
- **MentorAgent:** refusals now reach the model with a structural marker (`declined_by_user` / `stopped_by_user`) and the instruction travels in the tool result, instead of looking like a crashed tool that the model offers to retry (BUG-031, S3).
- **MentorAgent:** MentorAgent's own `last_session` bookkeeping is still written but no longer injected into the memory context, where it always took a slot (BUG-014, S3).
- **MentorAgent:** machine-facing output of the evaluation report (including `FailureReason`) is invariant-culture (BUG-020, S4).

### Removed

- **MentorAgent:** the stale second copy of the widget stylesheet and script that the core package shipped under its own `_content` path, referenced by nothing (BUG-023, S4).

### Fixed

- **MentorAgent:** BUG-018 (S1): a `[TeamTerminationCondition]` on a Level-3 team removed the iteration cap instead of tightening it, so a predicate that never fired ran the group chat without bound. Custom conditions now run in addition to `MaxIterations`, never instead of it.
- **MentorAgent:** BUG-013 (S2): on Path B (`options.Agent`) the tool list was built and logged but never sent to the model, so the assistant had no application actions, `navigate_to`, `invoke_ui_action` or memory tools. The list is now passed through `ChatClientAgentRunOptions`, whose tools are unioned with the agent's own.
- **MentorAgent:** BUG-015 (S2): Path B also received none of MentorAgent's instructions and none of the per-turn context (page, memory, RAG, skills); they are now added alongside the host's own persona. BUG-006 (S2): a custom `AIAgent` could fail to start at all, telling the user to reload the page. BUG-007 (S3): Path B diagnostics were unreachable.
- **MentorAgent:** BUG-027 (S3): `RagMinScore` defaults to 2, a scale that suits keyword-scored sources, while vector stores return cosine similarity in [0, 1] — so on a vector store every document was silently discarded. A warning is now logged once per provider naming the threshold, the document count and the highest score seen. For a vector store, set `RagMinScore` between 0.5 (loose) and 0.75 (strict), or 0 to disable filtering.
- **MentorAgent:** BUG-016 (S3): `UseRag = true` with no `IMentorRagSource` registered now warns at coordinator build. BUG-017 (S3): a `RagSystemPromptTemplate` without the `{documents}` placeholder now warns (it dropped every document while still showing citation chips).
- **MentorAgent:** BUG-028 (S2): `MaxSessionMessages = 0` (or negative, or an empty key in `appsettings.json`) threw while the coordinator was assembled; it is now guarded and read like the neighbouring options that use 0 for "no limit".
- **MentorAgent:** BUG-011 (S2): an assembly listed twice in `ScanAssemblies` registered everything twice and the coordinator failed to build; duplicates are now removed with a warning. BUG-008 (S3): "Native HITL is enabled but no tool requires approval" is now logged at a level operators watch.
- **MentorAgent:** BUG-030 (S2): Azure OpenAI does not reliably report the response model id, and a blank id was attributed to the cheap model, so escalated turns were priced as cheap ones. BUG-034 (S2): a dated model id (`gpt-4.1-2025-04-14` for a deployment named `gpt-4.1`) produced a separate unpriced dashboard row; it is now folded onto the configured or priced model by an exact trailing-date match, never by prefix. BUG-033 (S3): MentorAgent's own model calls (memory auto-capture, the safety check, the hosted-tool domain gate) are now metered. BUG-032 (S3): a turn the user stopped is now counted, with its latency, instead of vanishing from the metrics.
- **MentorAgent:** BUG-012 (S2): answering a confirmation synchronously (an auto-approval policy, a pre-approved client) parked the turn forever with no error.
- **MentorAgent:** BUG-019 (S3): a UI action requested by the model ran against the page registrations captured before the model call, so navigating away mid-turn ran a handler on a disposed component; registrations are now resolved at execution time.
- **MentorAgent:** BUG-010 (S3): tool results containing a tab or another control character produced JSON the model could not parse.
- **MentorAgent:** BUG-029 (S2): `AgentCardInfo.IconUrl`, `Provider`, `DocumentationUrl` and the version override now reach the published A2A agent card. `AgentCardInfo.Tags` is documented as reserved and not published, because the A2A specification attaches tags to skills, not to the card.
- **MentorAgent.Abstractions:** BUG-024 (S2): in hands-free voice mode the microphone stayed open during playback, so the assistant transcribed its own answer and sent it back as the next question. The microphone now closes when an answer begins, and the half-heard fragment is discarded.
- **MentorAgent.Abstractions:** BUG-039 (S3): loading `MentorAgent.js` twice — the configuration recommended on WebAssembly, where `index.html` carries a script tag and the widget injects a second — reset the voice state machine mid-session; the state now survives. Upgrade MentorAgent.Abstractions and MentorAgent.Blazor together.
- **MentorAgent.Abstractions:** BUG-022 (S4): the RAG citation chips read a CSS variable no stylesheet declares and rendered at full brightness; they now use the theme's secondary text level.
- **MentorAgent.Blazor:** BUG-009 (S2): `IMentorOrchestrator.RespondToApprovalAsync` on WebAssembly went through the `RespondToApproval` hub method, which deadlocks while the turn awaiting approval holds the connection; it now posts to `/mentor/approve`. `CancelCurrentRequest` no longer invokes the hub on a dead connection.
- **MentorAgent.Server:** BUG-036 (S2): the documented SSE `cards` frame was lost, because several handlers wrote to the response concurrently inside an empty catch block. All frames now go through a single ordered writer, and a failed write is logged.
- Documentation (each of these broke custom clients written from the READMEs): the streaming event is `StreamingChunk`, not `ReceiveChunk` (BUG-001, S2); navigation arrives as `NavigationRequested`, not `NavigateTo` (BUG-005, S2); the protocol reference now lists all 18 server-to-client events, including `Cards` and `HostedToolsDeclared` (BUG-002, S2), and marks `CancelRequest` and `RespondToApproval` as do-not-use in favour of the HTTP routes; the memory tool is `forget_all`, not `forget` (BUG-003, S3); the skills example follows the tool-naming rule (BUG-004, S3); `CancelCurrentRequest`, `RespondToApprovalAsync` and the metrics snapshot served over HTTP (including `DeflectionRate`) are documented with reference tables and examples (BUG-040, BUG-041, S3); and the READMEs now state that a SignalR reconnect is a new connection, a new DI scope and therefore a new conversation.

### Security

- **MentorAgent:** BUG-021 (S1): the MCP server published every `[MentorAction]`, including actions restricted with `RequiredRoles` and actions marked `RequiresConfirmation`, to any client that could reach `/mcp` — an endpoint mapped with permissive CORS by default, whose requests carry no signed-in principal. Gated actions are now withheld from the MCP surface, and the withheld set is logged with each tool and the reason.
- **MentorAgent:** BUG-035 (S1): the same hole existed for actions gated by the `RequiresApproval` predicate; both routes now fail closed. **Breaking:** a gated action is no longer reachable over MCP and there is no opt-in to publish it anyway; if an MCP client relied on one, perform that action from the application instead. (rc.3 added `McpCallerPrincipal` for publishing role-gated actions to authenticated callers.)

### Known issues

- On Blazor WebAssembly, `SerializeSessionAsync` and `RestoreSessionAsync` throw `NotSupportedException`, because the session lives in the server's DI scope. (rc.3 added `GET/POST /mentor/session` for remote clients.)
- A SignalR reconnect, including with `withAutomaticReconnect()`, is a new connection and therefore a new conversation.
- **MentorAgent.Server:** the SSE stream forwards six of the hub's eighteen events; `GeneratedImages`, RAG citations and the hosted-tool activity line are hub-only.
- **MentorAgent.Server:** the hub's receive-message limit is sized from the image options; an oversized argument aborts the connection instead of returning an error.
- **MentorAgent:** with `A2AServerUrl` unset, the A2A agent card advertises a bare path, which is useless to a client on another host; set it to the absolute URL.

## [1.0.0-preview.5] - 2026-08-12

### Added

- **MentorAgent.Declarative:** first release. `AddMentorAgentDeclarative(options)` defines Level-2 specialists in YAML instead of C# classes: it loads `*.agent.yaml` files from `Directory` (`SearchPattern`, default `*.agent.yaml`; `Recursive`, default `false`) and inline `Definitions`, and can expose one configuration section through `ConfigurationSection`. The agents join the same handoff graph as `[MentorAgent]` classes. A YAML file names tools, it does not define them: each is resolved from the Level-1 tools the application already registers, already wrapped in MentorAgent's gate, so `RequiredRoles`, human approval, action feedback and per-tool metrics keep working. A malformed definition is logged and skipped and never stops the assistant from starting. Definition files are code — load them only from deploy-time paths. The Agent Framework's declarative stack (Power Fx and its dependencies) stays out of the core package.
- **MentorAgent:** `IMentorAgentSource` (with `MentorAgentSourceContext`): supply Level-2 specialists that the `[MentorAgent]` attribute scan cannot see. They join the same handoff graph, and the tools handed to a source arrive already gated.
- Generative UI Level 2, cards: a tool can return a `MentorCard` (or several) instead of a string, and the widget renders a card with title, subtitle, label/value fields, an accent colour and buttons that send a message, navigate, or run a UI action the page registered. New contracts `MentorCard`, `MentorCardField`, `MentorCardAction`, `MentorCardActionKind`, `MentorCardAccent`; `MentorCardView` component (XSS-safe: no `MarkupString`); `IMentorStateService.OnCardsReady` / `NotifyCardsReady`; `MentorOptions.EnableGenerativeCards` (default `true`; `false` restores the previous text behaviour). Because cards come from application code, the conversation cannot add a button or change where one points; the model is told the card is on screen and receives the data in compact form.
- **MentorAgent.Abstractions:** `ChatWidget.CardTemplate` renders a card kind your own way and falls back to the built-in renderer for the rest. Buttons of a `MentorCardView` inside a template did nothing until rc.11 (BUG-088).
- **MentorAgent.Server:** cards are forwarded to remote clients as a `Cards` hub event and a `{"type":"cards"}` SSE frame, serialised camelCase with string enums. Additive: a client that ignores them still receives the whole answer as text.
- Onboarding tour: `EnableOnboardingTour` (default `false`, on `MentorOptions`, `MentorWidgetOptions` and `MentorAgentBlazorOptions`), `IMentorTour`, `MentorTourStep` and the `OnboardingTour` component. A short tour on a user's first open of the widget, generated once per process from the `[MentorPage]` pages and the assistant's tools; each step can carry a ready-made question. A generated URL matching no registered page is dropped; without a `ChatClient` a deterministic tour is built from the same data. Register your own `IMentorTour` to script it by hand.
- **MentorAgent.Server:** `GET /mentor/tour` returns the tour steps as JSON for headless and WebAssembly clients; enable `EnableOnboardingTour` on the server as well. Unauthenticated by design: the payload is the public screen names and capability descriptions, identical for every user and cached per process.
- **MentorAgent.Blazor:** `MentorAgentBlazorOptions.TourUrl` for fetching the tour from the server; if the fetch fails the widget opens normally.
- Voice options `VoiceStreaming` (default `true`), `VoiceBargeIn` (default `true`: playback stops when the user presses the microphone, sends a message, presses Stop or starts a new conversation), `VoiceHandsFree` (opt-in: the transcript is sent on silence and the microphone reopens when the spoken answer ends) and `VoiceRate` (default `1.0`), on `MentorOptions`, `MentorWidgetOptions` and `MentorAgentBlazorOptions`.

### Changed

- Voice output now speaks each sentence while the answer streams, instead of reading the finished reply back; a decimal point is not treated as a full stop, and fenced code blocks are skipped rather than read out.
- **MentorAgent.Server:** the README endpoint table now lists `/mentor/approve`, `/mentor/cancel`, `/mentor/admin/metrics` and `/mentor/tour`.

## [1.0.0-preview.4] - 2026-08-04

### Added

- **MentorAgent:** provider-hosted tools: `MentorOptions.HostedTools` (`MentorHostedTools.WebSearch | CodeInterpreter | FileSearch | ImageGeneration | HostedMcp`) lets the model provider run web search, a sandboxed code interpreter, file search, image generation and remote MCP calls on its own infrastructure. Related options: `FileSearchVectorStoreIds` (required for file search), `FileSearchMaxResults`, `HostedMcpServers` (`MentorHostedMcpServer`, with `RequireApproval`, `AlwaysRequireApprovalTools`, `NeverRequireApprovalTools`), `HostedImageModel` and `HostedImageSize` (`WIDTHxHEIGHT`; an unparsable value is ignored with a warning). Fail-closed: file search is skipped with a warning without vector store ids, hosted MCP without servers. Availability depends on the provider client: the Responses API and Foundry support the full set, Chat Completions only web search, and the Responses API also needs `UseServiceManagedHistory`. The enabled set is logged at startup and listed in the coordinator's system prompt; when a tool is withheld on a turn, the model is told so in that request (appended to `ChatOptions.Instructions`), so it refuses instead of fabricating a result.
- **MentorAgent:** `MentorOptions.ShowHostedToolActivity` (default `true`) reports provider-side work through the existing channels: action-feedback lines such as "Searching the web…", web-search and file-search sources as citation chips, and generated images attached to the message.
- **MentorAgent.Server:** hub events `GeneratedImages(string[])` (data: URIs or absolute URLs, ready for `<img src>`) and `HostedToolsDeclared(int)` (sent once on connect with the active `MentorHostedTools` flags). Hosted-tool activity also arrives on the existing `ActionExecuting` / `ActionCompleted` and `RagSourcesReady` events. Additive.
- **MentorAgent.Abstractions:** hosted-tools badge in the widget header, driven by `MentorWidgetOptions.ShowHostedToolsStatus` and `HostedTools` (mirrored on `MentorAgentBlazorOptions`), plus `IMentorStateService.OnHostedToolsDeclared` / `NotifyHostedToolsDeclared`: the widget prefers the set the server declares over its local option. New CSS variables `--bm-hosted-text` and `--bm-a2a-text`, overridden per theme.
- **MentorAgent:** hosted-tool cost controls: `FilterHostedTools` (default `true`) puts hosted tools through the semantic tool filter, with their own threshold `HostedToolFilterMinScore` (effective default 0.15); `MaxHostedToolCallsPerSession` (0 = unlimited); `HostedToolDomainCheck`, with `HostedToolDomainScope` and `HostedToolDomainClassifier`, lets a small model decide once per turn — only when a hosted tool would otherwise run — whether the message concerns the application at all (fails open). All three require `EnableToolFiltering` and an `EmbeddingGenerator`; without them nothing is filtered and a startup warning names the ignored options.
- **MentorAgent:** `MentorOptions.HitlMode`: `MentorHitlMode.Blocking` (default, unchanged) or `Native` (the Agent Framework's `ApprovalRequiredAIFunction` / `ToolApprovalRequestContent` flow, one extra round-trip per approved call). The confirmation banner and client protocol are identical in both modes. `MentorOptions.RequiresApproval`: a predicate that forces approval for any tool by name, including tools you do not own (MCP, skills, remote agents).
- Multimodal image input: `EnableImageInput`, `AllowedImageTypes` (default png, jpeg, gif, webp), `MaxImageBytes` (default 4 MB) and `MaxImagesPerMessage` (default 4), all re-validated on the server (the widget copies only drive client-side pre-checks); rejected attachments are dropped with a warning and the turn still runs. Requires a vision-capable chat model. New contracts `MentorAttachment` and `MentorUserMessage` and `IMentorOrchestrator.SendMessageAsync(text, attachments)`; an image-only turn is valid. The composer accepts file upload, clipboard paste, drag and drop and a remote URL, and downscales an image over `MaxImageBytes` in the browser (at most 2000 px, original type kept; animated GIFs are not re-encoded).
- **MentorAgent.Blazor:** `WasmMentorOrchestrator` / `MentorHubClient` gain `SendMessageAsync(text, attachments)`.
- **MentorAgent.Server:** `POST /mentor/chat` accepting `{ "message": "...", "attachments": [ { "mimeType", "dataBase64" | "url", "fileName" } ] }` (GET remains for text-only). With `EnableImageInput` on, `AddMentorAgentServer()` raises `HubOptions<MentorHub>.MaximumReceiveMessageSize` to `MaxImageBytes * MaxImagesPerMessage * 4/3 + 512 KB`, scoped to the MentorAgent hub and only ever raising an existing value — keep the image options tight.
- **MentorAgent.Server:** `POST /mentor/cancel?connectionId=...` stops an in-flight turn out of band (SignalR dispatches one hub invocation at a time per connection, so the `CancelRequest` hub method could only run after the turn it was meant to abort).
- **MentorAgent:** `MentorOptions.UseServiceManagedHistory` for providers that keep the conversation on the service (the Responses API, Foundry, Copilot Studio); the Agent Framework refuses to combine their conversation id with a local history provider. With it on, no history provider is installed and `MaxSessionMessages` and `EnableCompaction` no longer apply.
- **MentorAgent:** duplicate tool names among the filterable tools are reported at startup.

### Changed

- **MentorAgent.Server:** **Breaking:** the hub method `SendMessage` now takes a second argument, `attachments`. SignalR binds arguments by count, so every client must pass both — send `null` (or `[]`) when there are no attachments; a one-argument invocation is rejected. (This release documented the argument as optional; corrected in rc.3, BUG-051.)
- **MentorAgent.Abstractions:** **Breaking:** `ChatInput.OnSend` is now `EventCallback<MentorUserMessage>` instead of `EventCallback<string>`; update handlers to take a `MentorUserMessage` and read `Text` (and `Attachments`).
- **MentorAgent:** tool filtering and model routing now decide once per turn instead of once per request, since a mid-turn change of tool list or deployment is rejected by providers that keep the conversation.
- **MentorAgent:** provider-side errors that arrive inside the stream are logged at Error level with message, code and details, instead of a bare "Stream content: ErrorContent" line.

### Deprecated

- **MentorAgent.Server:** the `RespondToApproval` and `CancelRequest` hub methods are kept for backwards compatibility only (`CancelRequest` works only while the connection is idle). Use `POST /mentor/approve` and `POST /mentor/cancel`.

### Fixed

- **MentorAgent:** `ChatOptions` was copied property by property in two middlewares, dropping `Tools` — so a page registering UI actions offered the model only those actions instead of the full tool list — and `ConversationId`, which broke every local tool call under service-managed history. Options are now cloned.
- Stop never worked for SignalR clients, including the WebAssembly widget, because the `CancelRequest` hub method queued behind the still-streaming `SendMessage`; `WasmMentorOrchestrator.CancelCurrentRequest` and the README client examples now use `POST /mentor/cancel`. Upgrade MentorAgent.Blazor and MentorAgent.Server together: the WebAssembly client's Stop needs the new endpoint, and the hosted-tools badge and image attachments need the preview.4 server (a mismatched pair falls back to the local badge option).
- **MentorAgent.Abstractions:** a citation with no URL (a document matched in a vector store, a file produced by the code interpreter) rendered as a link to "#" that jumped to the top of the host page; the chip now has no `href` and is styled as non-interactive.
- **MentorAgent.Abstractions:** a failure in the send itself (for example the transport rejecting the payload) left the chat with no reply, no error and no busy indicator; it now surfaces as an error bubble and clears the busy state.
- **MentorAgent.Abstractions:** the A2A (indigo) and hosted-tools (amber) chips hard-coded pale dark-theme text and were nearly invisible on the Minimal (light) theme.

### Security

- **MentorAgent:** tools invoked inside a Level-2 specialist or a Level-3 team bypassed the confirmation banner and the `RequiredRoles` check, because those agents run their own function-calling loop inside the workflow. The gate is now attached to the function itself, so role checks, confirmation, action feedback, `NavigateTo`, `OnToolResult`/`OnException` and per-tool metrics apply at every level. Nested tools always use the blocking approval flow, even under `HitlMode.Native`; the user is asked exactly once.
- **MentorAgent:** `MentorMcpServer.RequiresConfirmation` was documented but not enforced; tools from such a server now require confirmation in both HITL modes.

### Known issues

- **MentorAgent:** provider-hosted tools run on the provider and never pass through the function-calling middleware, so `RequiredRoles`, `RequiresConfirmation` and per-tool metrics do not apply to them; hosted MCP servers can require approval through `MentorHostedMcpServer.RequireApproval`.
- **MentorAgent:** the `MaxHostedToolCallsPerSession` counter lives in the orchestrator scope: over SignalR it spans the connection, while each `POST /mentor/chat` call is its own session, so a stateless HTTP client gets a per-request cap, not a per-user one.
- **MentorAgent:** on Azure OpenAI, hosted image generation also needs the `x-ms-oai-image-generation-deployment` request header, which MentorAgent cannot add to an already-built `IChatClient`: attach it as a pipeline policy where you construct the `AzureOpenAIClient`. A startup log line restates this whenever image generation is on.

## [1.0.0-preview.3] - 2026-07-24

### Added

- Token and cost dashboard: the admin-only `MentorDashboard` component (never shown in the end-user widget) over an `IMentorMetrics` snapshot (`MentorMetricsSnapshot`), with usage attributed per model — cheap chat model, strong routing model and embedding model (`MentorModelUsage`, `MentorModelKind`) — hourly time series (`MentorUsagePoint`), a model selector, temporal inline-SVG charts (no JavaScript, CSP-safe, theme-aware), a per-model cost table and top actions. Cost comes from developer-supplied `ModelPricing` (`ModelPrice`; no built-in prices); history is kept for `MetricsRetention` (default 7 days). A pluggable `IMentorMetricsStore` (default RAM-only) adds persistence, saved every `MetricsPersistenceInterval` (default 30 s). Labels are localized in 10 languages; in WebAssembly pass the fetched snapshot and set the component's `Language` parameter.
- **MentorAgent.Server:** `GET /mentor/admin/metrics` returns the metrics snapshot for headless dashboards, gated by `MentorOptions.DashboardRole` (default `"Admin"`).
- **MentorAgent:** model routing: `StrongChatClient` with `RoutingStrategy` (`MentorRoutingStrategy.Semantic`, `Classifier`, `Cascade` or `Custom`, the default), `RoutingThreshold` (default 0.35), `RoutingComplexExemplars`, `RoutingClassifierClient`, and the `UseStrongModel` / `UseStrongModelAsync` predicates.
- **MentorAgent:** token and cost controls: a per-call token counter and log line; `EmbeddingGenerator`; opt-in semantic tool filtering (`EnableToolFiltering`, `ToolFilterMaxTools` default 12, `ToolFilterMinScore` default 0.35); opt-in history compaction (`EnableCompaction`, `CompactionTokenThreshold` default 4000, `CompactionMaxTurns` default 8); embedding-ranked memory (`MemoryRelevanceFiltering`, requires `EmbeddingGenerator`, falls back to the last N; identity and preference facts are always kept).
- **MentorAgent:** `MentorOptions.MemoryAutoCapture` (default `true`): after each turn a dedicated small model call saves durable user facts (name, role, team, preferences), so memory no longer depends on the model calling the `remember` tool. Set it to `false` to avoid the extra call per user message.
- **MentorAgent:** `MentorOptions.EnableRichResponses` (default `true`): the coordinator is asked to format tabular data as Markdown tables and enumerations as lists, which the widget renders safely (Generative UI, Level 1).
- **MentorAgent:** middleware hooks: output safety check (`EnableOutputSafetyCheck`) alongside the existing input check; `InputGuardrail` and `OutputGuardrail`; `OnToolResult` to transform a tool result before it reaches the model; `OnException`; and `ConfigureChatClientPipeline` to add your own delegating chat clients.
- **MentorAgent:** OpenTelemetry observability: `EnableObservability`, `ObservabilitySourceName` (default `"MentorAgent"`) and `ObservabilityIncludeSensitiveData` (prompt and response content, for development) — GenAI-convention traces and metrics for model calls, plus per-turn and per-tool spans and counters.
- **MentorAgent:** structured outputs: `IMentorStructured.GenerateAsync<T>` returns a strongly typed result with a JSON schema derived from `T`.
- **MentorAgent:** evaluation kit: `MentorEvaluator` over the Agent Framework's native evaluation, running `EvalCase`s (`MentorEvalOptions`, `MentorEvalReport`, `MentorEvalResult`) and gating on token budget and quality with `report.ThrowIfFailed()`.

### Changed

- **MentorAgent.Server:** identity bridge: `AddMentorAgentServer()` surfaces the authenticated SignalR/HTTP principal to the core, so per-user memory, per-user rate limiting and `RequiredRoles` work for headless clients, not only for Blazor Server.
- **MentorAgent.Server:** the coordinator is built once per SignalR connection instead of once per hub method call, so external MCP servers connect once and the conversation persists across messages.
- **MentorAgent:** with `MemoryAutoCapture` on and a `ChatClient` configured, the `remember` tool and its prompt block are dropped; the forget tool is always available.
- **MentorAgent:** the HITL confirmation message now spells out the concrete arguments the assistant is about to pass, over every transport (including the headless `ConfirmationRequired` event); the full text is available as a tooltip.
- **MentorAgent:** a slimmer, cache-friendly system prompt; RAG relevance relies on the vector search and `RagMinScore` alone.

### Fixed

- **MentorAgent.Abstractions:** event handlers (`@onclick`, `@onchange`, `@bind`) in the shipped components compiled to literal HTML attributes and never fired, which broke the WelcomePanel suggestion chips, the HITL confirmation banner buttons and the MCP/A2A status badges. Upgrade to get working handlers.
- **MentorAgent:** the system prompt no longer includes a bare "User: not authenticated" line for anonymous users, which made the model invent authorization rules and refuse tools with no role requirement; role-gated tools remain enforced by the middleware.

## [1.0.0-preview.2] - 2026-06-22

### Changed

- New package icon for all packages.

### Fixed

- **MentorAgent.Server:** README: the HITL examples for Angular, Vue and .NET MAUI/console use `POST /mentor/approve` instead of the deadlocking `RespondToApproval` hub call, and every client example (React, Angular, Vue, MAUI) handles the `McpServerStatusChanged` event.
- **MentorAgent.Blazor:** README: `OnMcpServerStatusChanged` is forwarded over the SignalR hub and fires on the client.

## [1.0.0-preview] - 2026-06-22

### Added

- First release under the MentorAgent name, as the successor of BlazorMentor: **MentorAgent** (the core library and widget for Blazor Server and in-process hosts, `AddMentorAgent()`), **MentorAgent.Server** (`AddMentorAgentServer()` / `MapMentorAgentServer()`, exposing the `/mentor-hub` SignalR hub, the `/mentor/chat` SSE stream and `POST /mentor/approve` for headless clients such as React, Angular, Vue and .NET MAUI), **MentorAgent.Blazor** (the Blazor WebAssembly / Auto client, `AddMentorAgentBlazor()` with `HubUrl`) and **MentorAgent.Abstractions** (shared components and contracts, installed transitively). Targets .NET 10 and Microsoft Agent Framework 1.6.1.
- **MentorAgent:** MCP server: expose MentorAgent as an MCP server (`McpServerEnabled`, `McpServerPath`, `ShowMcpStatus`). `McpServerPath` was read by nothing until rc.11 (BUG-091): pass the path to `MapMentorAgentMcp()` instead.
- **MentorAgent:** MCP client: consume external MCP servers configured through `McpServers`.
- **MentorAgent:** A2A server: expose MentorAgent as an A2A agent (`A2AServerEnabled`, `A2AServerPath`, `A2AServerUrl`, `ShowA2AStatus`). `A2AServerPath` was read by nothing until rc.11 (BUG-091): pass the path to `MapMentorAgentA2A()` instead.
- **MentorAgent:** A2A consumer: delegate to remote A2A agents through the handoff workflow (`RemoteAgents`, `MentorRemoteAgent`, with per-agent auth `Headers`); remote agents are listed in the coordinator's system prompt. `MentorRemoteAgent.RequiredRoles` is accepted but not enforced: a startup warning says the roles are not checked.
- **MentorAgent:** Agent Skills with progressive disclosure (`EnableSkills`, `SkillsFolder`, `[MentorSkill]`).
- **MentorAgent:** RAG through `IMentorRagSource` (`UseRag`, `RagResultCount`, `RagMinScore`, `ShowRagSources`).
- **MentorAgent:** Level-3 group-chat teams for multi-agent deliberation before action (`[MentorTeam]`, `[TeamMember]`).
- **MentorAgent:** LLM-based prompt-injection and jailbreak detection (`EnableSafetyCheck`) and per-user rate limiting (`RateLimitPerUser`).
- **MentorAgent:** memory context for user preferences (`UseMemoryContext`, `MemoryContextCount`).
- **MentorAgent:** typed and async UI-action overloads: `RegisterUIAction<T>`, `RegisterUIActionAsync`, `RegisterUIActionAsync<T>`.
- **MentorAgent:** `IncludeWorkflowExceptionDetails` to surface internal errors during development.

### Changed

- **MentorAgent:** reworked system prompt compared with BlazorMentor: a Communication Style block, more natural memory instructions, and cleaner Level-2/Level-3 agent prompts.

### Fixed

- **MentorAgent:** compared with BlazorMentor: the A2A consumer threw `UriFormatException` when a remote agent card contained relative `SupportedInterfaces` URLs, and the coordinator did not route to remote A2A agents after a successful connection.

### Known issues

- **MentorAgent:** `MentorRemoteAgent.RequiredRoles` is not enforced: any user can reach a remote agent.

[Unreleased]: https://www.nuget.org/packages/Novior.MentorAgent
[1.3.1]: https://www.nuget.org/packages/Novior.MentorAgent/1.3.1
[1.3.0]: https://www.nuget.org/packages/Novior.MentorAgent/1.3.0
[1.2.0]: https://www.nuget.org/packages/Novior.MentorAgent/1.2.0
[1.1.0]: https://www.nuget.org/packages/Novior.MentorAgent/1.1.0
[1.0.0]: https://www.nuget.org/packages/Novior.MentorAgent/1.0.0
[1.0.0-rc.15]: https://www.nuget.org/packages/MentorAgent/1.0.0-rc.15
[1.0.0-rc.14]: https://www.nuget.org/packages/MentorAgent/1.0.0-rc.14
[1.0.0-rc.13]: https://www.nuget.org/packages/MentorAgent/1.0.0-rc.13
[1.0.0-rc.12]: https://www.nuget.org/packages/MentorAgent/1.0.0-rc.12
[1.0.0-rc.11]: https://www.nuget.org/packages/MentorAgent/1.0.0-rc.11
[1.0.0-rc.10]: https://www.nuget.org/packages/MentorAgent/1.0.0-rc.10
[1.0.0-rc.9]: https://www.nuget.org/packages/MentorAgent/1.0.0-rc.9
[1.0.0-rc.8]: https://www.nuget.org/packages/MentorAgent/1.0.0-rc.8
[1.0.0-rc.7]: https://www.nuget.org/packages/MentorAgent/1.0.0-rc.7
[1.0.0-rc.6]: https://www.nuget.org/packages/MentorAgent/1.0.0-rc.6
[1.0.0-rc.5]: https://www.nuget.org/packages/MentorAgent/1.0.0-rc.5
[1.0.0-rc.4]: https://www.nuget.org/packages/MentorAgent/1.0.0-rc.4
[1.0.0-rc.3]: https://www.nuget.org/packages/MentorAgent/1.0.0-rc.3
[1.0.0-rc.2]: https://www.nuget.org/packages/MentorAgent/1.0.0-rc.2
[1.0.0-rc.1]: https://www.nuget.org/packages/MentorAgent/1.0.0-rc.1
[1.0.0-preview.5]: https://www.nuget.org/packages/MentorAgent/1.0.0-preview.5
[1.0.0-preview.4]: https://www.nuget.org/packages/MentorAgent/1.0.0-preview.4
[1.0.0-preview.3]: https://www.nuget.org/packages/MentorAgent/1.0.0-preview.3
[1.0.0-preview.2]: https://www.nuget.org/packages/MentorAgent/1.0.0-preview.2
[1.0.0-preview]: https://www.nuget.org/packages/MentorAgent/1.0.0-preview
