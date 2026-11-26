// MentorAgent (story 1640) — the observation of the pages in production: what the user opens, a dialog, a tab, a panel,
// read with the same reader as the scan (MentorPageReader.js, in observation) and sent to the application, structure
// only (page.observe). What MentorAgent never lets out (a password, card data, a hidden, disabled or excluded element)
// does not leave the browser (page.observe-values). Only the elements the page did not send yet are sent, within limits
// (page.observe-limit): past one, the observation stops for this page's life and says so once in the console, and the
// application writes it once in its log. The widget starts it when the application has it on (ObservePages).
import { read } from './MentorPageReader.js';

const MAX_ELEMENTS = 500;        // per page, as the manifest
const MAX_BYTES = 24 * 1024;     // per message: a SignalR message over 32 KB closes the connection, the circuit's too
const MAX_REPORTS = 200;         // per page's life
const MAX_TEXT = 256;
const MAX_SELECTOR = 512;
const QUIET = 1000;              // the page must stay still this long before it is read
const GAP = 2000;                // the least time between two messages

let current = null;

/**
 * Starts the observation, sending to `dotnet.invokeMethodAsync('OnPageObserved', path, elements, limit)`, which answers
 * whether to go on. `timing` is for the tests only.
 */
export function start(dotnet, timing) {
  stop();
  const state = {
    dotnet, sent: new Map(), reports: 0, last: 0, timer: 0, busy: false, stopped: false,
    quiet: (timing && timing.quiet) || QUIET, gap: (timing && timing.gap) || GAP,
    reportsMax: Math.min((timing && timing.reports) || MAX_REPORTS, MAX_REPORTS),
  };
  state.observer = new MutationObserver(() => schedule(state, state.quiet));
  state.observer.observe(document.documentElement, {
    subtree: true, childList: true, attributes: true,
    attributeFilter: ['open', 'hidden', 'aria-hidden', 'aria-expanded', 'class', 'style'],
  });
  current = state;
  schedule(state, state.quiet);
}

/** Stops the observation; nothing more is sent. */
export function stop() {
  if (!current) return;
  end(current);
}

function end(state) {
  state.stopped = true;
  state.observer.disconnect();
  clearTimeout(state.timer);
  if (current === state) current = null;
}

function schedule(state, delay) {
  if (state.stopped) return;
  clearTimeout(state.timer);
  state.timer = setTimeout(() => flush(state), Math.max(delay, state.last + state.gap - Date.now()));
}

// The page's path, without the application's base, the query or the fragment: the application finds its page.
function pathOf() {
  const base = new URL(document.baseURI).pathname.replace(/\/$/, '');
  const path = location.pathname;
  return base && path.startsWith(base + '/') ? path.slice(base.length) : path;
}

const fit = s => (typeof s === 'string' && s.length > 0 ? s.slice(0, MAX_TEXT) : null);

async function flush(state) {
  if (state.stopped) return;
  if (state.busy) { schedule(state, state.quiet); return; }

  const path = pathOf();
  if (!state.sent.has(path)) state.sent.set(path, new Set());
  const sent = state.sent.get(path);

  let fresh = [];
  try {
    for (const e of read({ observe: true })) {
      if (e.hidden || e.disabled || e.excluded || (e.type || '') === 'password'
          || /(^|\s)cc-/i.test(e.autocomplete || '')) continue;
      if (typeof e.selector !== 'string' || e.selector.length === 0 || e.selector.length > MAX_SELECTOR) continue;
      const item = { element: e.element, role: fit(e.role), label: fit(e.label), type: fit(e.type), form: fit(e.form), selector: e.selector };
      const key = JSON.stringify(item);
      if (!sent.has(key)) fresh.push([key, item]);
    }
  } catch (error) {
    console.debug('[MentorAgent] page.observe: the page could not be read; the observation stops.', error);
    end(state);
    return;
  }
  if (fresh.length === 0) return;

  let limit = null;
  if (sent.size + fresh.length > MAX_ELEMENTS) {
    fresh = fresh.slice(0, Math.max(0, MAX_ELEMENTS - sent.size));
    limit = 'elements';
  }
  const batch = [];
  let bytes = 2;
  for (const [key, item] of fresh) {
    const size = new TextEncoder().encode(key).length + 1;
    if (batch.length > 0 && bytes + size > MAX_BYTES) break;
    batch.push([key, item]);
    bytes += size;
  }
  const more = batch.length < fresh.length;
  if (more && limit === 'elements') limit = null;   // the rest goes in the next message; the limit with the last
  if (state.reports + 1 >= state.reportsMax && !limit) limit = 'reports';
  if (batch.length === 0) { end(state); return; }

  state.busy = true;
  state.last = Date.now();
  state.reports++;
  for (const [key] of batch) sent.add(key);
  let go = false;
  try {
    go = await state.dotnet.invokeMethodAsync('OnPageObserved', path, batch.map(b => b[1]), limit);
  } catch (error) {
    console.debug('[MentorAgent] page.observe: the application did not take the page; the observation stops.', error);
  } finally {
    state.busy = false;
  }

  if (limit) {
    console.warn('[MentorAgent] page.observe-limit: the observation of the pages stopped (' + limit + ').');
    end(state);
  } else if (go !== true) {
    end(state);
  } else if (more) {
    schedule(state, state.gap);
  }
}
