
// MentorAgent (story 1641) — the part of /mentor/agent.js that ties the runner above to a front end's own hub connection
// (React, Angular, any page that is not Blazor): the recipes the hub asks for run in this page, and the answer goes back
// over HTTP, done or the step that could not be done and why, never a value of the page (recipe.front-end). The routes
// of the front end go to the hub too, from the configuration or, without one, from the links of the page
// (page.front-end-routes). The page itself is said by the application, with path() in its page context.

const ROUTES_MAX = 1000; // as the server lists at most (page.route-limit)
const ROUTE_MAX = 256;   // the longest route the server takes (page.route-shape)
const ROUTE_DEPTH = 32;  // children under children, at most: a router's configuration is never deeper

/** The path of the page shown, for the page context's "path": without its query or fragment, which can carry what the user typed. */
export function path() {
  return location.pathname || '/';
}

/**
 * Ties the recipes to `connection`, a started HubConnection of @microsoft/signalr on the hub of Novior.MentorAgent.Server.
 * options.server: the application's base address, by default the one this script was loaded from;
 * options.accessToken: a function giving the bearer token of POST /mentor/recipe-result, as the connection's own;
 * options.credentials: of that request, 'include' by default (a cookie);
 * options.routes: the front end's routes, '/orders/:id' or { route, component }, or the router's own: Angular's Routes,
 * React Router's route objects, children included; without them, the links of the page.
 * Returns { routes, report, assistantIsOn, onAssistantStatus, detach }: report() sends the routes again, after a
 * navigation that brought new links; assistantIsOn() says whether the application's assistant is on, read from the hub
 * when the connection opens (GetAssistantStatus) and followed after (AssistantStatus); onAssistantStatus(callback)
 * calls callback(on) each time it is turned off or on, and returns the function that stops it. The page disables its
 * input while it is off: the server refuses the requests anyway.
 */
export function attach(connection, options) {
  if (!connection || typeof connection.on !== 'function' || typeof connection.invoke !== 'function')
    throw new TypeError('MentorAgent: attach needs a HubConnection of @microsoft/signalr.');
  const o = options || {};
  const server = new URL(o.server || '../', o.server ? location.href : import.meta.url);
  if (!server.pathname.endsWith('/')) server.pathname += '/';

  const onRecipe = (runId, steps) => answer(connection, server, o, runId, steps);
  connection.on('RecipeRequested', onRecipe);

  // Story 1290 — on until the server says otherwise, and with a server older than these messages.
  let on = true;
  const listeners = new Set();
  const setStatus = value => {
    if (typeof value !== 'boolean' || value === on) return;
    on = value;
    for (const listener of [...listeners]) {
      try {
        listener(on);
      } catch {
        // One listener that throws does not stop the others.
      }
    }
  };
  connection.on('AssistantStatus', setStatus);
  const readStatus = async () => {
    if (connection.state !== 'Connected') return;
    try {
      setStatus(await connection.invoke('GetAssistantStatus'));
    } catch {
      // A server older than GetAssistantStatus: the assistant stays on.
    }
  };

  const report = async () => {
    if (connection.state !== 'Connected') return false;
    try {
      await connection.invoke('ReportPages', routes(o.routes));
      return true;
    } catch {
      return false;
    }
  };
  if (typeof connection.onreconnected === 'function') connection.onreconnected(() => { report(); readStatus(); });
  report();
  readStatus();

  return {
    routes: () => routes(o.routes),
    report,
    assistantIsOn: () => on,
    onAssistantStatus: callback => {
      if (typeof callback !== 'function') throw new TypeError('MentorAgent: onAssistantStatus needs a function.');
      listeners.add(callback);
      return () => listeners.delete(callback);
    },
    detach: () => {
      connection.off('RecipeRequested', onRecipe);
      connection.off('AssistantStatus', setStatus);
      listeners.clear();
    },
  };
}

async function answer(connection, server, o, runId, steps) {
  let outcome;
  try {
    outcome = await run(steps);
  } catch {
    outcome = { done: false, step: 0, reason: 'error' };
  }
  const id = connection.connectionId;
  if (!id) return;
  const query = new URLSearchParams({
    connectionId: id,
    runId: String(runId),
    done: outcome.done ? 'true' : 'false',
    step: String(outcome.step),
  });
  if (!outcome.done && outcome.reason) query.set('reason', outcome.reason);
  const headers = {};
  try {
    const token = typeof o.accessToken === 'function' ? await o.accessToken() : null;
    if (token) headers.Authorization = 'Bearer ' + token;
  } catch {
    // No token: the request goes without, and the server answers as it would to anyone.
  }
  try {
    await fetch(new URL('mentor/recipe-result?' + query, server), {
      method: 'POST',
      headers,
      credentials: o.credentials || 'include',
    });
  } catch {
    // The server did not get the answer: the turn's call ends at its own timeout and says so.
  }
}

/** The routes to report: those configured, else the links of the page with this page's origin, the page itself first. */
function routes(configured) {
  const result = [];
  const seen = new Set();
  const add = (route, component) => {
    if (result.length >= ROUTES_MAX || !route || route.length > ROUTE_MAX || route[0] !== '/' || /\s/.test(route)
        || /[?#]/.test(route.replace(/\{[^{}]*\}/g, ''))) return; // a '?' only in an optional parameter
    const key = route.toLowerCase();
    if (seen.has(key)) return;
    seen.add(key);
    result.push({ route, component: component && String(component).length <= ROUTE_MAX ? String(component) : route });
  };

  if (Array.isArray(configured)) {
    walk(configured, '', 0, add);
    return result;
  }

  add(path());
  for (const a of document.querySelectorAll('a[href]')) {
    if (a.closest('[data-mentor-exclude]')) continue;
    let url;
    try {
      url = new URL(a.getAttribute('href'), location.href);
    } catch {
      continue;
    }
    if (url.origin === location.origin) add(url.pathname);
  }
  return result;
}

/**
 * The configured routes as the router itself is given them (story 1642): a string, ours { route, component }, Angular's
 * { path, component, children, redirectTo } or React Router's { path, index, children }. A child's path is under its
 * parent's, unless it starts with '/'; an entry with children is not a page itself, its children are (path '' or index
 * is the parent's own); a redirect is not a page, nor an entry without a path (Angular's matcher). The component is
 * named only by a string: a class is named by its route, since a production build renames classes.
 */
function walk(entries, parent, depth, add) {
  if (!Array.isArray(entries) || depth > ROUTE_DEPTH) return;
  for (const given of entries) {
    if (typeof given === 'string') {
      add(template(join(parent, given)));
      continue;
    }
    if (!given || typeof given !== 'object' || given.redirectTo !== undefined) continue;
    const children = Array.isArray(given.children) ? given.children : null;
    const own = typeof given.route === 'string' ? given.route
      : typeof given.path === 'string' ? given.path
      : given.index === true || children ? '' : null;
    if (own === null) continue;
    const full = join(parent, own);
    if (children) walk(children, full, depth + 1, add);
    else add(template(full), typeof given.component === 'string' ? given.component : undefined);
  }
}

/** A child's path under its parent's: 'orders' under '/shop' is '/shop/orders', '' is '/shop', '/x' stays '/x'. */
function join(parent, own) {
  const p = own.trim();
  if (!parent || p.startsWith('/')) return p;
  return p ? parent.replace(/\/+$/, '') + '/' + p : parent;
}

/** A route of React Router or Angular as the server writes it: ':id' is '{id}', ':id?' '{id?}', '*' and '**' '{*rest}'. */
function template(route) {
  if (typeof route !== 'string') return null;
  let r = route.trim();
  if (!r.startsWith('/')) r = '/' + r;
  return r.split('/').map(segment => {
    if (segment === '*' || segment === '**') return '{*rest}';
    const m = /^:([A-Za-z_][A-Za-z0-9_]*)(\?)?$/.exec(segment);
    return m ? '{' + m[1] + (m[2] || '') + '}' : segment;
  }).join('/');
}
