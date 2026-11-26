// Novior.MentorAgent.Scanner (story 1636, slice B) — the clicks the scan may make to reveal what a page hides: tabs,
// menus, collapsed sections. It clicks only on what the page's structure says opens something (role="tab", a menu
// button with aria-haspopup, aria-expanded="false", <summary>), and never on a submit, on a button of a form that
// posts, on a link to another page, on what is hidden, disabled, excluded or marked data-mentor-scan="skip". Words
// only take away: a label that says delete, save, pay, send, confirm… is never clicked, and a word never makes an
// element clickable. Called with null it guards the page and marks the elements it may click, returning how many;
// called with a number it clicks that element if it is still safe, returning whether it did.
(target) => {
  const MAX = 20;
  const MARK = 'data-mentor-scan-click';
  const STOP = new RegExp('\\b(' + [
    'elimin\\w*', 'cancell\\w*', 'delet\\w*', 'remov\\w*', 'rimuov\\w*', 'rimozion\\w*', 'salva\\w*', 'save\\w*',
    'paga', 'pagare', 'pagament\\w*', 'pay', 'payment\\w*', 'invia\\w*', 'inviare', 'send\\w*', 'submit\\w*',
    'conferm\\w*', 'confirm\\w*', 'approv\\w*', 'acquist\\w*', 'buy', 'purchase\\w*', 'checkout', 'ordina',
    'logout', 'log out', 'sign out', 'signout', 'esci', 'disconnett\\w*', 'reset\\w*', 'svuota\\w*', 'clear\\w*',
    'archivia\\w*', 'archive\\w*', 'revoca\\w*', 'revoke\\w*', 'annulla\\w*', 'cancel\\w*', 'destroy\\w*', 'wipe\\w*',
    'transfer\\w*', 'trasferi\\w*', 'bonific\\w*', 'refund\\w*', 'rimbors\\w*', 'publish\\w*', 'pubblica\\w*',
    'esegui\\w*', 'execute\\w*',
  ].join('|') + ')\\b');

  guard();
  if (target === null || target === undefined) {
    let n = 0;
    for (const el of document.querySelectorAll('[' + MARK + ']')) el.removeAttribute(MARK);
    for (const el of document.querySelectorAll('[role="tab"],[aria-haspopup],[aria-expanded="false"],summary')) {
      if (n >= MAX) break;
      if (!safe(el)) continue;
      el.setAttribute(MARK, String(n++));
    }
    return n;
  }
  const el = document.querySelector('[' + MARK + '="' + Number(target) + '"]');
  if (!el || !safe(el)) return false;
  el.click();
  return true;

  // While the scan reads the page, nothing it does can submit a form, open a window or answer a dialog with a yes.
  function guard() {
    if (window.__mentorScanGuard) return;
    window.__mentorScanGuard = true;
    window.alert = () => {};
    window.confirm = () => false;
    window.prompt = () => null;
    window.print = () => {};
    window.open = () => null;
    const stop = e => { e.preventDefault(); e.stopImmediatePropagation(); };
    window.addEventListener('submit', stop, true);
    HTMLFormElement.prototype.submit = function () {};
    HTMLFormElement.prototype.requestSubmit = function () {};
  }

  function safe(el) {
    const tag = el.tagName.toLowerCase();
    const role = el.getAttribute('role');
    const popup = el.getAttribute('aria-haspopup');
    const signal = role === 'tab' || (popup !== null && popup !== 'false')
      || el.getAttribute('aria-expanded') === 'false' || tag === 'summary';
    if (!signal) return false;

    // 1636 E: never the chat widget of MentorAgent (.bm-root).
    if (el.closest('[data-mentor-scan="skip"],[data-mentor-exclude],[hidden],[aria-hidden="true"],[inert],.bm-root')) return false;
    if (el.disabled === true || el.getAttribute('aria-disabled') === 'true' || el.isContentEditable) return false;
    const style = getComputedStyle(el);
    if (style.display === 'none' || style.visibility === 'hidden' || el.getClientRects().length === 0) return false;

    if (['input', 'select', 'textarea', 'option'].includes(tag) && !(tag === 'input' && el.type === 'button')) return false;
    const declared = (el.getAttribute('type') || '').toLowerCase();
    if (['submit', 'reset', 'image'].includes(declared) || el.hasAttribute('formaction')) return false;
    const form = el.form || el.closest('form');
    if (form) {
      if (form.hasAttribute('action') || (form.getAttribute('method') || '').toLowerCase() === 'post') return false;
      if ((tag === 'button' || tag === 'input') && el.type !== 'button') return false;
    }
    const link = el.closest('a[href],area[href]');
    if (link && !samePage(link.getAttribute('href'))) return false;

    return !STOP.test(fold([el.getAttribute('aria-label'), el.textContent, el.getAttribute('title'),
      el.getAttribute('value'), el.getAttribute('name'), el.id].join(' ')));
  }

  function samePage(href) {
    try {
      const u = new URL(href, location.href);
      return u.origin === location.origin && u.pathname === location.pathname && u.search === location.search;
    } catch {
      return false;
    }
  }

  function fold(s) {
    return (s || '').replace(/([a-z])([A-Z])/g, '$1 $2').normalize('NFD').replace(/[̀-ͯ]/g, '')
      .replace(/[-_.]+/g, ' ').replace(/\s+/g, ' ').toLowerCase();
  }
}
