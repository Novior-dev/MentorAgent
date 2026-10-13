using System.Security.Claims;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Fakes;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Novior.MentorAgent.Tests.Harness;

/// <summary>
/// Boots MentorAgent in a real DI container over <see cref="TestApp"/>'s surface.
///
/// <para>
/// This deliberately calls the real <c>AddMentorAgent()</c> rather than newing up the services a
/// test needs. Registration order, <c>TryAdd</c> semantics and the option snapshot taken before
/// configuration are themselves behaviour — several documented seams ("register your store
/// <em>before</em> AddMentorAgent") only exist at that level, and a container assembled by hand
/// would test a different program.
/// </para>
///
/// <para>
/// Two Blazor services MentorAgent expects are not registered by the package, because a host always
/// has them: <see cref="NavigationManager"/> and <see cref="AuthenticationStateProvider"/>. The
/// fixture supplies controllable versions, which is also what makes role gating and navigation
/// assertable.
/// </para>
/// </summary>
internal sealed class MentorHostFixture : IAsyncDisposable
{
    private readonly ServiceProvider _root;

    private MentorHostFixture(ServiceProvider root, IServiceScope scope, FakeChatClient? chat,
                              FakeEmbeddingGenerator? embeddings, TestNavigationManager nav,
                              TestAuthStateProvider auth, TestLogCollector logs)
    {
        _root = root;
        Scope = scope;
        Chat = chat;
        Embeddings = embeddings;
        Navigation = nav;
        Auth = auth;
        Logs = logs;
    }

    /// <summary>The scope services resolve from — MentorAgent's per-turn services are scoped.</summary>
    internal IServiceScope Scope { get; }

    /// <summary>The scripted model, unless the test supplied a pre-built agent (Path B).</summary>
    internal FakeChatClient? Chat { get; }

    /// <summary>The deterministic embedding generator, when one was configured.</summary>
    internal FakeEmbeddingGenerator? Embeddings { get; }

    /// <summary>Records navigations the assistant requested.</summary>
    internal TestNavigationManager Navigation { get; }

    /// <summary>Controls the current principal — drives <c>RequiredRoles</c>.</summary>
    internal TestAuthStateProvider Auth { get; }

    /// <summary>
    /// Log records emitted during the test, for warning assertions. This is the <em>live</em>
    /// collector the provider writes into — most of MentorAgent's logging happens when services
    /// are first resolved, which is long after the fixture is constructed.
    /// </summary>
    internal TestLogCollector Logs { get; }

    internal T Get<T>() where T : notnull => Scope.ServiceProvider.GetRequiredService<T>();

    internal T? GetOptional<T>() => Scope.ServiceProvider.GetService<T>();

    internal MentorOptions Options => Get<Microsoft.Extensions.Options.IOptions<MentorOptions>>().Value;

    internal IMentorOrchestrator Orchestrator => Get<IMentorOrchestrator>();

    /// <summary>
    /// The state service as a recorder, so a test can assert the event sequence of a turn.
    /// </summary>
    internal RecordingStateService? Recorder { get; init; }

    /// <summary>
    /// Runs one full turn through the real orchestrator.
    ///
    /// <para>
    /// Several documented behaviours only exist once the chat pipeline has been assembled — tool
    /// filtering, routing, guardrails and the degradation warnings for all of them are decided
    /// while building the pipeline for the first message, not when the orchestrator is resolved.
    /// A constraint test therefore has to drive a turn; resolving the service proves nothing.
    /// </para>
    ///
    /// <para>
    /// Failures are swallowed on purpose: many of these tests are about what MentorAgent
    /// <em>logged</em> and which tools it <em>sent</em> on the way to a failure, and a scripted
    /// model cannot always satisfy the whole pipeline. The assertion belongs in the test.
    /// </para>
    /// </summary>
    internal async Task<Exception?> RunTurnAsync(string message = "hello", CancellationToken ct = default)
    {
        try
        {
            await Orchestrator.SendMessageAsync(message, ct);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>
    /// The conversation the history provider STORED, read back from the session snapshot — what a
    /// restore, the next turn and a durable store all start from (BUG-104: a question can be sent to the
    /// model and still never be stored).
    ///
    /// <para>
    /// <paramref name="stateKey"/> is the provider's key in the session's state bag. The built-in
    /// provider keeps <c>{"messages": [...]}</c> under <c>InMemoryChatHistoryProvider</c>; a
    /// <c>ProviderSessionState&lt;List&lt;ChatMessage&gt;&gt;</c> provider keeps the array itself.
    /// </para>
    /// </summary>
    internal async Task<IReadOnlyList<Microsoft.Extensions.AI.ChatMessage>> StoredHistoryAsync(
        string stateKey = "InMemoryChatHistoryProvider")
    {
        var raw = await StoredHistoryJsonAsync(stateKey);
        if (raw is not { } state) return [];

        var array = state.ValueKind == System.Text.Json.JsonValueKind.Object
                    && state.TryGetProperty("messages", out var messages)
            ? messages
            : state;

        return array.ValueKind == System.Text.Json.JsonValueKind.Array
            ? System.Text.Json.JsonSerializer.Deserialize<List<Microsoft.Extensions.AI.ChatMessage>>(
                  array, Microsoft.Extensions.AI.AIJsonUtilities.DefaultOptions) ?? []
            : [];
    }

    /// <summary>
    /// The raw JSON the history provider keeps in the session, or null when there is no conversation
    /// or no such key. For assertions on what is persisted verbatim — typed helpers such as
    /// <c>GetAgentRequestMessageSourceId()</c> read nothing back from a JSON round trip.
    /// </summary>
    internal async Task<System.Text.Json.JsonElement?> StoredHistoryJsonAsync(
        string stateKey = "InMemoryChatHistoryProvider")
    {
        var snapshot = await Orchestrator.SerializeSessionAsync();
        if (snapshot is not { } s) return null;

        return s.TryGetProperty("session", out var session)
               && session.TryGetProperty("stateBag", out var bag)
               && bag.TryGetProperty(stateKey, out var state)
            ? state.Clone()
            : null;
    }

    /// <summary>The tool names sent to the model on the last call, or empty if it was never called.</summary>
    internal IReadOnlyList<string> LastToolsSent => Chat?.LastCall?.ToolNames ?? [];

    /// <summary>Tool names sent on the first call — before any function-calling round trips.</summary>
    internal IReadOnlyList<string> FirstToolsSent =>
        Chat is { Calls.Count: > 0 } ? Chat.Calls[0].ToolNames : [];

    /// <summary>
    /// Builds a host. <paramref name="configure"/> receives options already primed with a scripted
    /// chat client and this assembly as the scan target, so a test only states what it is about.
    /// </summary>
    /// <param name="withRecorder">
    /// Replaces <see cref="IMentorStateService"/> with a <see cref="RecordingStateService"/> and
    /// exposes it as <see cref="Recorder"/>. Registered <em>after</em> <c>AddMentorAgent()</c> —
    /// <paramref name="configureServices"/> runs before it, which is the documented position for a
    /// memory store or RAG source but is too early to override anything the library registers.
    /// </param>
    internal static MentorHostFixture Create(
        Action<MentorOptions>? configure = null,
        Action<IServiceCollection>? configureServices = null,
        bool withEmbeddings = false,
        bool withChatClient = true,
        bool withRecorder = false)
    {
        var services = new ServiceCollection();

        var logs = new TestLogCollector();
        services.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Debug);
            b.AddProvider(new TestLoggerProvider(logs));
        });

        var nav = new TestNavigationManager();
        var auth = new TestAuthStateProvider();
        services.AddSingleton<NavigationManager>(nav);
        services.AddSingleton<AuthenticationStateProvider>(auth);

        // MentorSkillsBuilder resolves SkillsFolder against the content root, so the environment
        // has to be present. Pointing it at the test output directory keeps file-based skill
        // discovery working against fixture files rather than someone's repository layout.
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment
        {
            ApplicationName = "Novior.MentorAgent.Tests",
            EnvironmentName = Environments.Development,
            ContentRootPath = AppContext.BaseDirectory,
        });

        // The application's own services, exactly as a host would register them.
        services.AddScoped<TestOrderService>();
        services.AddScoped<TestWeatherService>();
        services.AddScoped<TestShippingAgent>();
        services.AddScoped<TestExpenseSkill>();
        services.AddScoped<TestAnalysisTools>();

        // Anything the test wants registered *before* AddMentorAgent — the documented position for
        // a custom memory store, RAG source or metrics store.
        configureServices?.Invoke(services);

        var chat = withChatClient ? new FakeChatClient() : null;
        var embeddings = withEmbeddings ? new FakeEmbeddingGenerator() : null;

        services.AddMentorAgent(o =>
        {
            o.AppName = "TestApp";
            o.AppDescription = "A test application";

            // Scan the app project only. Pointing this at the test assembly would make every
            // [MentorPage] or [MentorAction] written for one suite visible to every other suite
            // that asserts on the discovered set.
            o.ScanAssemblies = [typeof(TestOrderService).Assembly];
            if (chat is not null) o.ChatClient = chat;
            if (embeddings is not null) o.EmbeddingGenerator = embeddings;

            configure?.Invoke(o);
        });

        // After AddMentorAgent, so this wins: the orchestrator resolves IMentorStateService, and
        // the last registration for a service type is the one the container hands out.
        var recorder = withRecorder ? new RecordingStateService() : null;
        if (recorder is not null)
            services.AddSingleton<IMentorStateService>(recorder);

        var root = services.BuildServiceProvider();
        var scope = root.CreateScope();

        return new MentorHostFixture(root, scope, chat, embeddings, nav, auth, logs)
        {
            Recorder = recorder,
        };
    }

    public async ValueTask DisposeAsync()
    {
        // MentorOrchestrator is IAsyncDisposable only, and a scope that contains one refuses a
        // synchronous Dispose outright rather than blocking. Both levels must go async.
        if (Scope is IAsyncDisposable asyncScope) await asyncScope.DisposeAsync();
        else Scope.Dispose();

        await _root.DisposeAsync();
    }
}

/// <summary>A <see cref="NavigationManager"/> that records instead of navigating.</summary>
internal sealed class TestNavigationManager : NavigationManager
{
    internal List<string> Navigations { get; } = [];

    internal TestNavigationManager() => Initialize("https://localhost/", "https://localhost/");

    /// <summary>Moves the "current" URI without recording it, to set up a starting page.</summary>
    internal void StartAt(string relativeUri) => Uri = ToAbsoluteUri(relativeUri).ToString();

    protected override void NavigateToCore(string uri, bool forceLoad)
    {
        Navigations.Add(uri);
        Uri = ToAbsoluteUri(uri).ToString();
    }
}

/// <summary>An <see cref="AuthenticationStateProvider"/> whose principal a test controls.</summary>
internal sealed class TestAuthStateProvider : AuthenticationStateProvider
{
    private ClaimsPrincipal _user = new(new ClaimsIdentity());

    /// <summary>Signs in with an id and roles — what <c>RequiredRoles</c> is checked against.</summary>
    internal void SignIn(string userId, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId), new(ClaimTypes.Name, userId) };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        _user = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
        NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(_user)));
    }

    /// <summary>
    /// Signs in with exactly these claims — a principal without <c>NameIdentifier</c>, such as Windows
    /// authentication or JwtBearer with <c>MapInboundClaims = false</c> gives (BUG-142).
    /// </summary>
    internal void SignInWith(params Claim[] claims)
    {
        _user = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
        NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(_user)));
    }

    /// <summary>Signs out — the anonymous case, where role-gated tools must fail closed.</summary>
    internal void SignOut()
    {
        _user = new ClaimsPrincipal(new ClaimsIdentity());
        NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(_user)));
    }

    /// <summary>
    /// When set, every read faults with this exception — the provider that exists but cannot answer
    /// (BUG-072: a Blazor Server provider read outside its circuit, or a host's own that throws).
    /// </summary>
    internal Exception? ThrowOnRead { get; set; }

    public override Task<AuthenticationState> GetAuthenticationStateAsync()
        => ThrowOnRead is { } ex
            ? Task.FromException<AuthenticationState>(ex)
            : Task.FromResult(new AuthenticationState(_user));
}

/// <summary>Captured log records — several documented behaviours are "warn and degrade".</summary>
internal sealed class TestLogCollector
{
    private readonly List<(LogLevel Level, string Category, string Message)> _records = [];
    private readonly Lock _gate = new();

    internal void Add(LogLevel level, string category, string message)
    {
        lock (_gate) _records.Add((level, category, message));
    }

    /// <summary>Exceptions passed to the logger — the detail a bare message throws away.</summary>
    internal List<Exception> Exceptions { get; } = [];

    /// <summary>The same exceptions with the level they were logged at: a sink prints an attached exception whole.</summary>
    internal List<(LogLevel Level, Exception Exception)> ExceptionsByLevel { get; } = [];

    /// <summary>Every logged exception, formatted, for a failure message that can be acted on.</summary>
    internal string ExceptionText => string.Join(
        Environment.NewLine + "---" + Environment.NewLine, Exceptions.Select(e => e.ToString()));

    internal IReadOnlyList<(LogLevel Level, string Category, string Message)> Records
    {
        get { lock (_gate) return _records.ToList(); }
    }

    /// <summary>True when some warning contains <paramref name="fragment"/>.</summary>
    internal bool HasWarning(string fragment) => Records.Any(r =>
        r.Level >= LogLevel.Warning &&
        r.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    /// <summary>All warning and error text, for a failure message that explains itself.</summary>
    internal string WarningText => string.Join(
        Environment.NewLine,
        Records.Where(r => r.Level >= LogLevel.Warning).Select(r => $"[{r.Level}] {r.Message}"));

}

/// <summary>A minimal <see cref="IHostEnvironment"/> — skills resolve paths against it.</summary>
internal sealed class TestHostEnvironment : IHostEnvironment
{
    public string ApplicationName { get; set; } = "Novior.MentorAgent.Tests";
    public string EnvironmentName { get; set; } = Environments.Development;
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } =
        new PhysicalFileProvider(AppContext.BaseDirectory);
}

internal sealed class TestLoggerProvider(TestLogCollector sink) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new TestLogger(categoryName, sink);
    public void Dispose() { }

    private sealed class TestLogger(string category, TestLogCollector sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter)
        {
            sink.Add(logLevel, category, formatter(state, exception));

            // MentorAgent logs several failures as "message + exception" and swallows the
            // exception. Without keeping it, a red test says only that something went wrong.
            if (exception is not null)
                lock (sink.Exceptions)
                {
                    sink.Exceptions.Add(exception);
                    sink.ExceptionsByLevel.Add((logLevel, exception));
                }
        }
    }
}
