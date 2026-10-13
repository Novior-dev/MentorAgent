
using Microsoft.Agents.AI;
using System.Reflection;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Rag;

namespace Novior.MentorAgent.Options;

/// <summary>
/// Configuration options for <c>AddMentorAgent()</c>.
/// </summary>
public sealed class MentorOptions
{


    // ── App Identity ──────────────────────────────────────────────────────────

    /// <summary>Host application name. Injected into the system prompt.</summary>
    public required string AppName { get; set; }

    /// <summary>Application domain description. More detail = more contextual AI.</summary>
    public string AppDescription { get; set; } = string.Empty;

    // ── Language & Mentorship ─────────────────────────────────────────────────

    /// <summary>
    /// Language for AI responses and the widget interface.
    /// Propagated to all agents, the Coordinator, and the MentorLocalizer.
    /// Default: English
    /// </summary>
    public MentorLanguage Language { get; set; } = MentorLanguage.English;

    /// <summary>Proactivity and mentorship level. Default: Standard.</summary>
    public MentorshipLevel MentorshipLevel { get; set; } = MentorshipLevel.Standard;

    // ── AI Provider ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Custom AI client — supports any provider implementing IChatClient.
    ///
    /// The provider's SDK is a package the host adds: Azure.AI.OpenAI for Azure, and
    /// Microsoft.Extensions.AI.OpenAI for AsIChatClient() on the OpenAI clients (it brings OpenAI).
    ///
    /// Examples:
    /// <code>
    /// // Azure OpenAI — ChatCompletion
    /// options.ChatClient = new AzureOpenAIClient(endpoint, credential)
    ///     .GetChatClient("gpt-4o-mini").AsIChatClient();
    ///
    /// // Azure OpenAI — Responses API (service-managed history)
    /// // NOTE: GetResponsesClient() takes NO argument — the deployment name goes to AsIChatClient.
    /// options.ChatClient = new AzureOpenAIClient(endpoint, credential)
    ///     .GetResponsesClient().AsIChatClient("gpt-4o-mini");
    ///
    /// // OpenAI direct — ChatCompletion
    /// options.ChatClient = new OpenAIClient("sk-...")
    ///     .GetChatClient("gpt-4o").AsIChatClient();
    ///
    /// // OpenAI direct — Responses API (same shape as Azure)
    /// options.ChatClient = new OpenAIClient("sk-...")
    ///     .GetResponsesClient().AsIChatClient("gpt-4o");
    ///
    /// // Ollama — through its OpenAI-compatible endpoint, with the same package
    /// options.ChatClient = new OpenAIClient(
    ///         new ApiKeyCredential("ollama"),   // Ollama ignores it; the SDK requires one
    ///         new OpenAIClientOptions { Endpoint = new Uri("http://localhost:11434/v1") })
    ///     .GetChatClient("llama3.2").AsIChatClient();
    /// </code>
    /// </summary>
    public IChatClient? ChatClient { get; set; }

    /// <summary>
    /// Pre-built AIAgent — use when your provider does NOT produce an IChatClient.
    ///
    /// Required for: AIProjectClient (Microsoft Foundry), AnthropicClient,
    /// AnthropicFoundryClient, and any provider whose SDK exposes .AsAIAgent() directly.
    ///
    /// MentorAgent will attach its tools (via AIContextProvider.AIContext.Tools),
    /// additional instructions (appended via AIContext.Instructions),
    /// and middleware via .AsBuilder().UseAIContextProviders(...).Use(...).
    ///
    /// ⚠️ IMPORTANT: ChatHistoryProvider cannot be added post-construction.
    /// Configure it directly in the agent you pass here.
    /// </summary>
    public AIAgent? Agent { get; set; }

    /// <summary>
    /// Optional embedding model, configured like <see cref="ChatClient"/>. When set, it enables
    /// <b>semantic</b> tool filtering (see <see cref="EnableToolFiltering"/>) — tools and the user
    /// message are compared by embedding similarity instead of keyword overlap, which is reliable
    /// and multilingual. When not set and filtering is on, every tool is sent (with a startup
    /// warning): filtering is semantic-only, and there is no keyword fallback.
    ///
    /// Example (Azure OpenAI — a separate embedding deployment):
    /// <code>
    /// options.EmbeddingGenerator = new AzureOpenAIClient(endpoint, credential)
    ///     .GetEmbeddingClient("text-embedding-3-small").AsIEmbeddingGenerator();
    /// </code>
    /// </summary>
    public IEmbeddingGenerator<string, Embedding<float>>? EmbeddingGenerator { get; set; }

    // ── Discovery ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Assemblies to scan for [Description], [MentorAction], [MentorAgent], [MentorTeam], [MentorSkill], [TeamMember].
    /// Usually: <c>options.ScanAssemblies = [typeof(Program).Assembly];</c>
    /// </summary>
    public required Assembly[] ScanAssemblies { get; set; }

    // ── UI ────────────────────────────────────────────────────────────────────

    /// <summary>Widget visual theme. Default: Default.</summary>
    public MentorTheme Theme { get; set; } = MentorTheme.Default;

    /// <summary>Widget position. Default: BottomRight.</summary>
    public ChatPosition Position { get; set; } = ChatPosition.BottomRight;

    /// <summary>Widget primary color (hex, e.g.: "#2563EB"). If null, uses the theme color.</summary>
    public string? PrimaryColor { get; set; }

    /// <summary>Bot name displayed in the widget header. Default: "Mentor AI"</summary>
    public string BotName { get; set; } = "Mentor AI";

    /// <summary>Welcome message when the chat is empty. If null, uses the default.</summary>
    public string? WelcomeMessage { get; set; }

    /// <summary>
    /// Input box placeholder text. If null, uses the localized default in <see cref="Language"/>
    /// ("Write a message..." in English).
    /// </summary>
    public string? InputPlaceholder { get; set; }

    /// <summary>Bot avatar image URL. If null, uses the built-in SVG avatar.</summary>
    public string? AvatarUrl { get; set; }

    // ── Contextual memory ───────────────────────────────────────────────────

    /// <summary>
    /// Enables contextual memory — the Mentor remembers preferences and actions
    /// across different sessions via the "remember" and "forget_all" tools.
    ///
    /// ⚠️ IMPORTANT — userId and data isolation:
    /// - With authentication configured: each user has their own isolated memory, keyed by the
    ///   ClaimTypes.NameIdentifier claim, else "sub", "oid" or the name, with the claim in the key
    ///   ("sub:…", "name:…") so a name never matches another user's identifier.
    /// - Without authentication: the key follows <see cref="AnonymousIdentity"/>. PerSession (default)
    ///   gives each circuit, hub connection or SSE request its own key, so nothing is shared and
    ///   nothing outlives the session; Shared uses the literal "anonymous" key for everyone — right
    ///   for a single-user app, a cross-user leak on a website.
    ///
    /// For production with multiple users who should keep their memory, configure authentication.
    ///
    /// For real persistence across app restarts, register your own implementation
    /// of IMentorMemoryStore BEFORE AddMentorAgent():
    /// <code>
    /// // Redis
    /// builder.Services.AddSingleton&lt;IMentorMemoryStore, MyRedisStore&gt;();
    ///
    /// // EF Core
    /// builder.Services.AddScoped&lt;IMentorMemoryStore, MyEfCoreStore&gt;();
    ///
    /// // MongoDB
    /// builder.Services.AddSingleton&lt;IMentorMemoryStore, MyMongoStore&gt;();
    ///
    /// builder.Services.AddMentorAgent(options => {
    ///     options.UseMemoryContext = true;
    ///     ...
    /// });
    /// </code>
    ///
    /// Default: false
    /// </summary>
    public bool UseMemoryContext { get; set; } = false;

    /// <summary>
    /// Who an <b>unauthenticated</b> visitor is, for memory and for the rate-limit counter.
    /// Default: <see cref="MentorAnonymousIdentity.PerSession"/> — one key per circuit/connection,
    /// so two visitors of the same site never read each other's memories.
    ///
    /// <para>
    /// Set <see cref="MentorAnonymousIdentity.Shared"/> in a <b>single-user</b> application (MAUI, a
    /// desktop tool) to get the previous behaviour, where every session shares the key
    /// <c>"anonymous"</c> and memory therefore survives a restart.
    /// </para>
    ///
    /// <para>
    /// Ignored once a user is signed in: their <c>ClaimTypes.NameIdentifier</c> is the key, as before, else
    /// their <c>sub</c>, <c>oid</c> or name with the claim in the key (<c>sub:…</c>, <c>name:…</c>). A
    /// signed-in principal with none of them is keyed as an anonymous one.
    /// </para>
    /// </summary>
    public MentorAnonymousIdentity AnonymousIdentity { get; set; } = MentorAnonymousIdentity.PerSession;

    /// <summary>
    /// Maximum number of recent memories injected into the system prompt on each call.
    /// Increase for more historical context, reduce to limit tokens.
    /// Default: 10
    /// </summary>
    public int MemoryContextCount { get; set; } = 10;

    /// <summary>
    /// Inject only the memories relevant to the current message instead of the last
    /// <see cref="MemoryContextCount"/>. Saves tokens on memory-heavy users.
    ///
    /// Relevance is <b>semantic</b>: each memory is embedded and compared to the message by
    /// cosine similarity (multilingual, no keyword lists) — consistent with semantic tool
    /// filtering. Requires <see cref="EmbeddingGenerator"/>; without it, memory silently falls
    /// back to last-N by recency. Identity/preference facts (keys starting with <c>user_</c> or
    /// <c>pref</c>) are always kept, so the AI can still greet by name and honour preferences.
    /// Default: false (last-N, preserving existing behavior).
    /// </summary>
    public bool MemoryRelevanceFiltering { get; set; }

    /// <summary>
    /// Reliably capture durable user facts with a dedicated post-turn extraction, instead of
    /// relying on the coordinator to proactively call the <c>remember</c> tool (which weaker
    /// models do inconsistently — e.g. saving on "Mi chiamo Antonio" but not on "Ciao, mi chiamo
    /// Antonio"). After each user message a small, focused LLM call extracts personal facts
    /// (name, role, team, company, location, preferences) and stores them directly.
    ///
    /// This is the single memory-writing path when active: on <b>Path A</b> (an <see cref="ChatClient"/>
    /// is configured) and <see cref="UseMemoryContext"/> is on, the redundant <c>remember</c> tool
    /// and its forceful prompt block are dropped (saving per-call tokens). On <b>Path B</b> (a
    /// pre-built <see cref="Agent"/> with no ChatClient) auto-capture cannot run, so the
    /// <c>remember</c> tool + prompt are kept as the fallback writer. The <c>forget</c> tool is
    /// always available.
    ///
    /// Cost: one lightweight model call per user message. Set to <see langword="false"/> to opt out
    /// and go back to the tool-based writing (cheaper, but the model saves facts less reliably).
    /// Default: <see langword="true"/>.
    /// </summary>
    public bool MemoryAutoCapture { get; set; } = true;

    /// <summary>
    /// When <see langword="true"/> (default), the coordinator is instructed to format answers with
    /// GitHub-flavoured Markdown — rendering tabular/comparative data as <b>tables</b> and
    /// enumerations as <b>lists</b> in the chat widget (Generative UI, Level 1). The widget always
    /// renders Markdown safely (text is HTML-encoded before any tag is emitted); this flag only
    /// controls the prompt nudge. Set to <see langword="false"/> for terse plain-text replies.
    /// Default: <see langword="true"/>.
    /// </summary>
    public bool EnableRichResponses { get; set; } = true;

    /// <summary>
    /// Renders a <see cref="Novior.MentorAgent.Abstractions.Models.MentorCard"/> returned by a tool as a
    /// real card in the chat, instead of letting the model describe it in prose — generative UI
    /// Level 2 (roadmap #14). Default: <see langword="true"/>.
    /// <para>
    /// Level 1 (<see cref="EnableRichResponses"/>) is Markdown the model writes. This is the level
    /// above: your tool returns the structure, so fields, labels, links and buttons come from your
    /// code and the model only chooses when to call the tool. A card can therefore carry actions
    /// safely — nothing the conversation says can add a button or change where one points.
    /// </para>
    /// <para>
    /// Set to <see langword="false"/> to fall back to text: the cards are still returned to the
    /// model, so the assistant keeps answering, it just describes the data instead of showing it.
    /// </para>
    /// </summary>
    public bool EnableGenerativeCards { get; set; } = true;

    // ── Wave 5 · #13 Multimodal image input ───────────────────────────────────

    /// <summary>
    /// Enables image attachments in the chat (roadmap #13). When on, the widget shows an attach button
    /// and accepts uploaded / pasted / dragged images and image URLs; the user turn is then sent as a
    /// multimodal <c>ChatMessage</c> (text + image content). Requires a <b>vision-capable</b>
    /// <see cref="ChatClient"/> (e.g. gpt-4o / gpt-4.1). Default: <see langword="false"/>.
    /// </summary>
    public bool EnableImageInput { get; set; } = false;

    /// <summary>Maximum size, in bytes, of a single inline image attachment (larger is rejected). Default: 4 MB.</summary>
    public int MaxImageBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>Maximum number of images per message. Default: 4.</summary>
    public int MaxImagesPerMessage { get; set; } = 4;

    /// <summary>Allowed image MIME types (allow-list, per AF Agent Safety). Default: png, jpeg, gif, webp.</summary>
    public IReadOnlyList<string> AllowedImageTypes { get; set; } =
        ["image/png", "image/jpeg", "image/gif", "image/webp"];

    // ── Wave 5 · #15 Hosted tools ─────────────────────────────────────────────

    /// <summary>
    /// Provider-hosted tools to expose to the coordinator (roadmap #15): web search, code
    /// interpreter, file search, image generation, hosted MCP. They are <b>declared, not executed</b>
    /// by MentorAgent — the model provider runs them on its own infrastructure during inference, so no
    /// code runs on your servers.
    ///
    /// <code>
    /// options.HostedTools = MentorHostedTools.WebSearch | MentorHostedTools.CodeInterpreter;
    /// </code>
    ///
    /// ⚠️ <b>Provider support is not universal.</b> Requires <see cref="ChatClient"/> (Path A). The
    /// Azure OpenAI / OpenAI <i>Responses</i> client supports the full set; the <i>Chat Completions</i>
    /// client supports web search only, and asking it for the others makes the provider reject the
    /// call. MentorAgent logs the enabled set at startup so a misconfiguration is visible.
    /// Default: <see cref="MentorHostedTools.None"/>.
    /// </summary>
    public MentorHostedTools HostedTools { get; set; } = MentorHostedTools.None;

    /// <summary>
    /// Vector store ids searched by <see cref="MentorHostedTools.FileSearch"/> — created by uploading
    /// your documents to the provider (Azure OpenAI / OpenAI vector stores, Foundry). Required when
    /// file search is enabled: without at least one id the tool is skipped and a warning is logged,
    /// because an unbounded file search is both useless and a data-exposure risk.
    /// </summary>
    public IReadOnlyList<string>? FileSearchVectorStoreIds { get; set; }

    /// <summary>
    /// Optional bound on how many matches <see cref="MentorHostedTools.FileSearch"/> returns. Lower =
    /// fewer tokens injected into the answer. <see langword="null"/> (default) lets the provider decide.
    /// </summary>
    public int? FileSearchMaxResults { get; set; }

    /// <summary>
    /// Model used by <see cref="MentorHostedTools.ImageGeneration"/> (e.g. <c>gpt-image-1-mini</c>).
    ///
    /// <para>
    /// ⚠️ <b>On Azure OpenAI this option is not enough on its own.</b> Azure resolves the image
    /// deployment from the <c>x-ms-oai-image-generation-deployment</c> <b>request header</b>, not from
    /// the tool payload — without it every image turn fails with <i>"imagegen deployment must be
    /// provided through header"</i>. MentorAgent is handed an already-built <see cref="ChatClient"/>,
    /// so it cannot add the header: attach it as a pipeline policy where you construct the
    /// <c>AzureOpenAIClient</c>. This option still carries the model id for providers that read it
    /// (OpenAI proper), and is logged at startup together with the header reminder.
    /// </para>
    /// </summary>
    public string? HostedImageModel { get; set; }

    /// <summary>
    /// Size of the generated image as <c>WIDTHxHEIGHT</c> (e.g. <c>"1024x1024"</c>). This is the cost
    /// knob for image generation — a larger image is billed more — so set it rather than inheriting a
    /// provider default you did not choose. An unparsable value is ignored with a warning.
    /// <see langword="null"/> (default) lets the provider decide.
    /// </summary>
    public string? HostedImageSize { get; set; }

    /// <summary>
    /// Remote MCP servers the <b>provider</b> connects to, used by
    /// <see cref="MentorHostedTools.HostedMcp"/>. Required when hosted MCP is enabled: with an empty
    /// list the tool is skipped and a warning is logged.
    ///
    /// <para>
    /// Distinct from <see cref="McpServers"/>, where your own process is the MCP client. Here the
    /// server must be reachable from the provider's network, and approval is enforced by the provider
    /// through <see cref="MentorHostedMcpServer.RequireApproval"/>.
    /// </para>
    /// </summary>
    public IReadOnlyList<MentorHostedMcpServer>? HostedMcpServers { get; set; }

    /// <summary>
    /// Let the semantic tool filter decide, per turn, whether each provider-hosted tool is relevant to
    /// the user's message — instead of declaring them on every turn. Default: <see langword="true"/>.
    ///
    /// <para>
    /// Hosted tools are the expensive ones (a web search is billed per call, a file-search turn injects
    /// thousands of extra input tokens), yet they were the only group the filter never touched: they are
    /// not <c>AIFunction</c>s and carry no description to embed. Descriptions now come from an internal
    /// catalogue, so they are scored exactly like every other tool.
    /// </para>
    ///
    /// <para>
    /// Requires <see cref="EnableToolFiltering"/> and an <see cref="EmbeddingGenerator"/>. Without them
    /// nothing is filtered and a warning is logged — <b>fail-open</b>: an assistant must not lose
    /// capabilities because a model was not configured.
    /// </para>
    /// </summary>
    public bool FilterHostedTools { get; set; } = true;

    /// <summary>
    /// Similarity threshold for hosted tools specifically. <see langword="null"/> (default) uses
    /// <c>0.15</c> — deliberately <b>lower</b> than <see cref="ToolFilterMinScore"/>, not equal to it.
    ///
    /// <para>
    /// Two reasons it must be its own, permissive value. First, the scores are not on the same scale:
    /// hosted tools are matched through long English descriptions while your actions carry short
    /// descriptions in the user's own language, so their scores run systematically lower and a
    /// threshold tuned for one population mis-fires on the other. Second, this filter is now a coarse
    /// pre-cut, not the decision — <see cref="HostedToolDomainCheck"/> makes the judgement call, so
    /// this only needs to drop what is plainly irrelevant.
    /// </para>
    ///
    /// <para>
    /// Measured on real turns: a shop assistant asked to generate an image scored <c>web_search</c>
    /// 0.04–0.07, <c>code_interpreter</c> 0.05–0.08, <c>image_generation</c> 0.29–0.35. At 0.35 the
    /// verdict flipped between two rewordings of the same request; at 0.15 it is stable and the
    /// irrelevant tools are still dropped. Raising this to "be safe" reintroduces exactly that
    /// flakiness. Every score is logged at Debug, so tune from data rather than intuition.
    /// </para>
    /// </summary>
    public float? HostedToolFilterMinScore { get; set; }

    /// <summary>
    /// Before declaring a provider-hosted tool, ask a small model whether the message is within this
    /// application's scope at all. Default: <see langword="false"/>.
    ///
    /// <para>
    /// A different question from <see cref="FilterHostedTools"/>. That one asks "does this message need
    /// <i>this tool</i>?" — and <c>"draw me a dog"</c> passes, because it genuinely is an image request.
    /// This one asks "does it have anything to do with <i>this application</i>?", which is what stops an
    /// e-commerce assistant from paying to generate a dog.
    /// </para>
    ///
    /// <para>
    /// It runs <b>once per turn, and only when a hosted tool has already passed relevance filtering</b>
    /// — that is, only when money is about to be spent anyway. A couple of hundred tokens against a
    /// per-call web search or a generated image is three orders of magnitude in the right direction.
    /// <b>Fails open</b>: a classifier that errors or answers unexpectedly lets the turn through.
    /// </para>
    ///
    /// <para>
    /// This deliberately does <i>not</i> use embedding similarity. Raw cosine has no stable zero point:
    /// measured on a real turn, <c>"genera immagine di un cane"</c> scored 0.398 against a shop's page
    /// names — high not because it was related but because both were short phrases in the same language
    /// — and the same request crossed the threshold on a one-word rewording.
    /// </para>
    /// </summary>
    public bool HostedToolDomainCheck { get; set; }

    /// <summary>
    /// The scope sentence the classifier judges against. <see langword="null"/> (default) derives it
    /// from <see cref="AppName"/>, <see cref="AppDescription"/> and the application's page names and,
    /// when hosted tools are enabled, appends them with the rule that they do not widen the subject
    /// (BUG-050) — a cost rule, strict on purpose. When set, it is also the subject
    /// <see cref="RefuseOutOfScope"/> judges against, verbatim.
    ///
    /// <para>
    /// Worth writing yourself when the derived text is too narrow — a shop assistant may legitimately
    /// field questions about shipping rules or VAT that no page covers — or when it is too broad, since
    /// a vague description makes the classifier permissive. One or two concrete sentences beat a
    /// paragraph.
    /// </para>
    /// </summary>
    public string? HostedToolDomainScope { get; set; }

    /// <summary>
    /// Replaces the built-in classifier with your own decision (<see langword="true"/> = in scope).
    /// Use it to reuse an existing intent classifier, apply per-user policy, or make the check free.
    /// Same shape as <see cref="InputGuardrail"/>.
    /// </summary>
    public Func<string, CancellationToken, Task<bool>>? HostedToolDomainClassifier { get; set; }

    /// <summary>
    /// Maximum provider-hosted tool calls allowed in one session (Blazor circuit / hub connection);
    /// beyond it they stop being declared. <c>0</c> (default) means no cap.
    ///
    /// <para>
    /// Complementary to <see cref="FilterHostedTools"/>, not an alternative: relevance scoring is
    /// probabilistic and lowers how often an expensive tool fires on a message that did not need it,
    /// while a counter is the only thing that puts a ceiling on a session's spend.
    /// </para>
    /// </summary>
    public int MaxHostedToolCallsPerSession { get; set; }

    /// <summary>
    /// Show the hosted-tools badge in the widget header (which provider tools are active), mirroring
    /// <see cref="ShowMcpStatus"/>. Useful in development; leave off in production. Default: false.
    /// </summary>
    public bool ShowHostedToolsStatus { get; set; }

    /// <summary>
    /// Report hosted-tool activity in the widget while the answer streams — "Searching the web…",
    /// "Running code…", the queries the provider issued, the pages it cited, the images it generated.
    ///
    /// <para>
    /// Hosted tools run remotely and can take several seconds during which nothing is streamed, so
    /// without this the widget looks frozen. Web results are shown through the same citation chips as
    /// RAG (they also need <see cref="ShowRagSources"/>). Default: true.
    /// </para>
    /// </summary>
    public bool ShowHostedToolActivity { get; set; } = true;

    /// <summary>
    /// Set this when the <see cref="ChatClient"/> keeps the conversation on the <b>service</b> rather
    /// than in your process — the OpenAI / Azure OpenAI <b>Responses</b> client (which is what hosted
    /// tools require), Microsoft Foundry agents, Copilot Studio.
    ///
    /// <para>
    /// Those services return a conversation id, and the Agent Framework refuses to combine that with a
    /// local <c>ChatHistoryProvider</c> — every turn would fail with
    /// <i>"Only ConversationId or ChatHistoryProvider may be used, but not both"</i>. With this flag on,
    /// MentorAgent installs no history provider and lets the service own the conversation.
    /// </para>
    ///
    /// <para>
    /// Consequences: <see cref="MaxSessionMessages"/> and <see cref="EnableCompaction"/> no longer apply
    /// (there is no local history to trim — a warning is logged if compaction is on), and any
    /// <see cref="ChatHistoryProvider"/> you configured is ignored. Everything else — tools, memory,
    /// RAG, guardrails, metrics — is unaffected. Default: <see langword="false"/>.
    /// </para>
    /// </summary>
    public bool UseServiceManagedHistory { get; set; }

    // ── Wave 5 · #16 Human-in-the-loop tool approval ──────────────────────────

    /// <summary>
    /// How tool approval is implemented (roadmap #16). <see cref="MentorHitlMode.Blocking"/> (default)
    /// keeps MentorAgent's own flow — one round-trip, streaming stays alive.
    /// <see cref="MentorHitlMode.Native"/> switches to the Agent Framework standard
    /// (<c>ApprovalRequiredAIFunction</c> + <c>ToolApprovalRequestContent</c>/<c>ToolApprovalResponseContent</c>)
    /// for interop with AF workflows and AF-native hosts.
    ///
    /// <para>
    /// The user-facing banner and the client protocol are identical in both modes, so this is safe to
    /// flip without touching any client. Only meaningful with <see cref="RequireConfirmation"/> on.
    /// </para>
    /// </summary>
    public MentorHitlMode HitlMode { get; set; } = MentorHitlMode.Blocking;

    /// <summary>
    /// Rules that auto-approve a tool call which would otherwise raise a confirmation banner, using
    /// the Agent Framework's own <c>ToolApprovalAgent</c> middleware. Evaluated in order; the first
    /// rule returning <see langword="true"/> approves the call. Only applies when
    /// <see cref="HitlMode"/> is <see cref="MentorHitlMode.Native"/>, which is the mode whose
    /// approvals travel as framework content rather than through MentorAgent's own banner.
    ///
    /// <para>
    /// Enabling this also enables the framework's <b>"don't ask again"</b> behaviour: when a user
    /// approves a call, a standing rule is recorded for the rest of that session and matching calls
    /// stop prompting.
    /// </para>
    ///
    /// <para>
    /// <b>This is an approval gate, not an authorization gate.</b> Auto-approving a call does not
    /// bypass <c>[MentorAction(RequiredRoles = …)]</c> — the role check lives inside MentorAgent's
    /// own tool wrapper and still refuses a caller who lacks the role. One is a prompt the user sees,
    /// the other is who is allowed to act, and a rule here can only ever waive the first.
    /// </para>
    ///
    /// Usage:
    /// <code>
    /// options.HitlMode = MentorHitlMode.Native;
    /// options.AutoApprovalRules =
    /// [
    ///     // A scheduled job has no one to answer a dialog: approve reads, never writes.
    ///     ctx =&gt; ValueTask.FromResult(ctx.FunctionCallContent.Name.StartsWith("get_")),
    /// ];
    /// </code>
    ///
    /// Default: empty — every gated tool prompts, exactly as before.
    /// </summary>
    public IList<Func<Microsoft.Agents.AI.ToolAutoApprovalRuleContext, ValueTask<bool>>> AutoApprovalRules
    { get; set; } = [];

    /// <summary>
    /// Safety cap on how many times a single turn may be re-run because every surfaced approval was
    /// auto-approved by <see cref="AutoApprovalRules"/>. Each re-invocation is a fresh, billable
    /// model call, and a per-request cap such as the function-invoking client's own iteration limit
    /// restarts every time and cannot bound this loop.
    ///
    /// <para>
    /// The same shape as BUG-018, where a custom team termination condition removed the iteration
    /// cap instead of tightening it and ran 770 model calls against a limit of 2. A model that keeps
    /// requesting an auto-approved tool is an unbounded spend, and this is the only thing that stops
    /// it. Leave it at the framework default unless you have measured a reason.
    /// </para>
    ///
    /// Default: null (the Agent Framework's own default cap applies)
    /// </summary>
    public int? MaxAutoApprovalIterations { get; set; }

    /// <summary>
    /// Optional predicate that forces human approval for a tool by name, <b>on top of</b>
    /// <c>[MentorAction(RequiresConfirmation = true)]</c> and
    /// <c>MentorMcpServer.RequiresConfirmation</c>. Return <see langword="true"/> to gate the tool.
    ///
    /// <para>
    /// Use it to apply the Agent Framework's Agent Safety guidance to tools you do not own — MCP
    /// tools and skills: gate anything with side effects, that touches sensitive data, or that cannot
    /// be undone. It receives tool names: an MCP tool under the name its server gives it, with no
    /// server prefix. A remote A2A agent is reached by handoff, not by a tool call, and never passes here.
    /// </para>
    ///
    /// <code>
    /// options.RequiresApproval = name =>
    ///     name.StartsWith("delete_") || name is "send_email" or "write_file";
    /// </code>
    ///
    /// Evaluated once per tool while the coordinator is built, so it must be a pure function of the
    /// tool name. <see langword="null"/> (default) = only the declared confirmations apply.
    /// </summary>
    public Func<string, bool>? RequiresApproval { get; set; }

    /// <summary>
    /// Optional predicate that declares, by tool name, the actions that only read — the same declaration
    /// as <c>[MentorAction(ReadOnly = true)]</c>, for actions marked only with <c>[Description]</c> or to
    /// declare them in one place. MentorAgent takes your word for it, as an MCP client takes a server's
    /// <c>readOnlyHint</c>: such actions are published to MCP clients with that hint, and with
    /// <see cref="RequireLookupForRecordQuestions"/> on they are the ones the assistant may be required to
    /// call before answering a question about a value of your records.
    ///
    /// <code>
    /// options.IsReadOnlyAction = name => name.StartsWith("get_") || name.StartsWith("search_");
    /// </code>
    ///
    /// A tool that needs confirmation is never read-only, whatever this returns. Must be a pure function
    /// of the name. <see langword="null"/> (default) = only the attribute declares.
    /// </summary>
    public Func<string, bool>? IsReadOnlyAction { get; set; }

    // ── Wave 3 · #7 Middleware suite ──────────────────────────────────────────

    /// <summary>
    /// Optional custom input guardrail. Return <see langword="true"/> if the message is safe to
    /// process, <see langword="false"/> to block it. When set it runs on every message (whether or
    /// not <see cref="EnableSafetyCheck"/> is on) and <b>replaces</b> the built-in LLM
    /// prompt-injection check. Leave <see langword="null"/> to use the built-in check (gated by
    /// <see cref="EnableSafetyCheck"/>).
    /// </summary>
    public Func<string, CancellationToken, Task<bool>>? InputGuardrail { get; set; }

    /// <summary>
    /// Optional output guardrail that moderates the assistant's <b>completed</b> reply before it is
    /// shown. Return <see langword="true"/> if the reply is safe, <see langword="false"/> to replace
    /// it with a blocked message. <b>Important:</b> when set, the reply for that turn is buffered and
    /// revealed all at once after moderation (no live streaming) so unmoderated text is never shown.
    /// Leave <see langword="null"/> (default) to keep live streaming with no output moderation.
    /// </summary>
    public Func<string, CancellationToken, Task<bool>>? OutputGuardrail { get; set; }

    /// <summary>
    /// Built-in <b>output</b> safety check — the counterpart of <see cref="EnableSafetyCheck"/> for the
    /// reply. When <see langword="true"/>, the assistant's completed reply is classified by the LLM and
    /// blocked (replaced with a safe message) if it leaks system instructions/secrets, discloses
    /// someone's personal data, or contains harmful content. Like the output guardrail, the reply is
    /// buffered and revealed after moderation (no live streaming that turn). A custom
    /// <see cref="OutputGuardrail"/> takes precedence over this built-in check. Requires a
    /// <see cref="ChatClient"/> (Path A); adds one lightweight model call per reply. Default:
    /// <see langword="false"/>.
    /// </summary>
    public bool EnableOutputSafetyCheck { get; set; }

    /// <summary>
    /// Optional hook to transform or redact a tool result before it is returned to the model
    /// (Result Overrides). Receives the tool name and its raw result; return the (possibly modified)
    /// result. Runs for every tool invocation, before the result reaches the model — and on the MCP
    /// server (<c>tools/call</c>) before it reaches the MCP caller, with the same input (BUG-107). A hook
    /// that throws there withholds the result: the caller gets an <c>isError</c> result.
    /// <see langword="null"/> = no transformation.
    /// </summary>
    public Func<string, object?, object?>? OnToolResult { get; set; }

    /// <summary>
    /// Optional hook to map an exception to a user-facing message. Return a message to show the user,
    /// or <see langword="null"/> to fall back to the built-in friendly mapping (content filter, rate
    /// limit, timeout, generic). Applies to turn-level and tool-level failures.
    /// <para>
    /// For a tool that throws, the message goes to the chat and to the model. With the hook set the chat
    /// never shows the exception's own message: <see langword="null"/>, an empty string or a hook that
    /// throws shows the localized "The action could not be completed." The model then gets the
    /// exception's message, which it needs to answer; return a message for any exception whose text
    /// must not reach it. Without the hook both get the exception's message. The log has only the
    /// exception's type, and the whole exception at Debug.
    /// </para>
    /// </summary>
    public Func<Exception, string?>? OnException { get; set; }

    /// <summary>
    /// Public extension point for custom middleware. Invoked while assembling the Path A
    /// (<see cref="ChatClient"/>) pipeline, letting you insert your own <c>DelegatingChatClient</c>
    /// or Agent Framework middleware via the builder (e.g. logging, caching, extra guardrails).
    /// Applied outermost, after MentorAgent's own middleware. <see langword="null"/> = none.
    /// </summary>
    public Func<ChatClientBuilder, ChatClientBuilder>? ConfigureChatClientPipeline { get; set; }

    // ── Wave 3 · #8 Observability (OpenTelemetry) ─────────────────────────────

    /// <summary>
    /// Emit OpenTelemetry traces/metrics (GenAI semantic conventions) from the chat client and
    /// embedding generator, plus MentorAgent-level spans/counters. The host app wires the exporter
    /// (App Insights, Aspire, OTLP) and adds the sources/meters. Default: <see langword="false"/>.
    /// </summary>
    public bool EnableObservability { get; set; }

    /// <summary>
    /// Include sensitive data (prompt and completion content) in telemetry. <b>Development only</b> —
    /// leave <see langword="false"/> in production to avoid logging user/model content. Default: false.
    /// </summary>
    public bool ObservabilityIncludeSensitiveData { get; set; }

    // The default is not a namespace: hosts write .AddSource("MentorAgent").AddMeter("MentorAgent"), so it
    // stays when the namespace changes (ContractNameTests).
    /// <summary>
    /// Name of the <c>ActivitySource</c>/<c>Meter</c> MentorAgent emits under. The host must add this
    /// name via <c>.AddSource(name).AddMeter(name)</c> on its OpenTelemetry setup. Default: "MentorAgent".
    /// </summary>
    public string ObservabilitySourceName { get; set; } = "MentorAgent";

    // ── Wave 3 · #9 Token & cost dashboard ────────────────────────────────────

    /// <summary>
    /// Per-model token prices for the admin dashboard's cost estimate. Keyed by model id. When
    /// <see langword="null"/> (default) or a model is missing, tokens are shown without cost.
    /// MentorAgent ships no built-in price table — you supply current prices.
    /// </summary>
    public IReadOnlyDictionary<string, ModelPrice>? ModelPricing { get; set; }

    /// <summary>
    /// Role required by the admin metrics endpoint, <c>GET /mentor/admin/metrics</c> (Novior.MentorAgent.Server);
    /// "" leaves it open (development only). The <c>MentorDashboard</c> component does not read it: protect
    /// the page that hosts the component yourself, for example with <c>[Authorize(Roles = "Admin")]</c>.
    /// Default: "Admin".
    /// </summary>
    public string DashboardRole { get; set; } = "Admin";

    /// <summary>
    /// How often the durable-dashboard persistence service flushes the current snapshot to a registered
    /// <c>IMentorMetricsStore</c> (it also flushes once on graceful shutdown). Only active when a custom
    /// store is registered. Default: 30 seconds.
    /// </summary>
    public TimeSpan MetricsPersistenceInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How far back the dashboard keeps per-model time-series buckets (roadmap #9 temporal charts).
    /// Older hourly buckets are pruned. Default: 7 days.
    /// </summary>
    public TimeSpan MetricsRetention { get; set; } = TimeSpan.FromDays(7);

    // ── Wave 4 · #11 Model routing ────────────────────────────────────────────

    /// <summary>
    /// Optional <b>strong</b> model, used for the turns <see cref="RoutingStrategy"/> sends to it;
    /// <see cref="ChatClient"/> is the <b>cheap</b> default — a real cost lever (small model for simple
    /// turns, strong model for complex ones). Routing is active when this is set and the strategy can
    /// decide: <see cref="MentorRoutingStrategy.Custom"/> needs <see cref="UseStrongModel"/> or
    /// <see cref="UseStrongModelAsync"/>, <see cref="MentorRoutingStrategy.Semantic"/> an
    /// <see cref="EmbeddingGenerator"/>; <see cref="MentorRoutingStrategy.Classifier"/> and
    /// <see cref="MentorRoutingStrategy.Cascade"/> need nothing more.
    /// </summary>
    public IChatClient? StrongChatClient { get; set; }

    /// <summary>
    /// Selects how the cheap↔strong decision is made when <see cref="StrongChatClient"/> is set
    /// (roadmap #11). Default <see cref="MentorRoutingStrategy.Custom"/> — uses
    /// <see cref="UseStrongModelAsync"/> / <see cref="UseStrongModel"/>. Set to
    /// <see cref="MentorRoutingStrategy.Semantic"/>, <see cref="MentorRoutingStrategy.Classifier"/>
    /// or <see cref="MentorRoutingStrategy.Cascade"/> for a built-in professional router.
    /// </summary>
    public MentorRoutingStrategy RoutingStrategy { get; set; } = MentorRoutingStrategy.Custom;

    /// <summary>
    /// Legacy synchronous predicate (<see cref="MentorRoutingStrategy.Custom"/>): decides, from the
    /// latest user message, whether to escalate to <see cref="StrongChatClient"/>
    /// (<see langword="true"/>) or stay on the cheap <see cref="ChatClient"/>. Prefer
    /// <see cref="UseStrongModelAsync"/>, which is async and sees the whole conversation; avoid naive
    /// keyword/length rules here.
    /// </summary>
    public Func<string, bool>? UseStrongModel { get; set; }

    /// <summary>
    /// Async, context-aware custom router (<see cref="MentorRoutingStrategy.Custom"/>): given the whole
    /// conversation, returns <see langword="true"/> to escalate to <see cref="StrongChatClient"/>.
    /// Takes precedence over <see cref="UseStrongModel"/> when both are set — lets you call your own
    /// classifier/embedding logic. The decision applies to the whole turn.
    /// </summary>
    public Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<bool>>? UseStrongModelAsync { get; set; }

    /// <summary>
    /// <see cref="MentorRoutingStrategy.Semantic"/> only: example phrases describing the kind of turn
    /// that should go to the strong model. The user message is escalated when its cosine similarity to
    /// the nearest exemplar reaches <see cref="RoutingThreshold"/>. <see langword="null"/> uses a
    /// built-in multilingual default set (analysis, comparison, long-form, coding, multi-step reasoning).
    /// </summary>
    public IReadOnlyList<string>? RoutingComplexExemplars { get; set; }

    /// <summary>
    /// <see cref="MentorRoutingStrategy.Semantic"/> only: cosine-similarity floor [0..1]. The turn is
    /// escalated to the strong model when the best exemplar score is at least this value. Higher =
    /// stricter (fewer escalations). Default: 0.35 (same scale as tool filtering).
    /// </summary>
    public float RoutingThreshold { get; set; } = 0.35f;

    /// <summary>
    /// <see cref="MentorRoutingStrategy.Classifier"/> / <see cref="MentorRoutingStrategy.Cascade"/> only:
    /// optional dedicated client for the routing judgment. Defaults to the cheap
    /// <see cref="ChatClient"/> so the routing decision itself stays inexpensive.
    /// </summary>
    public IChatClient? RoutingClassifierClient { get; set; }

    /// <summary>
    /// Optional client for MentorAgent's own yes/no decisions — the input and output safety checks,
    /// the hosted-tool scope check and the post-turn fact extraction. Defaults to <see cref="ChatClient"/>.
    ///
    /// <para>
    /// These calls send a few hundred tokens and read back one word, and they sit <b>in front of</b>
    /// the answer the user is waiting for: measured live on gpt-4.1, the safety check alone took
    /// 707–2091 ms, about half the time to the first character. A small, fast deployment
    /// (<c>gpt-4.1-mini</c> and the like) answers the same question in a fraction of that and at a
    /// fraction of the price, with no change in behaviour.
    /// </para>
    ///
    /// <code>
    /// options.ClassifierChatClient = azure.GetChatClient("gpt-4.1-mini").AsIChatClient();
    /// </code>
    ///
    /// <para>
    /// Its calls are metered like every other, so the dashboard shows what the classifier costs
    /// under its own model id.
    /// </para>
    /// </summary>
    public IChatClient? ClassifierChatClient { get; set; }

    /// <summary>
    /// Refuse, politely and without calling the coordinator, a message that is not about this
    /// application. Default: <see langword="false"/> — every message is answered, which is what a
    /// general-purpose assistant should do.
    ///
    /// <para>
    /// It asks a question of its own, which defaults to answering (BUG-105): a name the classifier does
    /// not recognise may be one of the application's own records, so a question about its price, stock
    /// or status is answered; greetings, follow-ups, requests about the conversation and questions about
    /// the assistant are answered; only a subject that plainly has nothing to do with the application —
    /// creative writing, the weather, sport — is refused, and when unsure the message is answered. It
    /// used to reuse the hosted-tool cost gate's strict sentence, and refused catalogue price questions.
    /// </para>
    ///
    /// <para>
    /// With <see cref="EnableSafetyCheck"/> on it travels in the same classifier call; on its own it adds
    /// one small call per turn. The hosted-tool check (<see cref="HostedToolDomainCheck"/>) then asks its
    /// own, strict question — only on a turn where a hosted tool is about to be declared.
    /// </para>
    ///
    /// <para>
    /// Judged against <see cref="HostedToolDomainScope"/> when set, otherwise the application's own
    /// name, description, and pages with their descriptions — never the hosted-tool capability rule. The
    /// refusal is localized and the turn ends there — no tools, no coordinator call, nothing billed for
    /// the answer — and it is counted in <c>GuardrailBlockCount</c>.
    /// </para>
    /// </summary>
    public bool RefuseOutOfScope { get; set; }

    /// <summary>
    /// Build one coordinator at start-up instead of making the first visitor wait for it.
    /// Default: <see langword="false"/>.
    ///
    /// <para>
    /// The work it moves off that first message is the shared kind: embedding the tool catalogue and
    /// the routing exemplars into the process cache, opening the shared MCP sessions, fetching the
    /// remote agent cards. Measured on the sample, the first message of a cold process took 3 seconds
    /// before a token appeared, and up to 50 when several circuits were starting their own MCP
    /// servers at once.
    /// </para>
    ///
    /// <para>
    /// It runs once the host has started (after a web host starts listening) and a failure of the
    /// warm-up itself never fails the boot: an application that refuses to start because a remote
    /// MCP server is down would be the worse failure. An invalid configuration does stop the host,
    /// because the options are validated when it starts. It needs a generic host (ASP.NET Core, a
    /// worker): a MAUI app runs no hosted service, so there the option does nothing.
    /// </para>
    /// </summary>
    public bool WarmUpAtStartup { get; set; }

    // ── Behavior ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Maximum number of messages in the built-in in-memory session history. Default: 50.
    /// <para>
    /// Every message counts, tool calls and tool results included, and the history is trimmed by
    /// whole turns, oldest first (<see cref="Novior.MentorAgent.Core.MentorChatReducer"/>), so the data a
    /// tool read stays visible to the model on the next turn. <c>0</c> or negative: no trimming (a
    /// warning is logged). Not read when <see cref="ChatReducer"/> is set, nor for a
    /// <see cref="ChatHistoryProvider"/> / <see cref="ChatHistoryProviderFactory"/> you supply, nor
    /// with <see cref="UseServiceManagedHistory"/>.
    /// </para>
    /// </summary>
    public int MaxSessionMessages { get; set; } = 50;

    /// <summary>
    /// Replaces the reducer that keeps the built-in in-memory history bounded. Default <c>null</c>:
    /// a <see cref="Novior.MentorAgent.Core.MentorChatReducer"/> sized by <see cref="MaxSessionMessages"/>,
    /// which trims whole turns and keeps every tool call together with its result.
    /// <para>
    /// When set, it is used as it is and <see cref="MaxSessionMessages"/> is not read. One instance
    /// serves every session, so it must not keep per-conversation state (the framework's reducers
    /// do not). It applies only to the built-in history: a <see cref="ChatHistoryProvider"/> /
    /// <see cref="ChatHistoryProviderFactory"/> you supply is used as it is, and with
    /// <see cref="UseServiceManagedHistory"/> there is no local history; both cases log a warning.
    /// </para>
    /// <example>
    /// The reducer used up to 1.0.0-rc.14, which drops tool calls and results from the history:
    /// <code>
    /// #pragma warning disable MEAI001 // MessageCountingChatReducer is experimental
    /// options.ChatReducer = new MessageCountingChatReducer(50);
    /// #pragma warning restore MEAI001
    /// </code>
    /// </example>
    /// </summary>
    public IChatReducer? ChatReducer { get; set; }

    // ── Token & Cost optimization (Wave 1) ───────────────────────────────────

    /// <summary>
    /// Enables history compaction (Wave 1 #5) — intelligently shrinks the in-flight
    /// conversation context before each LLM call instead of just truncating by count.
    ///
    /// When enabled, MentorAgent installs a non-LLM compaction pipeline:
    /// collapse old tool results → keep the last <see cref="CompactionMaxTurns"/> turns →
    /// hard token-budget truncation backstop at <see cref="CompactionTokenThreshold"/>.
    ///
    /// ⚠️ Only for the agent MentorAgent builds (Path A / ChatClient) with history kept locally — the
    /// default in-memory provider, <see cref="ChatHistoryProviderFactory"/> or a provider instance. It
    /// shrinks what is SENT, never what is stored (BUG-104). Not installed with
    /// <see cref="UseServiceManagedHistory"/> (Foundry, Responses API with store), where the service
    /// keeps the history; a startup warning says so.
    /// Uses the experimental Agent Framework compaction API.
    /// Default: false
    /// </summary>
    public bool EnableCompaction { get; set; } = false;

    /// <summary>
    /// Token budget that triggers compaction (and the truncation backstop).
    /// Only used when <see cref="EnableCompaction"/> is true. Default: 4000
    /// </summary>
    public int CompactionTokenThreshold { get; set; } = 4000;

    /// <summary>
    /// Maximum number of recent user turns kept by the sliding-window step of compaction.
    /// Only used when <see cref="EnableCompaction"/> is true. Default: 8
    /// </summary>
    public int CompactionMaxTurns { get; set; } = 8;

    /// <summary>
    /// Enables dynamic tool filtering (Wave 1 #4) — before each LLM call, only the tools
    /// semantically relevant to the user's latest message are sent, cutting the per-call token
    /// cost of tool schemas (often the single largest contributor).
    ///
    /// <b>Requires <see cref="EmbeddingGenerator"/>.</b> Relevance is computed by embedding
    /// similarity, which ranks tools by meaning across languages; the absolute score is not calibrated.
    /// When no action clears <see cref="ToolFilterMinScore"/>, the three best-ranked actions that need no
    /// confirmation and no role (an MCP tool only when its server marks it read-only) are sent anyway — a question
    /// that needs data can score below a greeting (BUG-106) — so give every action that changes data
    /// <c>RequiresConfirmation</c>. When only actions that need confirmation clear it, the three best-ranked
    /// that do not travel with them (BUG-099). If this is enabled but no embedding model is configured,
    /// filtering is skipped and <b>all tools are sent</b> (a warning is logged) — there is no keyword
    /// fallback.
    ///
    /// Safe by design: core tools (navigation, memory, routing, skills, UI actions) are
    /// always kept. Verify the effect with the token counter.
    /// Default: false
    /// </summary>
    public bool EnableToolFiltering { get; set; } = false;

    /// <summary>
    /// Maximum number of matched business tools to keep when <see cref="EnableToolFiltering"/>
    /// is active. Core tools are always kept on top of this. Default: 12
    /// </summary>
    public int ToolFilterMaxTools { get; set; } = 12;

    /// <summary>
    /// When <see cref="EnableToolFiltering"/> keeps at least one action for a message, keep at least this
    /// many: the best-ranked ones, whatever their score, up to <see cref="ToolFilterMaxTools"/>. It does not
    /// apply to a message where no action clears <see cref="ToolFilterMinScore"/>: that one gets the three
    /// best-ranked actions that need no confirmation and no role (see <see cref="EnableToolFiltering"/>).
    /// Default: 0 — the threshold alone decides, as before this option existed.
    ///
    /// <para>
    /// A trade-off, measured both ways (BUG-099). Asked "Quante unità di iPad Air ci sono in
    /// magazzino?", a shop's stock-update action (0.351) was the only one above 0.35 and the actions that
    /// read stock ranked just below; sent alone, the write action was called with a quantity of 0.
    /// With 3, the readers travelled with it and nothing was written. But asked "Quanto ha speso Anna
    /// Ferrari?", the lone match above the line was the right search (0.361), and with 3 the model picked
    /// a by-email lookup the floor had added and answered "no such customer" four times of four, where
    /// the threshold alone answered correctly four of four. Neither setting tells a wrong lone match from
    /// a right one. The protection against a write answering a read is
    /// <c>[MentorAction(RequiresConfirmation = true)]</c> on actions that change data.
    /// </para>
    /// </summary>
    public int ToolFilterMinTools { get; set; } = 0;

    /// <summary>
    /// Opt-in. When a classifier model says a message asks for a value of your records — a price, a stock
    /// level, an order's status — the assistant's first model call must call one of the actions you declared
    /// read-only (<c>[MentorAction(ReadOnly = true)]</c>, <see cref="IsReadOnlyAction"/>, an MCP tool with
    /// <c>readOnlyHint</c>) that semantic tool filtering selected for that message — the one that cleared
    /// <see cref="ToolFilterMinScore"/>, the companions of a confirmation-gated match, or the declared reads
    /// among the three actions the zero-match fallback sent — or hand the request to a specialist or a team.
    /// The reply is then written from what it returned. An action that needs a role (<c>RequiredRoles</c>) or a
    /// confirmation is never required.
    ///
    /// <para>
    /// Why: with the read actions on the turn the model still answered "I can't see the price, shall I
    /// look it up?" about one time in three on the ApiServer sample (BUG-106), and with a read that matched
    /// on its own it could still write "price not available" without calling it; stronger wording in the
    /// prompt made it invent figures instead. Forcing is structural: the first thing the model produces is
    /// the call.
    /// </para>
    ///
    /// <para>
    /// What changes: on those turns the reply starts after the result, so a Proactive "I'm looking it
    /// up…" sentence no longer comes first; one small classifier call (<see cref="ClassifierChatClient"/>
    /// when set, bounded by <see cref="SafetyCheckTimeout"/>) is paid on every turn whose selection holds a
    /// declared read — with filtering's zero-match fallback, that is nearly every turn, greetings included.
    /// With <see cref="RequireActionForChangeRequests"/> on too, the same call answers both questions.
    /// Without <see cref="EnableToolFiltering"/>, an <see cref="EmbeddingGenerator"/> and at least one declared
    /// read-only action that needs no role, or on Path B, it has no effect, and a startup warning says so. A
    /// classifier that fails or answers anything unreadable leaves the turn as it would be without this.
    /// Default: false
    /// </para>
    /// </summary>
    public bool RequireLookupForRecordQuestions { get; set; } = false;

    /// <summary>
    /// Opt-in. When a classifier model says a message asks to carry out one of your actions that ask for
    /// confirmation — and gives every input that action needs — the assistant's first model call must call one of
    /// those actions that semantic tool filtering selected for that message, or hand the request to a specialist
    /// or a team: the confirmation banner is then shown before anything is said. When it says the message asks to
    /// go to one of your pages (<c>[MentorPage]</c>), the first call must be <c>navigate_to</c>.
    ///
    /// <para>
    /// Why (BUG-118): asked "Imposta lo stock dell'iPad Air a 30", the assistant answered "Stock aggiornato" with
    /// no tool called — no banner, nothing changed — about one time in five on the ApiServer sample, and "Ecco la
    /// pagina Prodotti" without navigating. A rule in the prompt cannot stop it: worded generally it would also
    /// forbid reporting what a specialist did, or an action of an earlier turn.
    /// </para>
    ///
    /// <para>
    /// A request that leaves out something the action needs is never forced. The classifier is shown the inputs
    /// each selected action requires — the schema's required parameters, with their type and description — and
    /// answers that it is not a complete request when an input is missing ("Annulla l'ordine 1004" when
    /// <c>cancel_order</c> needs a reason), when the entry is only described ("the last order"), when a name is
    /// given where the input is an ID, or when the new value has to be worked out from the current one ("add 5
    /// units"). The model is then free to ask, as without this option — a forced call would have had to invent the
    /// missing value, and the banner would have shown it as the user's. Required means required in the schema: an
    /// input the action declares optional is not asked for first, whatever its <c>ProactiveHint</c> says — make it
    /// required if it must be. An action whose input schema cannot be shown faithfully (a <c>$ref</c>, a
    /// combination of schemas, parameters none of which is marked required) is never required.
    /// </para>
    ///
    /// <para>
    /// Only actions that will ask a person first are ever required: declared confirmation
    /// (<c>[MentorAction(RequiresConfirmation = true)]</c>, <see cref="RequiresApproval"/>, an MCP server with
    /// <c>RequiresConfirmation</c>) with <see cref="RequireConfirmation"/> on. With <see cref="MentorHitlMode.Native"/>,
    /// no <see cref="AutoApprovalRules"/> rule is consulted for a required call — it is always shown to a person; the
    /// calls the model makes on its own are the rules' to decide, as before. A page request is matched against your
    /// <c>[MentorPage]</c> pages: a page the application does not have, or something within the page already open,
    /// is not forced. A request from another agent is never required to navigate, and its confirmation-gated
    /// actions are refused there as always. The turn that answers an approval a Stop left open is never forced.
    /// </para>
    ///
    /// <para>
    /// What changes: on those turns the banner, or the page, comes before any sentence. One small classifier
    /// call (<see cref="ClassifierChatClient"/> when set, bounded by <see cref="SafetyCheckTimeout"/>) is paid on
    /// every turn while the application has a page or the message selected such an action — shared with
    /// <see cref="RequireLookupForRecordQuestions"/> when both are on. Without <see cref="EnableToolFiltering"/>
    /// and an <see cref="EmbeddingGenerator"/>, or on Path B, it has no effect, and a startup warning says so. A
    /// classifier that fails or answers anything unreadable leaves the turn as it would be without this.
    /// Default: false
    /// </para>
    /// </summary>
    public bool RequireActionForChangeRequests { get; set; } = false;

    /// <summary>
    /// Minimum cosine similarity (0–1) a tool must reach to count as a match in <b>semantic</b> tool
    /// filtering (requires <see cref="EmbeddingGenerator"/>). Higher = stricter (fewer tools sent). Cosine
    /// is a ranking, not a calibrated relevance: this line decides which actions match, not whether a
    /// message needs one (BUG-106). Default: 0.35
    /// </summary>
    public float ToolFilterMinScore { get; set; } = 0.35f;

    /// <summary>Shows proactive suggestion chips. Default: true</summary>
    public bool EnableSuggestions { get; set; } = true;

    /// <summary>Visual feedback during tool call execution. Default: true</summary>
    public bool EnableActionFeedback { get; set; } = true;

    /// <summary>
    /// On/off for the confirmation dialogs of your actions (<c>[MentorAction(RequiresConfirmation = true)]</c>, and
    /// the undeclared writes of <see cref="RequireConfirmationForWrites"/>), of MCP-client servers with
    /// <c>MentorMcpServer.RequiresConfirmation</c>, and of <see cref="RequiresApproval"/>. Default: true
    /// <para>
    /// A hosted MCP server's approvals (<see cref="MentorHostedMcpServer.RequireApproval"/>,
    /// <see cref="MentorHostedMcpServer.AlwaysRequireApprovalTools"/>) still show the banner: the
    /// provider asks for them, and they are set on that server. Switching this off silences the banner only:
    /// <c>RequiredRoles</c> keeps refusing a caller without the role.
    /// </para>
    /// </summary>
    public bool RequireConfirmation { get; set; } = true;

    /// <summary>
    /// A <c>[MentorAction]</c> that declares neither <c>ReadOnly = true</c> nor <c>RequiresConfirmation</c> is
    /// treated as an action that changes data, and asks for confirmation like one declared
    /// <c>RequiresConfirmation = true</c>. Default: true.
    /// <para>
    /// An action is excluded by declaring it: <c>ReadOnly = true</c> for one that only reads (or
    /// <see cref="IsReadOnlyAction"/>), <c>RequiresConfirmation = false</c> to run a write without asking.
    /// Being gated, such an action is also withheld from the MCP server and from the A2A card, like every
    /// confirmation-gated action: nobody is there to confirm.
    /// </para>
    /// <para>
    /// <b>It covers <c>[MentorAction]</c> methods only.</b> A method marked with <c>[Description]</c> alone asks for
    /// no confirmation and is published on <c>/mcp</c>, as before: mark it <c>[MentorAction]</c> to have this
    /// default. At startup a warning lists both kinds of write that run without confirmation.
    /// </para>
    /// <para>
    /// Set to false for the behaviour up to 1.0.0-rc.14, where an action asked only when it declared
    /// <c>RequiresConfirmation = true</c>.
    /// </para>
    /// </summary>
    public bool RequireConfirmationForWrites { get; set; } = true;

    // ── Storage ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Custom provider for conversation history persistence.
    ///
    /// If null, uses InMemoryChatHistoryProvider (default — data lost on restart).
    ///
    /// For real persistence, register a custom provider BEFORE AddMentorAgent():
    /// <code>
    /// // CosmosDB
    /// options.ChatHistoryProvider = new CosmosChatHistoryProvider(cosmosClient, "db", "container");
    ///
    /// // Custom (implement ChatHistoryProvider)
    /// options.ChatHistoryProvider = new MyRedisChatHistoryProvider(redisConn);
    /// </code>
    ///
    /// Ref: https://learn.microsoft.com/en-us/agent-framework/agents/conversations/storage
    /// </summary>
    public ChatHistoryProvider? ChatHistoryProvider { get; set; }

    /// <summary>
    /// Builds a <see cref="ChatHistoryProvider"/> <b>once per coordinator</b> — that is, once per
    /// circuit, per SignalR connection, per MAUI app instance. Prefer this to
    /// <see cref="ChatHistoryProvider"/> in any application with more than one user.
    ///
    /// <para>
    /// <b>Why it exists (BUG-055).</b> <see cref="ChatHistoryProvider"/> is a single object on
    /// options, which are a singleton — so one instance serves every user in the process. The
    /// default is the opposite: with both left null MentorAgent constructs a fresh
    /// <c>InMemoryChatHistoryProvider</c> inside each coordinator build, isolated by construction.
    /// Setting the instance property therefore <i>inverts</i> the isolation model, and the natural
    /// implementation — one class, one <c>List&lt;ChatMessage&gt;</c> — then replays every user's
    /// conversation to every other user. Driven live, a private code written by one authenticated
    /// principal came back verbatim to two others.
    /// </para>
    ///
    /// <para>
    /// The factory receives the coordinator's <see cref="IServiceProvider"/>, so a provider can
    /// resolve whatever identifies the current user in your application and key its storage by it.
    /// </para>
    ///
    /// <code>
    /// // In-memory, isolated because each scope gets its own instance:
    /// options.ChatHistoryProviderFactory = _ => new InMemoryChatHistoryProvider();
    ///
    /// // Shared backing store, partitioned by the caller:
    /// options.ChatHistoryProviderFactory = sp =>
    ///     new CosmosChatHistoryProvider(cosmos, "db", "conversations",
    ///         partitionKey: sp.GetRequiredService&lt;IUserContext&gt;().UserId);
    /// </code>
    ///
    /// <para>
    /// Setting this <b>and</b> <see cref="ChatHistoryProvider"/> throws when the host starts (the
    /// options are validated on start; without a generic host, the first time they are read): they
    /// answer the same question with opposite isolation semantics, and picking one silently would
    /// make an application's safety depend on which line was written last.
    /// </para>
    /// </summary>
    public Func<IServiceProvider, ChatHistoryProvider>? ChatHistoryProviderFactory { get; set; }

    // ── Voice ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Enables microphone in the widget — native browser Speech Recognition API.
    /// Supported on Chrome/Edge. Firefox partial. Requires HTTPS in production.
    /// Default: false
    /// </summary>
    public bool EnableVoiceInput { get; set; } = false;

    /// <summary>
    /// Enables speech synthesis — the Mentor reads responses aloud.
    /// Native browser Speech Synthesis API. Supported on all modern browsers.
    /// Default: false
    /// </summary>
    public bool EnableVoiceOutput { get; set; } = false;

    /// <summary>
    /// Speaks each sentence as it streams, instead of reading the finished answer back (roadmap #17).
    /// Default: <see langword="true"/>.
    /// <para>
    /// Voice output used to wait for the whole response, so the assistant stayed silent for the
    /// length of a paragraph and then recited it. Sentence-by-sentence playback keeps pace with the
    /// text. Turn it off only if your answers are reliably one line long.
    /// </para>
    /// </summary>
    public bool VoiceStreaming { get; set; } = true;

    /// <summary>
    /// Stops playback the moment the user takes the floor — presses the microphone or sends a
    /// message (roadmap #17). Default: <see langword="true"/>.
    /// <para>
    /// This is press-to-interrupt, not acoustic barge-in: a browser cannot reliably listen through
    /// its own playback, and a microphone left open during synthesis transcribes the assistant.
    /// </para>
    /// </summary>
    public bool VoiceBargeIn { get; set; } = true;

    /// <summary>
    /// Hands-free conversation: the transcript is sent when the user stops speaking, and the
    /// microphone reopens once the spoken answer finishes (roadmap #17). Default:
    /// <see langword="false"/> — it holds the microphone across turns, which no application should
    /// do without being asked. Requires <see cref="EnableVoiceInput"/> and
    /// <see cref="EnableVoiceOutput"/>.
    /// </summary>
    public bool VoiceHandsFree { get; set; } = false;

    /// <summary>
    /// Speech rate passed to <c>SpeechSynthesisUtterance.rate</c>. 1.0 is the browser default and
    /// the useful range is roughly 0.5–2.0. Default: 1.0.
    /// </summary>
    public double VoiceRate { get; set; } = 1.0;

    /// <summary>
    /// Shows a short guided tour the first time a user opens the widget (roadmap #18).
    /// Default: <see langword="false"/>.
    /// <para>
    /// The steps are generated once per process from the pages registered with
    /// <c>[MentorPage]</c> and the assistant's own tools, so the tour describes the application
    /// as it is rather than as it was when someone wrote the script. Each step can carry a
    /// ready-made question, which is the part that actually helps: users do not fail to use an
    /// in-app assistant because they cannot find it, they fail because they do not know what to
    /// ask it.
    /// </para>
    /// <para>
    /// Register your own <see cref="Novior.MentorAgent.Abstractions.Interfaces.IMentorTour"/> before
    /// <c>AddMentorAgent()</c> to write the tour by hand.
    /// </para>
    /// </summary>
    public bool EnableOnboardingTour { get; set; } = false;

    // ── Security ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Enables AI safety check on input messages.
    /// Before processing each message, asks the LLM whether it is a prompt injection,
    /// jailbreak, or sensitive data extraction attempt.
    /// Works in any language automatically.
    /// ⚠️ Adds one extra API call per message (~200-500ms).
    /// Default: false
    /// </summary>
    public bool EnableSafetyCheck { get; set; } = false;

    /// <summary>
    /// How long a safety check may take before it counts as failed — the input check
    /// (<see cref="EnableSafetyCheck"/> or <see cref="InputGuardrail"/>) and the output check
    /// (<see cref="EnableOutputSafetyCheck"/> or <see cref="OutputGuardrail"/>). What happens then is
    /// <see cref="SafetyCheckFailure"/>. Default: 15 seconds.
    ///
    /// <para>
    /// The input check runs <b>before</b> the turn and blocks it, so it needs a bound: a provider that
    /// never answers would otherwise leave the assistant busy for good. On a hosted model it takes
    /// 0.7–2 s (measured on gpt-4.1); on a local model running on a CPU a single call can take close
    /// to a minute, and 15 s then fails every time. Raise it, or point the checks at a fast
    /// <see cref="ClassifierChatClient"/>.
    /// </para>
    ///
    /// <para>
    /// <see cref="TimeSpan.Zero"/> or any negative value (including
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>) means <b>no limit</b>. The user can
    /// still press Stop: a check waiting on the provider is cancelled with the turn, whatever this is.
    /// From configuration: <c>"SafetyCheckTimeout": "00:01:30"</c>; <c>"00:00:00"</c> for no limit.
    /// </para>
    /// </summary>
    public TimeSpan SafetyCheckTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// What a safety check does when it cannot reach a verdict: it timed out
    /// (<see cref="SafetyCheckTimeout"/>), the provider returned an error, or a custom guardrail
    /// threw. <see cref="MentorSafetyCheckFailure.Allow"/> (default) processes the message and shows
    /// the reply; <see cref="MentorSafetyCheckFailure.Block"/> refuses the message, or withholds the
    /// reply, with a localized "try again" message. Both log a warning.
    ///
    /// <para>
    /// A verdict of UNSAFE blocks under both settings — this is only about having no verdict. A
    /// provider-side content filter that rejects the input check's own call counts as UNSAFE, not as
    /// a failure.
    /// </para>
    /// </summary>
    public MentorSafetyCheckFailure SafetyCheckFailure { get; set; } = MentorSafetyCheckFailure.Allow;

    /// <summary>
    /// Maximum message length in characters.
    /// 0 = unlimited.
    /// Default: 4000
    /// </summary>
    public int MaxMessageLength { get; set; } = 4000;

    // ── Rate Limiting ─────────────────────────────────────────────────────────

    /// <summary>
    /// Maximum number of messages per user within the time window.
    /// 0 = disabled (default).
    /// E.g.: 20 → max 20 messages every RateLimitWindowSecs seconds.
    ///
    /// ⚠️ IMPORTANT — User identification:
    /// - With ASP.NET authentication configured: each user is identified by their
    ///   <c>ClaimTypes.NameIdentifier</c> claim, else <c>sub</c>, <c>oid</c> or their name — the key memory
    ///   uses. Each user gets an independent counter.
    /// - Without authentication: the key follows <see cref="AnonymousIdentity"/>. PerSession (default)
    ///   gives every circuit, hub connection and SSE request its own counter — a reload, a reconnect
    ///   or a new request starts from zero, so this does not bound an anonymous caller's spend.
    ///   Shared makes one global counter for every anonymous visitor.
    ///   For a limit that holds per person, configure ASP.NET Core authentication.
    ///
    /// The MCP server's <c>tools/call</c> is bounded too (BUG-107), with the same limit and window but an
    /// allowance of its own — a tool call is not a model turn, and an automation must not lock its user
    /// out of the chat. The caller is the principal <c>McpCallerPrincipal</c> resolves, else the request's
    /// user, counted by its <c>NameIdentifier</c>, <c>sub</c> or <c>oid</c> claim (else its name). A caller
    /// with none is counted by remote address when <c>IHttpContextAccessor</c> is registered (behind a
    /// proxy, apply forwarded headers); otherwise every such caller shares one allowance — never one per
    /// MCP session, which costs nothing to open — and one busy client can use it up for the others.
    /// A refused call is answered with an <c>isError</c> result.
    /// </summary>
    public int RateLimitPerUser { get; set; } = 0;

    /// <summary>
    /// Duration in seconds of the rate limiting window. Default: 60.
    /// E.g.: 60 → the window resets every minute.
    /// </summary>
    public int RateLimitWindowSecs { get; set; } = 60;

    // ── MCP Client (consume external MCP servers) ────────────────────────────

    /// <summary>
    /// External MCP (Model Context Protocol) servers to connect to as a client.
    /// Each server's tools are automatically added to the coordinator as L1 tools.
    ///
    /// Example:
    /// <code>
    /// options.McpServers = [
    ///     new MentorMcpServer { Name = "filesystem", Command = "npx",
    ///         Arguments = ["-y", "@modelcontextprotocol/server-filesystem", "/tmp"] }
    /// ];
    /// </code>
    /// </summary>
    public MentorMcpServer[]? McpServers { get; set; }

    // ── MCP Server (expose MentorAgent as MCP server) ───────────────────────

    /// <summary>
    /// Resolves the <see cref="System.Security.Claims.ClaimsPrincipal"/> behind an MCP request, so
    /// that role-gated actions can be published to callers entitled to them (ROADMAP #32).
    ///
    /// <para>
    /// <b>Default <see langword="null"/>, and the default is the safe one.</b> With no resolver,
    /// nothing an author protected is published at all — the behaviour BUG-021 and BUG-035 forced,
    /// on the only surface where a tool is invoked by someone who is not the application's own
    /// user.
    /// </para>
    ///
    /// <para>
    /// Supply this only when your host can genuinely say <i>who</i> is calling — an mTLS subject, a
    /// validated bearer token, a gateway-injected header you trust. Then a
    /// <c>[MentorAction(RequiredRoles = [...])]</c> becomes publishable, and it is published
    /// <b>only</b> when the resolved principal is authenticated and holds one of those roles. This
    /// is an identity, not an override: a resolver that returns <see langword="null"/>, returns an
    /// unauthenticated principal, or throws falls back to withholding.
    /// </para>
    ///
    /// <para>
    /// Roles are tested with <see cref="System.Security.Claims.ClaimsPrincipal.IsInRole"/>, which
    /// honours each identity's <c>RoleClaimType</c> (a <c>roles</c> claim works, not only
    /// <c>ClaimTypes.Role</c>, once the identity names it: with JwtBearer and
    /// <c>MapInboundClaims = false</c>, set <c>TokenValidationParameters.RoleClaimType = "roles"</c>, or every
    /// role-gated action stays withheld). The resolver is called <b>once per MCP request</b> — <c>tools/list</c>
    /// and <c>tools/call</c> alike — and its answer is never cached per caller (BUG-103), so keep it
    /// cheap: read the request's already-validated principal, do not introspect a token per call.
    /// </para>
    ///
    /// <para>
    /// <b>Confirmation gates are never satisfied by it.</b> <c>RequiresConfirmation</c> and
    /// <see cref="RequiresApproval"/> both mean "ask a human first", and there is no human on the
    /// far end of an MCP call. Those actions stay unpublished however privileged the caller —
    /// waiving them is exactly what BUG-021 was about.
    /// </para>
    ///
    /// <code>
    /// builder.Services.AddHttpContextAccessor();
    /// builder.Services.AddOptions&lt;MentorOptions&gt;().Configure&lt;IHttpContextAccessor&gt;(
    ///     (options, http) =&gt; options.McpCallerPrincipal = () =&gt; http.HttpContext?.User);
    /// </code>
    /// </summary>
    public Func<System.Security.Claims.ClaimsPrincipal?>? McpCallerPrincipal { get; set; }

    /// <summary>
    /// Set to true to expose MentorAgent as an MCP server.
    /// The server lives in the Novior.MentorAgent.Server package: also call
    /// <c>builder.Services.AddMentorAgentMcp()</c> after <c>AddMentorAgent()</c> (<c>AddMentorAgentServer()</c>
    /// includes it) and <c>app.MapMentorAgentMcp()</c> in Program.cs to map the endpoint.
    /// Default: false
    /// </summary>
    public bool McpServerEnabled { get; set; }

    /// <summary>
    /// Endpoint path for the MCP server, used by <c>app.MapMentorAgentMcp()</c> when no path is passed
    /// to it. Default: "/mcp"
    /// </summary>
    public string McpServerPath { get; set; } = "/mcp";

    /// <summary>
    /// Show MCP connection status in the widget header.
    /// Useful during development. Default: false (hidden in production).
    /// </summary>
    public bool ShowMcpStatus { get; set; }

    // ── A2A Consumer (connect to remote A2A agents) ──────────────────────────

    /// <summary>
    /// Remote A2A (Agent-to-Agent) agents to include in the Handoff workflow.
    /// Each remote agent is discoverable from its agent card URL and will appear
    /// in the coordinator's routing options alongside local [MentorAgent] classes.
    ///
    /// Example:
    /// <code>
    /// options.RemoteAgents = [
    ///     new MentorRemoteAgent {
    ///         Name = "InventoryAgent",
    ///         Description = "Manages warehouse operations",
    ///         AgentCardUrl = "https://inventory.example.com"   // the base URL: the card is read from /.well-known/agent-card.json
    ///     }
    /// ];
    /// </code>
    /// </summary>
    public MentorRemoteAgent[]? RemoteAgents { get; set; }

    /// <summary>
    /// Show remote A2A agent status in the widget header.
    /// Displays a badge listing all configured remote agents.
    /// Useful during development. Default: false.
    /// </summary>
    public bool ShowA2AStatus { get; set; }

    // ── A2A Server (expose MentorAgent as A2A agent) ────────────────────────

    /// <summary>
    /// Set to true to expose MentorAgent as an A2A agent.
    /// The agent lives in the Novior.MentorAgent.Server package: also call
    /// <c>builder.Services.AddMentorAgentA2A()</c> after <c>AddMentorAgent()</c> (<c>AddMentorAgentServer()</c>
    /// includes it) and <c>app.MapMentorAgentA2A()</c> in Program.cs to map the endpoints.
    /// Exposes: <c>/.well-known/agent-card.json</c> (Agent Card) + the A2A HTTP endpoint.
    /// Default: false
    /// </summary>
    public bool A2AServerEnabled { get; set; }

    /// <summary>
    /// Path of the A2A task endpoint, used by <c>app.MapMentorAgentA2A()</c> when no path is passed to
    /// it. The agent card stays at <c>/.well-known/agent-card.json</c>. When other agents call this one,
    /// set <see cref="A2AServerUrl"/> to the full public URL <b>including</b> this path. Default: "/a2a"
    /// </summary>
    public string A2AServerPath { get; set; } = "/a2a";

    /// <summary>
    /// The public URL of this agent's A2A endpoint, e.g. "http://localhost:5001/a2a".
    /// This value is written into the agent card served at /.well-known/agent-card.json
    /// so that remote orchestrators know where to POST tasks.
    ///
    /// Required when other MentorAgent instances (or any A2A client) need to call
    /// this agent as a remote. If null, the card's interface carries only the path (<see cref="A2AServerPath"/>):
    /// a MentorAgent caller resolves it against the card's address, another A2A client may not.
    ///
    /// Example (second instance on port 5001):
    /// <code>
    /// options.A2AServerUrl = "http://localhost:5001/a2a";
    /// </code>
    /// </summary>
    public string? A2AServerUrl { get; set; }

    /// <summary>
    /// Optional override for the auto-generated Agent Card metadata.
    /// If null, the card is built from AppName, AppDescription, and discovered tools.
    /// </summary>
    public AgentCardInfo? AgentCard { get; set; }

    // ── RAG (Retrieval-Augmented Generation) ─────────────────────────────────

    /// <summary>
    /// Enables RAG — before each coordinator call the knowledge base is searched
    /// and relevant documents are injected into the system prompt.
    ///
    /// Requires an <see cref="IMentorRagSource"/> registered in DI. An <c>ITextSearch</c> is not wrapped for
    /// you (<c>TextSearchRagSource</c> is a placeholder): call it from your own <see cref="IMentorRagSource"/>.
    /// Default: false
    /// </summary>
    public bool UseRag { get; set; }

    /// <summary>
    /// Maximum number of documents to retrieve per query. Default: 5
    /// </summary>
    public int RagResultCount { get; set; } = 5;

    /// <summary>
    /// Template for the RAG context injection in the system prompt.
    /// Use <c>{documents}</c> as placeholder for the retrieved content.
    /// Default: "Use the following documents to answer:\n{documents}", in English whatever
    /// <see cref="Language"/> is.
    /// </summary>
    public string RagSystemPromptTemplate { get; set; } =
        "Use the following documents to answer:\n{documents}";

    /// <summary>
    /// Show citation chips (source links) below AI responses that used RAG context.
    /// Requires sources to include a <c>SourceUrl</c> or <c>Title</c>.
    /// Default: false
    /// </summary>
    public bool ShowRagSources { get; set; }

    /// <summary>
    /// Minimum relevance score a document must reach to be included in the RAG context.
    /// Documents below this threshold are discarded before injection into the prompt
    /// and before being shown as source chips.
    ///
    /// The scale depends on the <see cref="IMentorRagSource"/> implementation:
    /// - Full-text / keyword search (e.g. <c>ShopFlowRagSource</c>): integer-like scores
    ///   where 1 = single term match in content, 3 = title match.
    ///   A value of <c>2</c> requires at least two content matches or one title match.
    /// - Vector / cosine similarity search: scores in [0, 1].
    ///   Typical thresholds: 0.5 (loose) to 0.75 (strict).
    ///
    /// Which threshold applies: this option when the host sets it (in code or through configuration
    /// binding); otherwise the source's own <see cref="IMentorRagSource.DefaultMinScore"/> when it declares
    /// one — a vector source should; otherwise this option's default, 2.
    ///
    /// Set to <c>0</c> to disable filtering (return all results from <c>SearchAsync</c>).
    /// Default: 2
    /// </summary>
    public float RagMinScore
    {
        get => _ragMinScore;
        set
        {
            _ragMinScore = value;
            RagMinScoreSetByHost = true;
        }
    }

    private float _ragMinScore = 2f;

    /// <summary>
    /// True once <see cref="RagMinScore"/> has been assigned — by the host's code or by configuration
    /// binding, which goes through the same setter. Until then the source's
    /// <see cref="IMentorRagSource.DefaultMinScore"/> may replace the default.
    /// </summary>
    internal bool RagMinScoreSetByHost { get; private set; }

    // ── Agent Skills ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Enables Agent Skills — domain knowledge packages with progressive disclosure.
    ///
    /// When true, MentorAgent discovers skills from:
    /// <list type="bullet">
    ///   <item>File-based: <c>{SkillsFolder}/{name}/SKILL.md</c></item>
    ///   <item>Class-based: classes decorated with <c>[MentorSkill]</c> in <see cref="ScanAssemblies"/></item>
    /// </list>
    ///
    /// The skill catalogue is injected into the system prompt; full instructions are
    /// loaded on demand via the <c>load_skill</c> tool (~100 tokens overhead per skill
    /// instead of loading all instructions upfront).
    ///
    /// Usage:
    /// <code>
    /// // Skills/expense-report/SKILL.md  → auto-discovered
    ///
    /// [MentorSkill(Name = "refund-policy", Description = "Return and refund rules",
    ///     Instructions = "## Refund Policy\n- Items returnable within 30 days...")]
    /// public class RefundPolicySkill { }
    ///
    /// options.EnableSkills = true;
    /// </code>
    ///
    /// Default: false
    /// </summary>
    public bool EnableSkills { get; set; } = false;

    /// <summary>
    /// Folder to scan for file-based skills.
    /// Each sub-directory must contain a <c>SKILL.md</c> file.
    /// Can be absolute or relative to the application content root.
    /// Default: "Skills"
    /// </summary>
    public string SkillsFolder { get; set; } = "Skills";

    /// <summary>
    /// Additional skill sources, using the Agent Framework's own <c>AgentSkillsSource</c>
    /// abstraction. Skills they yield reach the assistant through the same <c>load_skill</c> /
    /// <c>read_skill_resource</c> tools, the same catalogue prompt and the same gate as skills
    /// MentorAgent discovered itself — a host can therefore serve skills from a database, a remote
    /// catalogue, or Microsoft's own file source, without MentorAgent knowing where they came from.
    ///
    /// <para>
    /// The list is composed with the framework's decorators:
    /// <c>AggregatingAgentSkillsSource</c> preserves your registration order,
    /// <c>FilteringAgentSkillsSource</c> applies <see cref="SkillFilter"/> when set,
    /// <c>DeduplicatingAgentSkillsSource</c> keeps the first occurrence of a name, and
    /// <c>CachingAgentSkillsSource</c> is added when <see cref="SkillsRefreshInterval"/> is set.
    /// </para>
    ///
    /// <para>
    /// <b>An application skill always wins a name collision</b> — sources are consulted after
    /// MentorAgent's own discovery, and a duplicate name is skipped with a warning naming it. A
    /// host source can add capability; it cannot silently replace one of yours.
    /// </para>
    ///
    /// <para>
    /// <b>The framework's file source reads a different dialect.</b> <c>AgentFileSkillsSource</c>
    /// validates each <c>SKILL.md</c> for YAML frontmatter, while MentorAgent's own convention is a
    /// heading followed by a prose paragraph. Both work — register the framework's source here and
    /// keep <see cref="SkillsFolder"/> for yours.
    /// </para>
    ///
    /// Usage:
    /// <code>
    /// options.EnableSkills = true;
    /// options.SkillSources =
    /// [
    ///     // Skills written to the Agent Skills specification, with YAML frontmatter
    ///     new AgentFileSkillsSource("/srv/shared-skills"),
    ///     new MyDatabaseSkillsSource(connectionString),
    /// ];
    /// </code>
    ///
    /// Default: empty — nothing here runs and skills behave exactly as before.
    /// </summary>
    public IList<Microsoft.Agents.AI.AgentSkillsSource> SkillSources { get; set; } = [];

    /// <summary>
    /// Filters the skills contributed by <see cref="SkillSources"/>. Return <c>true</c> to keep a
    /// skill. Applied through the framework's <c>FilteringAgentSkillsSource</c>, and it runs
    /// <em>before</em> de-duplication, so filtering a name out lets the next source offering that
    /// name through rather than leaving a hole.
    ///
    /// <para>
    /// Does not apply to skills MentorAgent discovered itself — those are your own code, and a
    /// predicate that silently removed them would be a debugging trap.
    /// </para>
    ///
    /// Usage:
    /// <code>
    /// // Only publish skills the current deployment is compatible with
    /// options.SkillFilter = skill =&gt; skill.Frontmatter.Compatibility is null
    ///                              || skill.Frontmatter.Compatibility.Contains("v2");
    /// </code>
    ///
    /// Default: null (every skill from every source is kept)
    /// </summary>
    public Func<Microsoft.Agents.AI.AgentSkill, bool>? SkillFilter { get; set; }

    /// <summary>
    /// How long a skill list fetched from <see cref="SkillSources"/> stays fresh before it is
    /// re-fetched. When set, the composition is wrapped in the framework's
    /// <c>CachingAgentSkillsSource</c> and shared by every session in the process: sessions reuse the
    /// list, and the first one built after the interval has passed fetches it again.
    ///
    /// <para>
    /// Without it (the default) every new session — each circuit, hub connection or SSE request —
    /// re-reads the sources when its coordinator is built, and keeps that list for its lifetime. Set
    /// it for a source that is remote or expensive.
    /// </para>
    ///
    /// Usage:
    /// <code>
    /// options.SkillsRefreshInterval = TimeSpan.FromMinutes(15);   // re-read the remote catalogue
    /// </code>
    ///
    /// Default: null (no cache: the sources are read once per session)
    /// </summary>
    public TimeSpan? SkillsRefreshInterval { get; set; }

    // ── Validazione ───────────────────────────────────────────────────────────

    /// <summary>
    /// When true, exception messages from workflow agents are included in the agent response.
    /// Useful for debugging. Do NOT enable in production.
    /// Default: false
    /// </summary>
    public bool IncludeWorkflowExceptionDetails { get; set; } = false;

    internal void Validate()
    {
        // Caught here rather than at coordinator build, because the consequence of getting it wrong
        // is one user reading another's conversation — and that must not wait until the first
        // message to be discovered (BUG-055).
        if (ChatHistoryProvider is not null && ChatHistoryProviderFactory is not null)
            throw new InvalidOperationException(
                "[MentorAgent] Both ChatHistoryProvider and ChatHistoryProviderFactory are set. " +
                "They answer the same question with opposite isolation semantics — the instance is " +
                "shared by every user in the process, the factory builds one per coordinator — so " +
                "choosing between them here would make your application's isolation depend on which " +
                "line was written last. Set exactly one; prefer ChatHistoryProviderFactory when the " +
                "application has more than one user.");

        if (ChatClient is null && Agent is null)
            throw new InvalidOperationException(
                "[MentorAgent] Configure ChatClient or Agent. The provider's SDK is a package you add yourself.\n\n" +
                "  // Azure OpenAI — packages Azure.AI.OpenAI and Microsoft.Extensions.AI.OpenAI:\n" +
                "  options.ChatClient = new AzureOpenAIClient(endpoint, credential).GetChatClient(\"gpt-4o-mini\").AsIChatClient();\n" +
                "  // OpenAI — package Microsoft.Extensions.AI.OpenAI:\n" +
                "  options.ChatClient = new OpenAIClient(\"sk-...\").GetChatClient(\"gpt-4o\").AsIChatClient();\n" +
                "  // Ollama, through its OpenAI-compatible endpoint — package Microsoft.Extensions.AI.OpenAI:\n" +
                "  options.ChatClient = new OpenAIClient(new ApiKeyCredential(\"ollama\"), new OpenAIClientOptions { Endpoint = new Uri(\"http://localhost:11434/v1\") }).GetChatClient(\"llama3.2\").AsIChatClient();\n\n" +
                "  // Microsoft Foundry — package Microsoft.Agents.AI.Foundry (version: \"Provider package versions\" in the README):\n" +
                "  options.Agent = new AIProjectClient(endpoint, credential).AsAIAgent(model: \"gpt-4o-mini\", instructions: \"...\");\n" +
                "  // Anthropic — package Microsoft.Agents.AI.Anthropic:\n" +
                "  options.Agent = new AnthropicClient() { APIKey = apiKey }.AsAIAgent(model: \"claude-haiku-4-5\", instructions: \"...\");");

        if (ChatClient is not null && Agent is not null)
            throw new InvalidOperationException(
                "[MentorAgent] Set ChatClient OR Agent, not both.");

        if (string.IsNullOrWhiteSpace(AppName))
            throw new InvalidOperationException("[MentorAgent] AppName is required.");

        if (ScanAssemblies is not { Length: > 0 })
            throw new InvalidOperationException(
                "[MentorAgent] ScanAssemblies cannot be empty. " +
                "Use: options.ScanAssemblies = [typeof(Program).Assembly];");
    }
}