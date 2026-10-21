// The demo's small browser helpers: the theme and the page language the visitor picked, kept in the browser.
// Storage can be missing (a private window, blocked site data): every read and write is guarded, and the page
// works without it.
(function () {
  var themeKey = "mentoragent.demo.theme";
  var languageKey = "mentoragent.demo.language";

  function read(key) {
    try { return window.localStorage.getItem(key); } catch (e) { return null; }
  }

  function write(key, value) {
    try { window.localStorage.setItem(key, value); } catch (e) { /* not kept: fine */ }
  }

  function prefersDark() {
    return window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches;
  }

  // Before the first paint: a stored choice wins over the system's.
  var stored = read(themeKey);
  if (stored === "light" || stored === "dark") document.documentElement.dataset.theme = stored;

  window.mentorDemo = {
    toggleTheme: function () {
      var current = document.documentElement.dataset.theme || (prefersDark() ? "dark" : "light");
      var next = current === "dark" ? "light" : "dark";
      document.documentElement.dataset.theme = next;
      write(themeKey, next);
    },
    storedLanguage: function () {
      return read(languageKey);
    },
    storeLanguage: function (code) {
      document.documentElement.lang = code;
      write(languageKey, code);
    },
    // The Copy buttons of What to try. The clipboard can be missing or refused: then the answer is false.
    copy: function (text) {
      if (!navigator.clipboard) return Promise.resolve(false);
      return navigator.clipboard.writeText(text).then(function () { return true; }, function () { return false; });
    }
  };

  var language = read(languageKey);
  if (language === "it" || language === "en") document.documentElement.lang = language;
})();
