using Novior.MentorAgent.Options;

namespace Novior.MentorAgent.Core;

/// <summary>
/// Builds the provider-hosted tools declared in <see cref="MentorOptions.HostedTools"/>
/// (roadmap #15 — Agent Framework <i>hosted tools</i>).
///
/// <para>
/// These are markers, not implementations: <c>HostedWebSearchTool</c>,
/// <c>HostedCodeInterpreterTool</c>, <c>HostedFileSearchTool</c>, <c>HostedImageGenerationTool</c> and
/// <c>HostedMcpServerTool</c> tell the provider that it may run web search / sandboxed code / file
/// search / image generation / remote MCP calls on <b>its</b> infrastructure while generating the
/// response. Nothing executes on the host application, so they never reach
/// <c>MentorAgentUIMiddleware</c>'s function-calling pipeline (no role check, no local confirmation,
/// no per-tool metrics) — that is inherent to hosted tools, not a MentorAgent limitation.
/// </para>
///
/// <para>
/// Hosted MCP is the one exception: the provider itself can pause for approval, and that approval
/// arrives as a <c>ToolApprovalRequestContent</c>, which the native HITL loop (#16) already answers
/// through the normal confirmation banner.
/// </para>
/// </summary>
internal static class HostedToolsBuilder
{
    /// <summary>
    /// Media type sent for generated images. Must be a full MIME type — see
    /// <see cref="BuildImageGenerationTool"/> for why it can never be left unset.
    /// </summary>
    private const string DefaultImageMediaType = "image/png";

    /// <summary>
    /// Returns the hosted tools to add to the coordinator plus the set that was actually enabled
    /// (a tool that fails validation is dropped, so the badge never claims a capability the model
    /// does not have).
    /// </summary>
    public static (List<AITool> Tools, MentorHostedTools Active) Build(MentorOptions options, ILogger logger)
    {
        var tools  = new List<AITool>();
        var active = MentorHostedTools.None;

        if (options.HostedTools == MentorHostedTools.None)
            return (tools, active);

        // Hosted tools travel in ChatOptions.Tools, which only exists on Path A. On Path B the
        // pre-built AIAgent carries its own tool list, configured where the agent is created.
        if (options.ChatClient is null)
        {
            logger.LogWarning(
                "[MentorAgent] HostedTools is set but no ChatClient is configured (Path B). Hosted tools " +
                "must be declared on the pre-built AIAgent itself — none were added.");
            return (tools, active);
        }

        if (options.HostedTools.HasFlag(MentorHostedTools.WebSearch))
        {
            tools.Add(new HostedWebSearchTool());
            active |= MentorHostedTools.WebSearch;
        }

        if (options.HostedTools.HasFlag(MentorHostedTools.CodeInterpreter))
        {
            tools.Add(new HostedCodeInterpreterTool());
            active |= MentorHostedTools.CodeInterpreter;
        }

        if (options.HostedTools.HasFlag(MentorHostedTools.FileSearch))
        {
            var storeIds = options.FileSearchVectorStoreIds?
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToList() ?? [];

            if (storeIds.Count == 0)
            {
                // Fail closed: an unscoped file search is useless to the model and, on providers that
                // default to "everything uploaded", a data-exposure risk. Skip rather than guess.
                logger.LogWarning(
                    "[MentorAgent] Hosted file search is enabled but FileSearchVectorStoreIds is empty — " +
                    "the tool was skipped. Set options.FileSearchVectorStoreIds = [\"vs_...\"].");
            }
            else
            {
                var fileSearch = new HostedFileSearchTool
                {
                    Inputs = [.. storeIds.Select(id => new HostedVectorStoreContent(id))],
                };
                if (options.FileSearchMaxResults is > 0)
                    fileSearch.MaximumResultCount = options.FileSearchMaxResults;

                tools.Add(fileSearch);
                active |= MentorHostedTools.FileSearch;
            }
        }

        if (options.HostedTools.HasFlag(MentorHostedTools.ImageGeneration))
        {
            tools.Add(BuildImageGenerationTool(options, logger));
            active |= MentorHostedTools.ImageGeneration;
        }

        if (options.HostedTools.HasFlag(MentorHostedTools.HostedMcp))
            active |= BuildHostedMcpServers(options, tools, logger);

        if (active != MentorHostedTools.None)
        {
            // One clear line at startup: hosted tools fail at the provider, not in our code, so the
            // configured set has to be visible when a call comes back "unsupported tool".
            logger.LogInformation(
                "[MentorAgent] Hosted tools enabled: {Tools}. They run on the model provider — supported by " +
                "the Azure OpenAI / OpenAI Responses client (and Foundry); the Chat Completions client " +
                "supports web search only.", active);

            // Model routing (#11) sends the same tool list to whichever model serves the turn, so a
            // strong model on a client that does not support hosted tools fails the moment a message
            // is escalated — intermittently, which is much harder to diagnose than a startup error.
            if (options.StrongChatClient is not null)
                logger.LogWarning(
                    "[MentorAgent] Hosted tools are combined with model routing: the strong model receives " +
                    "the same tool list, so build StrongChatClient on a client that supports them too " +
                    "(otherwise escalated turns fail with an unknown-parameter error).");
        }

        return (tools, active);
    }

    /// <summary>
    /// Builds the image-generation tool.
    ///
    /// <para>
    /// <b>The media type is mandatory, not a nicety.</b> Microsoft.Extensions.AI 10.6.0 converts it into
    /// the OpenAI output-file-format with no null check, so a tool that leaves it unset throws
    /// <c>ArgumentNullException</c> while the request is being assembled — before any network call, on
    /// every single turn. The conversion also only understands full MIME types: <c>"png"</c> fails
    /// exactly like <see langword="null"/>. Verified against Azure OpenAI, 2026-07-31.
    /// </para>
    /// </summary>
    private static HostedImageGenerationTool BuildImageGenerationTool(MentorOptions options, ILogger logger)
    {
        var size = ParseImageSize(options.HostedImageSize, logger);

        var imageOptions = new ImageGenerationOptions { MediaType = DefaultImageMediaType };
        if (!string.IsNullOrWhiteSpace(options.HostedImageModel))
            imageOptions.ModelId = options.HostedImageModel;
        if (size is not null)
            imageOptions.ImageSize = size;

        logger.LogInformation(
            "[MentorAgent] Hosted image generation: model {Model}, size {Size}, format {Format}.",
            options.HostedImageModel ?? "provider default",
            size is null ? "provider default" : $"{size.Value.Width}x{size.Value.Height}",
            DefaultImageMediaType);

        // Azure resolves the image deployment from a request header, not from the tool payload, and
        // MentorAgent is handed an already-built IChatClient — so it cannot add it. Said once at
        // startup, because the alternative is a 400 mid-turn whose message never reaches the developer.
        logger.LogInformation(
            "[MentorAgent] On Azure OpenAI the image deployment must ALSO travel as the " +
            "'x-ms-oai-image-generation-deployment' header — add it as a pipeline policy on the " +
            "AzureOpenAIClient, or the provider answers 'imagegen deployment must be provided through header'.");

        return new HostedImageGenerationTool { Options = imageOptions };
    }

    /// <summary>
    /// Parses <c>WIDTHxHEIGHT</c> into a size. A malformed value falls back to the provider default with
    /// a warning rather than throwing: a typo in a size should not stop the application from starting.
    /// </summary>
    private static System.Drawing.Size? ParseImageSize(string? value, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var parts = value.Split(['x', 'X', '×'], StringSplitOptions.TrimEntries);
        if (parts.Length == 2
            && int.TryParse(parts[0], out var width)  && width  > 0
            && int.TryParse(parts[1], out var height) && height > 0)
        {
            return new System.Drawing.Size(width, height);
        }

        logger.LogWarning(
            "[MentorAgent] HostedImageSize '{Value}' is not in WIDTHxHEIGHT form (e.g. 1024x1024) — " +
            "the provider's default size is used instead.", value);
        return null;
    }

    /// <summary>
    /// Adds one <c>HostedMcpServerTool</c> per configured remote server. Unlike the other hosted
    /// tools this one carries an approval mode, because the provider — not MentorAgent — is the MCP
    /// client and therefore the only place a call can be intercepted.
    /// </summary>
    private static MentorHostedTools BuildHostedMcpServers(
        MentorOptions options, List<AITool> tools, ILogger logger)
    {
        var servers = options.HostedMcpServers?
            .Where(s => !string.IsNullOrWhiteSpace(s.Name) && !string.IsNullOrWhiteSpace(s.Url))
            .ToList() ?? [];

        if (servers.Count == 0)
        {
            logger.LogWarning(
                "[MentorAgent] Hosted MCP is enabled but HostedMcpServers is empty — nothing was added. " +
                "Add at least one MentorHostedMcpServer with a Name and an absolute Url.");
            return MentorHostedTools.None;
        }

        var added = MentorHostedTools.None;

        foreach (var server in servers)
        {
            if (!Uri.TryCreate(server.Url, UriKind.Absolute, out var uri))
            {
                // The provider dials this address, so a relative or malformed one can only fail
                // mid-turn with an opaque provider error. Reject it here instead.
                logger.LogWarning(
                    "[MentorAgent] Hosted MCP server '{Name}' was skipped: '{Url}' is not an absolute URL.",
                    server.Name, server.Url);
                continue;
            }

            var tool = new HostedMcpServerTool(server.Name, uri)
            {
                ServerDescription = server.Description,
                ApprovalMode      = BuildApprovalMode(server),
            };

            if (server.AllowedTools is { Count: > 0 })
                tool.AllowedTools = [.. server.AllowedTools];

            if (server.Headers is { Count: > 0 })
                tool.Headers = server.Headers.ToDictionary(h => h.Key, h => h.Value);

            tools.Add(tool);
            added = MentorHostedTools.HostedMcp;

            logger.LogInformation(
                "[MentorAgent] Hosted MCP server '{Name}' declared to the provider ({Url}) — tools: {Tools}, approval: {Approval}.",
                server.Name, uri,
                server.AllowedTools is { Count: > 0 } ? string.Join(", ", server.AllowedTools) : "all",
                DescribeApproval(server));

            if (!server.RequireApproval && server.AlwaysRequireApprovalTools is not { Count: > 0 })
                logger.LogWarning(
                    "[MentorAgent] Hosted MCP server '{Name}' runs with no approval: the provider will call " +
                    "its tools without asking, and MentorAgent cannot intercept them. Use this only for a " +
                    "read-only server you trust.", server.Name);
        }

        return added;
    }

    private static HostedMcpServerToolApprovalMode BuildApprovalMode(MentorHostedMcpServer server)
    {
        var always = server.AlwaysRequireApprovalTools;
        var never  = server.NeverRequireApprovalTools;

        // Per-tool lists win over the blanket flag — that is the point of configuring them.
        if (always is { Count: > 0 } || never is { Count: > 0 })
            return HostedMcpServerToolApprovalMode.RequireSpecific(
                always is { Count: > 0 } ? [.. always] : [],
                never  is { Count: > 0 } ? [.. never]  : []);

        return server.RequireApproval
            ? HostedMcpServerToolApprovalMode.AlwaysRequire
            : HostedMcpServerToolApprovalMode.NeverRequire;
    }

    private static string DescribeApproval(MentorHostedMcpServer server)
    {
        var always = server.AlwaysRequireApprovalTools;
        var never  = server.NeverRequireApprovalTools;

        if (always is { Count: > 0 } || never is { Count: > 0 })
            return $"specific (always: {(always is { Count: > 0 } ? string.Join(", ", always) : "-")}; " +
                   $"never: {(never is { Count: > 0 } ? string.Join(", ", never) : "-")})";

        return server.RequireApproval ? "always" : "never";
    }
}
