# Cross-app parity matrix

The same checks, run on all three sample applications, and the answers compared.

**Why this file exists.** The earlier E2E passes ran *three different scripts* — a rich one on the
Blazor Server sample, a protocol-level one on the headless server, and a targeted subset on the
WebAssembly client. Each app passed the checks it was given, and nothing compared the answers. That
is the one arrangement in which a feature can work on one transport and silently fail on another,
which is exactly what had happened: **four defects (BUG-036 … BUG-039) were found within an hour of
asking all three apps the same questions**, and none of them was reachable by any of the 1,396
in-process tests.

| | App | Transport | Port |
|---|---|---|---|
| **A** | `MentorAgentSample` | Blazor Server, in-process | 5224 |
| **B** | `MentorAgentBlazor` | Blazor WebAssembly → SignalR, cross-origin | 5001 → 5169 |
| **C** | `MentorAgentServer` | headless: SSE / MCP / A2A, no widget | 5169 |

Legend: **OK** verified live · **n/a** does not apply to this surface · **FIXED** was broken, fixed
this pass.

## The matrix

| # | Check | A · Blazor Server | B · WASM | C · headless |
|---|---|---|---|---|
| 1 | First turn, streaming | OK | OK | OK — SSE `chunk`/`completed` |
| 2 | Tool call reaches the domain | OK | OK | OK |
| 3 | Tour, first run | OK — step 1 of 6 | OK | OK — `GET /mentor/tour` |
| 4 | Tour, resume mid-way | OK | OK — saved `2` gives **"PASSO 3 DI 6"** | n/a — no widget |
| 5 | Tour, skip persists | OK | OK | n/a |
| 6 | Tour URL resolved against the **server** origin | n/a — same origin | OK — `5169/mentor/tour`, not `5001` | n/a |
| 7 | RAG citation chips | OK | OK | n/a — not forwarded (documented) |
| 8 | Cards | OK | OK — `Ordine #1001` with `Apri`/`Dettagli` | **FIXED** BUG-036 |
| 9 | MCP badge reaches online | OK | OK — `--connecting` until the first turn; the coordinator is lazy | OK — `initialize` + `tools/list` |
| 10 | A2A | OK — badge | OK — badge `ShopFlow-B` | OK — absolute `/a2a` in the card |
| 11 | Hosted-tools badge | OK | OK — `4 tools` | OK — declared in the log |
| 12 | HITL, banner carries the real arguments | OK | OK — `Order Id: 1002 · Status: Delivered` | **FIXED** BUG-038 |
| 13 | HITL, Conferma runs the tool | OK | OK — over `POST /mentor/approve` | **FIXED** — order 1004 became `Shipped` |
| 14 | HITL, Annulla withholds **and** the model does not re-offer | OK (BUG-031) | OK — *"Ho capito, non aggiornerò lo stato dell'ordine."* | n/a — verdict `false` over the same route |
| 15 | Gated action withheld from an unauthenticated caller | OK | OK | OK — fail-closed on `/mcp` (BUG-021, BUG-035) |
| 16 | Stop mid-stream | OK | OK | n/a — client disconnect |
| 17 | **The next turn works after Stop** | OK | OK | n/a |
| 18 | Reset | OK | OK — 7 messages back to welcome + 3 chips | n/a |
| 19 | `navigate_to` | OK | OK — went to `/data/products` | **FIXED** BUG-037 |
| 20 | Typed UI actions | OK — `int` + `string` | OK — `filter_orders("Pending")`, table 6 rows → 1 | n/a — no page |
| 21 | Memory write | OK | OK — *"ho memorizzato che preferisci le spedizioni express"* | OK — same store |
| 22 | Unread badge on a reply to a closed panel | OK | OK — launcher shows `1` | n/a |
| 23 | Image input: URL / upload / paste / drop | OK — all four | OK — all four (1 → 2 → 3 thumbnails) | OK — `POST` with `attachments[]` |
| 24 | Dashboard / metrics, per model | OK | OK — cross-origin: `gpt-4.1`, `o3`, `text-embedding-3-small` | OK — `/mentor/admin/metrics`, survives a restart |
| 25 | Widths 320 / 375 / 1280: panel fits, input visible, no horizontal scroll | OK | OK — 288×624 at 320, 343×716 at 375 | n/a |
| 26 | Header bands disjoint; `.bm-status-pills` a **direct child** of `.bm-header` | OK | OK | n/a |
| 27 | Live `var(--bm-*)` audit | OK — 4 unresolved, all `.bm-dash` by design | OK — **identical**: 20 referenced, the same 4 | n/a |
| 28 | `?v=` asset bump | n/a — Blazor versions its own tags | OK — 60,686 B css / 25,573 B js, no 0-byte 200 | n/a |
| 29 | The widget script loaded twice | n/a — one tag | **FIXED** BUG-039 — two tags, and the second reset the voice state machine | n/a |
| 30 | CORS cross-origin | n/a | OK — `/api/*`, hub, `/mentor/approve` | OK |

## What the comparison found that a single-app pass could not

**Three of the four defects are the same shape:** an outcome that travels on a channel one transport
forwards and another does not, ending in a frame that claims success.

The headless SSE endpoint forwarded **4 of the 18 events** the SignalR hub forwards. So a card, a
navigation and a withheld action all ended the same way — `{"type":"completed"}` — and the client
believed the turn had succeeded. In the confirmation case the assistant had already said *"Procedo
ora con l'aggiornamento"*, and the action never ran.

Two details are worth carrying forward:

- **The cards frame was subscribed and still lost.** Four `async void` handlers wrote straight to
  `HttpResponse`, which does not allow overlapping writes, inside `catch { }`. A frame lost in
  silence is indistinguishable from a frame that was never raised.
- **The first in-process test for the confirmation frame passed — by luck.** The blocking HITL path
  raises `OnStreamingCompleted` one line *before* the confirmation, deliberately, so the widget shows
  the explanation before the banner appears. Ending the SSE response on that signal made the teardown
  race the confirmation. With a fake model the race went the other way. A green test that is green by
  scheduling is worth nothing; the fix removes the race rather than the symptom, by awaiting the
  turn's own task.

**The fourth is a WebAssembly-only defect that the recommended setup guarantees.** A standalone WASM
app loads `index.html` before .NET starts, so the docs tell hosts to put a versioned `<script>` tag
there; the widget's `<HeadContent>` then injects a second one. The file is one object literal, so the
second load reset `_handsFree`, `_isSpeaking` and the live `SpeechRecognition` handle. The Blazor
Server sample has one tag and never meets it.

## What was checked and needed nothing

Twenty-six of the thirty rows were already at parity, including every one that had been driven on
only one app before: tour resume, Annulla, Stop, Reset, typed UI actions, memory, the unread badge,
all four image paths, the responsive widths, the header-band geometry, and the live CSS-variable
audit — which returned **the same four unresolved names on both Blazor apps**, and those four are the
dashboard variables deliberately scoped to `.bm-dash`.

One of my own checks was wrong before the library was: at 320 px the header's avatar and info blocks
share vertical space, which a naive band comparison reports as an overlap. They are horizontal
siblings (avatar right edge 103, info right edge 305). The three *stacked* blocks — info, actions,
pills — are disjoint, which is the property B18 pins.

## Deliberate asymmetries, so "no row" is not read as "not looked at"

| Not on C (headless) | Why |
|---|---|
| Tour steps, Reset, unread badge, widths, header geometry | There is no widget. The tour's *data* is checked through `GET /mentor/tour` |
| UI actions, `navigate_to` target | No page to act on. The **frame** is checked in row 19 |
| RAG chips | SSE forwards the six frames that decide what the user sees; citations ride the hub. Documented in the server README's frame table |

| Not on B (WASM) | Why |
|---|---|
| 6 positions × 4 themes | 24 restarts to re-prove what `ChatWidgetTests` pins per component and `CssVariableContractTests` pins per stylesheet. The configured combination is verified in the browser |
| `9+` unread cap | Ten live turns to re-prove one bUnit assertion |


## Re-run on Agent Framework 1.18.0 (20 August 2026)

The bump from 1.6.1 was re-validated by running the same list again. **The single most important
result is one no in-process test could produce:** build 0 warnings / 0 errors, 1,423 tests green,
and `MentorAgentServer` **would not start** — `Azure.AI.OpenAI 2.9.0-beta.1` calls a
`ResponsesClient` constructor that `OpenAI 2.11.0` removed (BUG-043, the fourth S1). Fixed by
holding `OpenAI` at 2.10.0; see the bug entry for the exact pin.

| Check | A · Blazor Server | B · WASM | C · headless |
|---|---|---|---|
| Application starts at all | OK | OK | **FIXED** BUG-043 — died on `GetResponsesClient()` |
| Live turn against Azure, with a tool call | OK — product table, `€1.899,99` | OK — 14.4 s cross-origin | OK — SSE `chunk` → `completed` |
| RAG citation chips | OK | OK | n/a |
| `cards` frame / card render | OK | OK | OK — 1 `cards` + 254 `chunk` + 1 `completed` |
| MCP `initialize` + `tools/list` | n/a | n/a | OK — `ShopFlow Server`, **19 tools, no gated tool published** |
| Agent card, absolute `/a2a` URL | n/a | n/a | OK |
| Metrics survive a restart, per model | OK | OK | OK — 30 turns, €0,4456, `gpt-4.1` / `o3` / embedding split |
| Two `<script>` tags, voice state intact | n/a | OK — BUG-039 holds | n/a |

**What changed in observed behaviour, and is not a defect.** A bare `GET /mcp` now answers **400**
with a JSON-RPC error naming the missing `Mcp-Session-Id` header, where it previously answered a
bare **406**. That is the MCP SDK's own message and strictly more useful; the earlier E2E-3 note
recording 406 is superseded.

**A sample-harness defect the re-run exposed.** `.claude/launch.json` started the WASM client on
port **5280**, while `MentorAgentServer/Program.cs` allows `http://localhost:5001` in its CORS list
*and* uses that same origin as `AgentCardUrl`. Every cross-origin call failed with
`TypeError: Failed to fetch`, which reads exactly like a library CORS bug and is not one — the two
sample files disagreed with each other. `launch.json` was aligned to 5001. Worth recording because
the symptom accuses the wrong component.

## Reproducing

The in-process half:

```
dotnet test Novior.MentorAgent.slnx --filter FullyQualifiedName~SseFrameParityTests
```

The browser half needs all three apps running (`sample-server`, `headless-server`, `wasm-client` in
`.claude/launch.json`) and is driven by hand. The pane does not composite, so assert through
`read_page` / `javascript_tool` / `preview_logs`, and divide bounding-rect geometry by 0.92.
