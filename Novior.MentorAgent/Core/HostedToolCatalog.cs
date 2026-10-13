namespace Novior.MentorAgent.Core;

/// <summary>
/// Gives each provider-hosted tool a stable key and a description, so the semantic tool filter can
/// treat it like any other candidate (roadmap #15 + #4).
///
/// <para>
/// <b>Why a catalog is needed at all.</b> A hosted tool is a marker: <c>HostedWebSearchTool</c> and
/// friends carry no description, and they are not <c>AIFunction</c>s, so the filter had nothing to
/// embed and no way to score them — which is exactly why they were the one group exempt from
/// filtering while the cheap application tools were being trimmed on every turn.
/// </para>
///
/// <para>
/// The descriptions are written for <i>embedding</i>, not for the model: they name the situations a
/// user's message would be about, because that is what the cosine similarity is computed against.
/// They are in English on purpose — the recommended embedding models are multilingual, and one text
/// per tool keeps the behaviour identical whatever language the user writes in.
/// </para>
/// </summary>
internal static class HostedToolCatalog
{
    /// <summary>Prefix that keeps hosted keys from ever colliding with a function tool's name.</summary>
    private const string KeyPrefix = "hosted:";

    /// <summary>
    /// Returns the filtering key and the text to embed for a hosted tool, or <see langword="null"/>
    /// when the tool is not one (a normal function, which the filter already handles).
    /// </summary>
    public static (string Key, string Text)? Describe(AITool tool) => tool switch
    {
        HostedWebSearchTool => (KeyPrefix + "web_search",
            "Search the public web for current, recent or external information: news, prices, " +
            "documentation, release dates, what happened lately, facts that change over time and " +
            "are not stored in this application."),

        HostedCodeInterpreterTool => (KeyPrefix + "code_interpreter",
            "Run code in a sandbox to compute an exact result: arithmetic, mathematics, statistics, " +
            "simulations, parsing or transforming data, producing a chart or a plot."),

        HostedFileSearchTool => (KeyPrefix + "file_search",
            "Search the uploaded documents and the knowledge base: manuals, guides, contracts, " +
            "policies, specifications, PDFs — anything answered by reading a document."),

        HostedImageGenerationTool => (KeyPrefix + "image_generation",
            "Generate, draw or create an image, picture, illustration, drawing, logo or banner " +
            "from a textual description."),

        // A remote MCP server is the one hosted tool whose purpose is application-specific, so its
        // own metadata is the only sensible description — a generic one would match everything.
        //
        // ServerName, not Name: AITool.Name on this type is the tool *kind* ("mcp"), the same for
        // every server. Using it produced the key "hosted:mcp:mcp" for a server configured as
        // "microsoft_learn" — two servers would then collide on one key and share one embedding,
        // and the embedded text lost the only word that said what the server is about.
        HostedMcpServerTool mcp => (KeyPrefix + "mcp:" + mcp.ServerName,
            $"{mcp.ServerName}: {mcp.ServerDescription ?? "remote MCP server"}. " +
            $"Tools: {(mcp.AllowedTools is { Count: > 0 } t ? string.Join(", ", t) : "all")}."),

        _ => null,
    };

    /// <summary>True when the tool is provider-hosted rather than a locally executed function.</summary>
    public static bool IsHosted(AITool tool) => Describe(tool) is not null;
}
