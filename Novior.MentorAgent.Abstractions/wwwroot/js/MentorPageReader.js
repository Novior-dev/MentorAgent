// MentorAgent — reads what a page offers, structure only: the interactive elements with their tag, role, label, type,
// option labels, form and a stable selector. It never reads a value: not an input's, not a text area's, not the text of
// an editable element, not the option a select has chosen. The flags (autocomplete, hidden, disabled, excluded) are for
// the rules MentorAgent applies before anything leaves (a password, card data, a hidden, disabled or excluded element).
// One reader for the scan of Novior.MentorAgent.Scanner (story 1636), which runs it in the browser it drives, and for
// the observation of the widget in production (story 1640, read({ observe: true })), so both apply the same rules.
// 1636 E: the chat widget of MentorAgent is not the page's (its root is .bm-root), and an element with no box on the
// page, hidden by its own style or by an ancestor's, is hidden.
// 1640: the rows of a table or a grid are data, not structure. An element in a row is read once for its column, with
// the column's header as its label and a selector that fits every row (page.observe-repeated). In observation the same
// holds for every structure that repeats, a list of links, cards of records, a feed: an element whose place in the page,
// without the positions among its siblings, is found twice or more is read once, with no label and no options. A field
// with a label tied to it (<label for>, aria-labelledby) and an id of its own is the exception, as in a form whose
// fields share a wrapper: it keeps its label, and the application keeps it only once two users saw it the same.
export function read(options) {
  const observe = !!(options && options.observe);
  const WIDGET = '.bm-root';
  const MAX = 600;
  const NATIVE = ['a', 'button', 'input', 'select', 'textarea', 'summary'];
  const ROLES = new Set(['button', 'link', 'tab', 'menuitem', 'menuitemcheckbox', 'menuitemradio', 'checkbox', 'radio',
    'switch', 'combobox', 'textbox', 'searchbox', 'listbox', 'option', 'slider', 'spinbutton', 'treeitem']);
  const EDITABLE = new Set(['textbox', 'searchbox', 'combobox', 'spinbutton', 'slider']);
  const CELL = 'td,th,[role=cell],[role=gridcell],[role=rowheader]';
  const MAX_STEPS = 16;
  const text = s => (s || '').replace(/\s+/g, ' ').trim();
  const count = s => { try { return document.querySelectorAll(s).length; } catch { return 0; } };
  const unique = s => count(s) === 1;
  const forms = Array.from(document.forms);
  const counts = new Map();

  const found = new Map();
  for (const el of document.querySelectorAll('a[href],button,input,select,textarea,summary,[role],[contenteditable=""],[contenteditable="true"]')) {
    const tag = el.tagName.toLowerCase();
    const role = el.getAttribute('role');
    if (!NATIVE.includes(tag) && !(role && ROLES.has(role)) && !el.isContentEditable) continue;
    if (el.closest(WIDGET)) continue;
    const type = tag === 'input' ? (el.getAttribute('type') || 'text').toLowerCase()
      : tag === 'button' ? (el.getAttribute('type') || 'submit').toLowerCase() : null;
    const style = getComputedStyle(el);
    const flags = {
      autocomplete: el.getAttribute('autocomplete'),
      hidden: el.hidden || type === 'hidden' || el.closest('[hidden],[aria-hidden="true"]') !== null
        || style.display === 'none' || style.visibility === 'hidden' || el.getClientRects().length === 0,
      disabled: el.disabled === true || el.getAttribute('aria-disabled') === 'true',
      excluded: el.closest('[data-mentor-exclude]') !== null,
    };
    const row = rowOf(el);
    const schema = row ? row.selector : observe && !tied(el, tag) ? repeated(el) : null;
    const item = {
      element: tag,
      role: role,
      label: row ? row.header : schema ? null : labelOf(el, tag, role),
      type: type,
      options: tag === 'select' && !observe && !schema ? Array.from(el.options).map(o => text(o.label || o.textContent)) : [],
      form: formOf(el, !!schema),
      selector: schema || selectorOf(el),
      ...flags,
    };
    // A schema is read once: the first of its elements that MentorAgent may let out stands for all of them.
    const before = found.get(item.selector);
    if (before && !(before.hidden || before.disabled || before.excluded)) continue;
    if (!before && found.size >= MAX) break;
    found.set(item.selector, item);
  }
  return Array.from(found.values());

  function labelOf(el, tag, role) {
    const aria = el.getAttribute('aria-label');
    if (aria) return text(aria);
    const by = el.getAttribute('aria-labelledby');
    if (by) {
      const t = text(by.split(/\s+/).map(id => document.getElementById(id)).filter(Boolean).map(textOf).join(' '));
      if (t) return t;
    }
    if (el.labels && el.labels.length) {
      const t = text(Array.from(el.labels).map(textOf).join(' '));
      if (t) return t;
    }
    // The text of an element is its label only where it cannot be what someone typed.
    const editable = el.isContentEditable || tag === 'textarea' || tag === 'input' || tag === 'select' || EDITABLE.has(role);
    if (!editable) {
      const t = text(el.textContent);
      if (t) return t;
    }
    return text(el.getAttribute('placeholder') || el.getAttribute('title') || '');
  }

  // The text of a label without the fields in it: a label that holds its select or its text area would otherwise
  // carry their options and what was typed (page.observe-values).
  function textOf(node) {
    const copy = node.cloneNode(true);
    copy.querySelectorAll('input,select,textarea,[contenteditable=""],[contenteditable="true"]').forEach(n => n.remove());
    return copy.textContent;
  }

  // In a structure that repeats, a form's id may be its record's: only its name or its label then.
  function formOf(el, schema) {
    const f = el.form || el.closest('form');
    if (!f) return null;
    return text(f.getAttribute('aria-label') || f.getAttribute('name') || (schema ? '' : f.id) || '') || ('form ' + (forms.indexOf(f) + 1));
  }

  function selectorOf(el) {
    if (observe) return observedSelectorOf(el);
    if (el.id && unique('#' + CSS.escape(el.id))) return '#' + CSS.escape(el.id);
    const tag = el.tagName.toLowerCase();
    for (const attribute of ['data-mentor-id', 'data-testid', 'name', 'aria-label']) {
      const v = el.getAttribute(attribute);
      if (!v) continue;
      const s = tag + '[' + attribute + '="' + CSS.escape(v) + '"]';
      if (unique(s)) return s;
    }
    return pathOf(el, true, null);
  }

  // BUG-256 — in observation the selector carries nothing of the page: an id, a name, a test id, a class or an
  // aria-label may be a record's (#order-1002, the id of a customer's card), and two users who open the same record
  // see it the same. Only the developer's data-mentor-id, which MentorAgent's own conventions fill with names, never a
  // row's value, and otherwise the element's place by tag and position, from the nearest ancestor with a data-mentor-id
  // of its own, or from the body. Deeper than MAX_STEPS steps, the element is left out (null).
  function observedSelectorOf(el) {
    const parts = [];
    for (let n = el; n && n.nodeType === 1 && n !== document.documentElement; n = n.parentElement) {
      const id = n.getAttribute('data-mentor-id');
      if (id) {
        const s = n.tagName.toLowerCase() + '[data-mentor-id="' + CSS.escape(id) + '"]';
        if (unique(s)) { parts.unshift(s); break; }
      }
      if (parts.length >= MAX_STEPS) return null;
      let part = n.tagName.toLowerCase();
      const same = n.parentElement ? Array.from(n.parentElement.children).filter(c => c.tagName === n.tagName) : [];
      if (same.length > 1) part += ':nth-of-type(' + (same.indexOf(n) + 1) + ')';
      parts.unshift(part);
    }
    return parts.join(' > ');
  }

  // The element's place in the page, each step a tag. With `positions`, each step has its position among the siblings
  // of the same tag, and the path starts at the nearest ancestor with an id of its own (or the document): a selector for
  // this element alone. Without, no position and no id, since the id of a row or a card is often the record's own: a
  // selector for every element in the same place. `keep` keeps one step's position (a cell: its column).
  function pathOf(el, positions, keep) {
    const parts = [];
    for (let n = el; n && n.nodeType === 1 && n !== document.documentElement && parts.length < 12; n = n.parentElement) {
      if (positions && n !== el && n.id && unique('#' + CSS.escape(n.id))) { parts.unshift('#' + CSS.escape(n.id)); break; }
      let part = n.tagName.toLowerCase();
      if (n === keep) part += ':nth-child(' + (Array.from(n.parentElement.children).indexOf(n) + 1) + ')';
      else if (positions) {
        const same = n.parentElement ? Array.from(n.parentElement.children).filter(c => c.tagName === n.tagName) : [];
        if (same.length > 1) part += ':nth-of-type(' + (same.indexOf(n) + 1) + ')';
      }
      parts.unshift(part);
    }
    return parts.join(' > ');
  }

  // An element in a row of a table's or a grid's body (a row with data cells, also in tfoot): the selector of its
  // column, the same in every row, and the column's header as its label. Null when it is not in such a row.
  function rowOf(el) {
    const cell = el.closest(CELL);
    if (!cell || !cell.parentElement) return null;
    const tr = cell.parentElement;
    const table = tr.closest('table,[role=grid],[role=table],[role=treegrid]');
    if (!table) return null;
    if (tr.closest('thead') || !Array.from(tr.children).some(c => c.matches('td,[role=cell],[role=gridcell]'))) return null;
    const column = Array.from(tr.children).indexOf(cell);
    return { selector: pathOf(el, false, cell), header: headerOf(table, column) };
  }

  function headerOf(table, column) {
    const rows = Array.from(table.querySelectorAll('tr,[role=row]'));
    const row = table.querySelector('thead tr')
      || rows.find(r => Array.from(r.children).some(c => c.matches('th,[role=columnheader]'))
                        && !Array.from(r.children).some(c => c.matches('td,[role=cell],[role=gridcell]')));
    const cell = row ? row.children[column] : null;
    return cell ? text(cell.getAttribute('aria-label') || cell.textContent) || null : null;
  }

  // A field with an id of its own and a label tied to it by that id or by aria-labelledby, not one that wraps it.
  function tied(el, tag) {
    if (!['input', 'select', 'textarea'].includes(tag) || !el.id || !unique('#' + CSS.escape(el.id))) return false;
    if (el.getAttribute('aria-labelledby')) return true;
    return Array.from(el.labels || []).some(l => l.htmlFor === el.id && !l.contains(el));
  }

  // Observation: the element's place without the positions among siblings. Found twice or more, it is a structure that
  // repeats (page.observe-repeated), and that place is its selector.
  function repeated(el) {
    const path = pathOf(el, false, null);
    if (!counts.has(path)) counts.set(path, count(path));
    return counts.get(path) >= 2 ? path : null;
  }
}
