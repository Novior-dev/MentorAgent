using System.Threading.Channels;
using Novior.MentorAgent.Core;
using Microsoft.AspNetCore.Http;

namespace Novior.MentorAgent.Server.Endpoints;

/// <summary>
/// Server-Sent Events (SSE) endpoint for clients that prefer HTTP streaming
/// over SignalR (React, Vue, curl, Axios, etc.).
///
/// GET  /mentor/chat?message=hello            — text-only
/// POST /mentor/chat  { "message": "...",     — text + image attachments (roadmap #13);
///                      "attachments": [ … ] }  a base64 image never fits in a query string.
///
/// Response format: text/event-stream
///   data: {"type":"chunk","text":"Hello"}
///   data: {"type":"cards","cards":[…]}
///   data: {"type":"navigation","url":"/orders"}
///   data: {"type":"confirmationRequired","actionId":"…","tool":"…","message":"…"}
///   data: {"type":"completed"}
///   data: {"type":"error","message":"..."}
///
/// <para>
/// Every frame beyond <c>chunk</c>/<c>completed</c>/<c>error</c> is <b>additive</b>: a client that
/// does not know a <c>type</c> ignores it and still receives the whole answer as text. That is what
/// lets the protocol grow without breaking the three-frame clients the README has always shown.
/// </para>
/// </summary>
public static class MentorSseEndpoint
{
    /// <summary>Request body of <c>POST /mentor/chat</c>.</summary>
    public sealed record ChatRequest(string Message, IReadOnlyList<MentorAttachment>? Attachments = null);

    /// <summary>
    /// How structured frames are written. Explicit, because the defaults are wrong for a wire
    /// protocol: <c>JsonSerializer.Serialize</c> with no options emits PascalCase property names and
    /// enums as integers, which does not match the camelCase the rest of these endpoints produce and
    /// leaves a JavaScript client reading <c>"Kind": 1</c> — a value whose meaning silently changes
    /// the day an enum member is inserted.
    /// </summary>
    private static readonly JsonSerializerOptions FrameJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>GET /mentor/chat?message=… — text-only streaming.</summary>
    public static Task HandleAsync(HttpContext ctx)
        => StreamAsync(ctx, ctx.Request.Query["message"].ToString(), null);

    /// <summary>POST /mentor/chat — streaming with optional image attachments (#13).</summary>
    public static async Task HandlePostAsync(HttpContext ctx)
    {
        ChatRequest? body;
        try
        {
            body = await ctx.Request.ReadFromJsonAsync<ChatRequest>(ctx.RequestAborted);
        }
        catch (Exception ex)
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsync($"Invalid JSON body: {ex.Message}");
            return;
        }

        await StreamAsync(ctx, body?.Message ?? string.Empty, body?.Attachments);
    }

    private static async Task StreamAsync(
        HttpContext ctx,
        string message,
        IReadOnlyList<MentorAttachment>? attachments)
    {
        // An image-only turn is valid, so blank text is allowed when attachments are present.
        if (string.IsNullOrWhiteSpace(message) && attachments is not { Count: > 0 })
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsync("A non-empty 'message' (or at least one attachment) is required.");
            return;
        }

        ctx.Response.ContentType  = "text/event-stream";
        ctx.Response.Headers["Cache-Control"] = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no"; // disable nginx buffering

        // Who is calling, recorded for this request's scope before the host's provider is asked (BUG-131):
        // in a Blazor Web App that provider throws outside a circuit.
        ctx.RequestServices.GetRequiredService<MentorTransportUser>().User = ctx.User;

        var orchestrator = ctx.RequestServices.GetRequiredService<IMentorOrchestrator>();
        var state        = ctx.RequestServices.GetRequiredService<IMentorStateService>();
        var logger       = ctx.RequestServices.GetRequiredService<ILoggerFactory>()
                              .CreateLogger(typeof(MentorSseEndpoint).FullName!);
        var ct           = ctx.RequestAborted;

        // One writer, one queue.
        //
        // The events this endpoint forwards are raised from different places in a turn — chunks from
        // the streaming loop, cards and confirmations from inside a tool call — and the previous
        // shape handed each of them its own `async` lambda that wrote to the response directly. Two
        // of those can be in flight at once, which HttpResponse does not allow, and the failure
        // landed in an empty catch: the frame vanished and nothing anywhere said so. That is how a
        // documented cards frame could be absent from a live stream while the server log recorded
        // the cards being produced.
        //
        // Enqueuing is synchronous and never blocks the turn; a single pump drains the queue in
        // order, so frames arrive in the order they were raised and no two writes overlap.
        var frames = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
        });

        void Emit(string type, string payload)
        {
            if (!frames.Writer.TryWrite($"data: {{\"type\":\"{type}\"{payload}}}\n\n"))
                logger.LogWarning("[MentorAgent:SSE] Frame '{Type}' could not be queued.", type);
        }

        Action<string> onChunk = chunk =>
            Emit("chunk", $",\"text\":{JsonSerializer.Serialize(chunk)}");

        // #14 L2 — generative cards.
        Action<IReadOnlyList<MentorCard>> onCards = cards =>
            Emit("cards", $",\"cards\":{JsonSerializer.Serialize(cards, FrameJson)}");

        // navigate_to is a core tool: it is registered on every application that declares a page, so
        // an SSE client cannot opt out of reaching it. Without this frame the model announces the
        // move, the turn completes, and the client stays where it was — the assistant asserting a
        // state change that never happened is worse than a missing feature.
        Action<string> onNavigate = url =>
            Emit("navigation", $",\"url\":{JsonSerializer.Serialize(url)}");

        // The action is withheld until a human decides. Answer it with
        // POST /mentor/approve?actionId=…&approved=true|false, which is transport-agnostic and
        // shares the singleton approval store — the same route the WASM widget uses.
        Action<ConfirmationRequest> onConfirmation = req =>
            Emit("confirmationRequired",
                 $",\"actionId\":{JsonSerializer.Serialize(req.ActionId.ToString())}" +
                 $",\"tool\":{JsonSerializer.Serialize(req.ToolName)}" +
                 $",\"message\":{JsonSerializer.Serialize(req.Message)}");

        Action<string> onError = error =>
            Emit("error", $",\"message\":{JsonSerializer.Serialize(error)}");

        state.OnStreamingChunk       += onChunk;
        state.OnError                += onError;
        state.OnCardsReady           += onCards;
        state.OnNavigationRequested  += onNavigate;
        state.OnConfirmationRequired += onConfirmation;

        try
        {
            var pump = PumpAsync(ctx, frames.Reader, logger, ct);

            // The turn's own task decides when the turn is over — not OnStreamingCompleted.
            //
            // OnStreamingCompleted marks the end of a streaming *segment*, and the blocking HITL
            // path raises it deliberately one line before the confirmation, so the user reads the
            // assistant's explanation before the banner appears. Ending the response there closed
            // the stream while the turn was still parked: the client was told "completed" for an
            // action that had been withheld, and the confirmation frame lost a race against the
            // teardown that unsubscribed it. Awaiting the turn removes the race entirely — the
            // handlers stay attached for as long as the turn can still raise anything.
            //
            // A turn parked on an approval therefore holds the response open, which is the point:
            // POST /mentor/approve releases it and the stream carries on. If the client goes away
            // instead, RequestAborted cancels the parked wait and the turn unwinds.
            await orchestrator.SendMessageAsync(message, attachments, ct);

            // Queued last, so everything raised during the turn is already ahead of it: completing
            // the writer and awaiting the pump is what guarantees no frame is dropped at the end.
            frames.Writer.TryWrite("data: {\"type\":\"completed\"}\n\n");
            frames.Writer.Complete();
            await pump;
        }
        catch (OperationCanceledException) { /* client disconnected */ }
        finally
        {
            state.OnStreamingChunk       -= onChunk;
            state.OnError                -= onError;
            state.OnCardsReady           -= onCards;
            state.OnNavigationRequested  -= onNavigate;
            state.OnConfirmationRequired -= onConfirmation;

            frames.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Drains the frame queue onto the response, one write at a time.
    /// A write that fails is logged rather than swallowed: a frame lost in silence is
    /// indistinguishable from a frame that was never raised, from either end of the wire.
    /// </summary>
    private static async Task PumpAsync(
        HttpContext ctx,
        ChannelReader<string> reader,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            await foreach (var frame in reader.ReadAllAsync(ct))
            {
                await ctx.Response.WriteAsync(frame, ct);
                await ctx.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { /* client disconnected mid-stream */ }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[MentorAgent:SSE] Writing to the response stream failed; " +
                                  "the remaining frames of this turn were not delivered.");
        }
    }
}
