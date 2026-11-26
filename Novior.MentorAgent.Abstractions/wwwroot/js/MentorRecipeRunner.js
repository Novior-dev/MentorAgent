// MentorAgent (story 1638) — a recipe run in the user's browser: the steps of a recipe the application wrote, on the
// elements its selectors find, with the values the model gave (recipe.run). Between two steps the page must be still.
// An element that is not there, is hidden, disabled, or inside data-mentor-exclude stops the recipe: "not done", with
// the step and why (recipe.element-missing); the runner never looks for a similar element. It answers
// { done, step, reason } and never a value of the page. A row step finds the row of a table or a grid by the text of
// one of its cells, compared here, in the browser: the page's filter first, then the rows' container scrolled a page
// at a time, so that a virtualized grid renders the rows further down (recipe.row); after each scroll it waits for the
// middle of the rows' view to show a row with its key, not only for the page to be still, as a grid on a busy server
// renders them after a round trip (BUG-252); without a filter, on a grid that has drawn no row it may read yet, it
// first waits for one. Nothing inside data-mentor-exclude
// is read (recipe.row-excluded): excluded rows, and excluded key cells, are never compared; when every row found is
// excluded the step answers "excluded" at once, whatever the key, so that the answer says nothing of what the area
// holds, and nothing is typed in a filter nor scrolled. An excluded filter is not written either.
// A password field, or one the page marks for a password, a one-time code or a card's number or code, is never written
// (story 1319, form.sensitive): "sensitive", unless the step says the application let its field in (allowSensitive).
// A sort step clicks a grid's header until its aria-sort says the order asked, three clicks at most (story 1319 B,
// grid.sort): after each click it waits for aria-sort to change, as long as a "wait" step may wait, not for the page to be
// still, because the page may sort after a round trip to its server. A header without aria-sort is taken as not sorted; if it still has none after a
// click, it does not say its order: "no-sort-state". Not the order asked after three clicks: "not-sorted". It reads the
// header's state only, never a row.

const QUIET = 150;       // the page must stay still this long after a step
const STILL_MAX = 5000;  // a page that never stops changing is taken as it is after this long
const WAIT_MAX = 10000;  // how long a "wait" step, or an option of a combobox, may take to appear
const SCROLLS_MAX = 500; // a row step scrolls at most this many times
const SORT_CLICKS = 3;   // a sort step clicks a header at most this many times

/** Runs the steps in order; `timing` is for the tests only. */
export async function run(steps, timing) {
  const t = {
    quiet: (timing && timing.quiet) || QUIET,
    still: (timing && timing.still) || STILL_MAX,
    wait: (timing && timing.wait) || WAIT_MAX,
  };
  if (!Array.isArray(steps)) return { done: false, step: 0, reason: 'no-steps' };
  for (let i = 0; i < steps.length; i++) {
    let reason;
    try {
      reason = await one(steps[i] || {}, t);
    } catch {
      reason = 'error';
    }
    if (reason) return { done: false, step: i, reason };
    await still(t);
  }
  return { done: true, step: steps.length, reason: null };
}

async function one(step, t) {
  const kind = step.kind;
  if (kind === 'wait') return await waitFor(step.selector, t.wait) ? null : 'missing';
  if (kind === 'row') return await row(step, t);
  if (kind === 'sort') return await sort(step, t);

  const found = usable(step.selector);
  if (typeof found === 'string') return found;
  const el = found;

  switch (kind) {
    case 'set': return set(el, step.value == null ? '' : String(step.value), step.allowSensitive === true);
    case 'choose': return await choose(el, String(step.value ?? ''), t);
    case 'check': return check(el, String(step.value ?? 'true').toLowerCase() !== 'false');
    case 'click': el.click(); return null;
    default: return 'unknown-step';
  }
}

/** The element, or why it cannot be used: missing, excluded, hidden, disabled. */
function usable(selector) {
  let el = null;
  try {
    el = typeof selector === 'string' && selector ? document.querySelector(selector) : null;
  } catch {
    return 'missing';
  }
  if (!el) return 'missing';
  if (el.closest('[data-mentor-exclude]')) return 'excluded';
  if (hidden(el)) return 'hidden';
  if (disabled(el)) return 'disabled';
  return el;
}

function hidden(el) {
  if (!el.isConnected || el.getClientRects().length === 0) return true;
  const style = getComputedStyle(el);
  return style.visibility === 'hidden' || style.visibility === 'collapse';
}

function disabled(el) {
  return el.disabled === true || el.matches(':disabled') || el.closest('[aria-disabled="true"]') !== null;
}

/**
 * Writes the value as typing would: the native setter, then input and change (bind:event="oninput" and @bind; the
 * value React's controlled fields and Angular's forms read), then the field is left, as the user leaves it for the next
 * one (Angular's updateOn: 'blur'; story 1641).
 */
function set(el, value, allowSensitive) {
  let proto = null;
  if (el instanceof HTMLInputElement) {
    if (['checkbox', 'radio', 'file', 'button', 'submit', 'reset', 'image'].includes(el.type)) return 'wrong-element';
    if (sensitive(el) && !allowSensitive) return 'sensitive';
    proto = HTMLInputElement.prototype;
  } else if (el instanceof HTMLTextAreaElement) {
    proto = HTMLTextAreaElement.prototype;
  } else if (el.isContentEditable) {
    el.focus();
    el.textContent = value;
    el.dispatchEvent(new Event('input', { bubbles: true }));
    return null;
  } else {
    return 'wrong-element';
  }
  el.focus();
  Object.getOwnPropertyDescriptor(proto, 'value').set.call(el, value);
  el.dispatchEvent(new Event('input', { bubbles: true }));
  el.dispatchEvent(new Event('change', { bubbles: true }));
  el.blur();
  return null;
}

/** The autocomplete tokens of a field that holds a secret or a payment's detail. */
const SENSITIVE_AUTOCOMPLETE = ['current-password', 'new-password', 'one-time-code', 'cc-number', 'cc-csc'];

/** Whether the field holds a password, or what the page marks as a secret: never written unless let in. */
function sensitive(el) {
  if (el.type === 'password') return true;
  const tokens = (el.getAttribute('autocomplete') || '').toLowerCase().split(/\s+/);
  return tokens.some(t => SENSITIVE_AUTOCOMPLETE.includes(t));
}

/** A select's option, or an ARIA combobox's, by its label. */
async function choose(el, label, t) {
  const wanted = label.trim().toLowerCase();
  if (el instanceof HTMLSelectElement) {
    const option = Array.from(el.options).find(o => o.label.trim().toLowerCase() === wanted || o.text.trim().toLowerCase() === wanted);
    if (!option || option.disabled) return 'no-option';
    el.focus();
    Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value').set.call(el, option.value);
    el.dispatchEvent(new Event('input', { bubbles: true }));
    el.dispatchEvent(new Event('change', { bubbles: true }));
    return null;
  }
  if (el.getAttribute('role') === 'combobox') {
    el.click();
    const listId = el.getAttribute('aria-controls') || el.getAttribute('aria-owns');
    const until = Date.now() + t.wait;
    while (Date.now() < until) {
      const scope = (listId && document.getElementById(listId)) || document;
      const option = Array.from(scope.querySelectorAll('[role="option"]'))
        .find(o => (o.textContent || '').trim().toLowerCase() === wanted && !hidden(o) && !disabled(o));
      if (option) {
        option.click();
        return null;
      }
      await delay(50);
    }
    return 'no-option';
  }
  return 'wrong-element';
}

/** Checks or unchecks a checkbox, a radio or an ARIA checkbox, with a click, as the user would. */
function check(el, on) {
  if (el instanceof HTMLInputElement && (el.type === 'checkbox' || el.type === 'radio')) {
    if (el.checked !== on) el.click();
    return null;
  }
  const role = el.getAttribute('role');
  if (role === 'checkbox' || role === 'switch' || role === 'radio') {
    if ((el.getAttribute('aria-checked') === 'true') !== on) el.click();
    return null;
  }
  return 'wrong-element';
}

/** The row whose key cell reads the value: as it is, after the page's filter, or scrolling; then its target clicked. */
async function row(step, t) {
  const key = String(step.value ?? '').trim().toLowerCase();
  if (!key || !step.cell) return 'no-row';
  // BUG-252: a grid still loading, a round trip after its page, has no row the step may read yet: it waits for one, at
  // most `still`, before it answers. With a filter it does not: a page may show its rows only once filtered, and the
  // filter's wait takes the rows drawn late (1639). Rows inside data-mentor-exclude are no such row, so an area that is excluded and a
  // grid that has not drawn its rows are not taken for every row excluded. Nor are a grid's placeholder rows, drawn before
  // its data with an empty key cell: the step waits for a row with its key.
  if (!step.filter && !readRows(step).split('\n').some(k => k)) await newRows(step, '', t);
  const rows = allRows(step.selector);
  if (rows.length > 0 && rows.every(excluded)) return 'excluded';
  let found = rowWith(step, key);

  if (!found && step.filter) {
    const filter = usable(step.filter);
    if (typeof filter === 'string') return filter;
    const before = readRows(step);
    const wrong = set(filter, String(step.value));
    if (wrong) return wrong;
    found = await rowAfterChange(step, key, before, t);
  }

  if (!found) {
    const first = allRows(step.selector).find(r => !excluded(r));
    const box = first && scroller(first);
    if (box) {
      // BUG-252: a virtualized grid renders the rows a scroll brings after a round trip to its server, which on a busy
      // server is longer than a quiet while: waiting for quiet alone read the rows before they came, and scrolled past
      // the row. After a scroll the step waits, at most `still`, for the middle of the rows' view to show a row with its
      // key: a spacer or a placeholder row there is a load still coming. A scroll within what the grid already drew
      // (its overscan) changes nothing and is covered at once, and so is a list whose rows are all in the page.
      const scrollTo = async top => {
        const from = box.scrollTop;
        box.scrollTop = top;
        if (box.scrollTop !== from) await drawnInView(step, box, t);
        await still(t);
      };
      await scrollTo(0);
      found = rowWith(step, key);
      for (let i = 0; !found && i < SCROLLS_MAX; i++) {
        const before = box.scrollTop;
        await scrollTo(before + Math.max(box.clientHeight - 40, 20));
        found = rowWith(step, key);
        if (box.scrollTop === before) break;   // the end of the rows
      }
    }
  }

  if (!found) return 'no-row';
  found.scrollIntoView({ block: 'center' });
  if (!step.target) return null;
  const target = found.querySelector(step.target);
  if (!target) return 'missing';
  if (hidden(target)) return 'hidden';
  if (disabled(target)) return 'disabled';
  target.click();
  return null;
}

/** Clicks the header until its aria-sort reads the order asked ("ascending" or "descending"). */
async function sort(step, t) {
  const wanted = step.value === 'descending' ? 'descending' : 'ascending';
  for (let clicks = 0; ; clicks++) {
    const found = usable(step.selector);
    if (typeof found === 'string') return found;
    const state = sortState(found);
    if (state === wanted) return null;
    if (clicks > 0 && state === null) return 'no-sort-state';
    if (clicks === SORT_CLICKS) return 'not-sorted';
    found.click();
    if (!await sortChanged(step.selector, state, t)) return state === null ? 'no-sort-state' : 'not-sorted';
  }
}

/** The header's aria-sort, on the element or on the header that holds it; null, it has none. */
function sortState(el) {
  const holder = el.hasAttribute('aria-sort') ? el : el.closest('th, [role="columnheader"]');
  return holder && holder.hasAttribute('aria-sort') ? holder.getAttribute('aria-sort') : null;
}

/** Whether the header's aria-sort changes from `before` within `wait`, the header found again as the page redraws it. */
function sortChanged(selector, before, t) {
  const now = () => {
    const el = safeQuery(selector);
    return el ? sortState(el) : before;
  };
  return new Promise(resolve => {
    if (now() !== before) { resolve(true); return; }   // a page that sorts at once, in the click's handler
    const done = changed => { observer.disconnect(); clearTimeout(cap); resolve(changed); };
    const observer = new MutationObserver(() => { if (now() !== before) done(true); });
    observer.observe(document.documentElement, { subtree: true, childList: true, attributes: true });
    const cap = setTimeout(() => done(now() !== before), t.wait);
  });
}

function rowWith(step, key) {
  for (const r of allRows(step.selector)) {
    if (excluded(r)) continue;   // never read
    let cell = null;
    try {
      cell = r.querySelector(step.cell);
    } catch {
      return null;
    }
    if (cell && !excluded(cell) && (cell.textContent || '').trim().toLowerCase() === key) return r;
  }
  return null;
}

/**
 * What a row step reads of the rows: the key cell of each row that is not excluded, in order. Nothing excluded is read,
 * so an excluded row that changes is no change.
 */
function readRows(step) {
  const keys = [];
  for (const r of allRows(step.selector)) {
    if (excluded(r)) continue;   // never read
    let cell = null;
    try {
      cell = r.querySelector(step.cell);
    } catch {
      return '';
    }
    keys.push(cell && !excluded(cell) ? (cell.textContent || '').trim().toLowerCase() : '');
  }
  return keys.join('\n');
}

/** The rows the selector finds, or none. */
function allRows(selector) {
  try {
    return Array.from(document.querySelectorAll(selector));
  } catch {
    return [];
  }
}

/** Whether the element is inside data-mentor-exclude, itself included: what is there is never read. */
function excluded(el) {
  return !!el.closest('[data-mentor-exclude]');
}

function safeQuery(selector) {
  try {
    return document.querySelector(selector);
  } catch {
    return null;
  }
}

/** The nearest container of the element that scrolls, or the page. */
function scroller(el) {
  for (let p = el.parentElement; p; p = p.parentElement) {
    const style = getComputedStyle(p);
    if (/(auto|scroll)/.test(style.overflowY) && p.scrollHeight > p.clientHeight) return p;
  }
  const page = document.scrollingElement;
  return page && page.scrollHeight > page.clientHeight ? page : null;
}

async function waitFor(selector, max) {
  const until = Date.now() + max;
  while (Date.now() < until) {
    if (typeof usable(selector) !== 'string') return true;
    await delay(50);
  }
  return false;
}

/**
 * After the page's filter was written: the row as soon as a change of the page brings it; else what the page holds once
 * it has been still for a while after the rows it reads changed from `before`, or after `still` if they never change.
 * The page that filters may answer after a round trip to its server longer than a quiet while (Blazor Server): waiting
 * for quiet alone would look before the rows changed. A change that leaves the rows as they were is not the answer: on
 * Blazor Server the event's handler renders once at its first await, before the filtered rows come (fixes 1639).
 * Nor is the first rows of a table that showed none when the step began: they are the table being drawn (a virtualized
 * grid draws its rows only after it has measured itself, a round trip later on Blazor Server), and they become `before`
 * (1639; the cause of its test that failed under load). A page that shows its rows only once filtered, and answers
 * without the key, ends at `still`.
 */
function rowAfterChange(step, key, before, t) {
  return new Promise(resolve => {
    let timer = 0;
    const done = found => { observer.disconnect(); clearTimeout(timer); clearTimeout(cap); resolve(found); };
    const observer = new MutationObserver(() => {
      const found = rowWith(step, key);
      if (found) { done(found); return; }
      clearTimeout(timer);
      const rows = readRows(step);
      if (rows === before) return;   // the same rows: not yet the filter's answer
      if (!before.trim()) {          // no rows shown before: these are the table's own, drawn late
        before = rows;
        return;
      }
      timer = setTimeout(() => done(rowWith(step, key)), t.quiet);
    });
    observer.observe(document.documentElement, { subtree: true, childList: true, attributes: true, characterData: true });
    const cap = setTimeout(() => done(rowWith(step, key)), t.still);
  });
}

/**
 * BUG-252: waits, at most `still`, until the middle of the rows' view shows a row the step may read with its key: the
 * rows a scroll brings to a virtualized grid come after a round trip, a spacer or an empty placeholder row first.
 */
function drawnInView(step, box, t) {
  const covered = () => {
    const page = box === document.scrollingElement;
    const r = page ? { top: 0, bottom: window.innerHeight } : box.getBoundingClientRect();
    const y = (Math.max(r.top, 0) + Math.min(r.bottom, window.innerHeight)) / 2;
    return allRows(step.selector).some(row => {
      if (excluded(row)) return false;   // never read
      let cell = null;
      try {
        cell = row.querySelector(step.cell);
      } catch {
        return false;
      }
      if (!cell || excluded(cell) || !(cell.textContent || '').trim()) return false;
      const b = row.getBoundingClientRect();
      return b.top <= y && b.bottom >= y;
    });
  };
  return new Promise(resolve => {
    const until = Date.now() + t.still;
    const tick = () => (covered() || Date.now() > until ? resolve() : setTimeout(tick, 50));
    tick();
  });
}

/**
 * BUG-252: whether a row a step reads shows a key that was not in `shown` within `still`, then the page is still for a
 * while. The rows a scroll brings to a virtualized grid come after a round trip, and a grid that loads them draws empty
 * placeholder rows first: a change of the rows alone is not yet the answer, a key that was not shown is. Nothing excluded
 * is read, so an excluded row that changes is no change.
 */
function newRows(step, shown, t) {
  const before = new Set(shown.split('\n').filter(k => k));
  const arrived = () => readRows(step).split('\n').some(k => k && !before.has(k));
  return new Promise(resolve => {
    let timer = 0;
    let changed = false;
    const done = value => { observer.disconnect(); clearTimeout(timer); clearTimeout(cap); resolve(value); };
    const settle = () => { clearTimeout(timer); timer = setTimeout(() => done(true), t.quiet); };
    const observer = new MutationObserver(() => {
      if (!changed && !arrived()) return;
      changed = true;
      settle();
    });
    observer.observe(document.documentElement, { subtree: true, childList: true, attributes: true, characterData: true });
    const cap = setTimeout(() => done(changed), t.still);
    if (arrived()) { changed = true; settle(); }   // already drawn
  });
}

/** Waits until the page has not changed for a while, or gives up waiting after `still`. */
function still(t) {
  return new Promise(resolve => {
    let timer = 0;
    const done = () => { observer.disconnect(); clearTimeout(timer); clearTimeout(cap); resolve(); };
    const observer = new MutationObserver(() => { clearTimeout(timer); timer = setTimeout(done, t.quiet); });
    observer.observe(document.documentElement, { subtree: true, childList: true, attributes: true, characterData: true });
    timer = setTimeout(done, t.quiet);
    const cap = setTimeout(done, t.still);
  });
}

function delay(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
}
