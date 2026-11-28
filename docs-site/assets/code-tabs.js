(function () {
  var KEY = "foundgine-code-lang";
  var groups = Array.prototype.slice.call(document.querySelectorAll(".code-tabs"));
  if (!groups.length) return;
  function stored() { try { return localStorage.getItem(KEY); } catch (e) { return null; } }
  function save(v) { try { localStorage.setItem(KEY, v); } catch (e) {} }
  function select(lang, focusTab) {
    groups.forEach(function (g) {
      g.querySelectorAll("[role=tab]").forEach(function (t) {
        var on = t.getAttribute("data-lang") === lang;
        t.setAttribute("aria-selected", on ? "true" : "false");
        t.tabIndex = on ? 0 : -1;
        if (on && focusTab === g) t.focus();
      });
      g.querySelectorAll("[role=tabpanel]").forEach(function (p) {
        p.hidden = p.getAttribute("data-lang") !== lang;
      });
    });
  }
  groups.forEach(function (g) {
    var tabs = Array.prototype.slice.call(g.querySelectorAll("[role=tab]"));
    tabs.forEach(function (t, i) {
      t.addEventListener("click", function () { var l = t.getAttribute("data-lang"); save(l); select(l); });
      t.addEventListener("keydown", function (e) {
        var n = e.key === "ArrowRight" ? i + 1 : e.key === "ArrowLeft" ? i - 1 : null;
        if (n === null) return;
        e.preventDefault();
        var next = tabs[(n + tabs.length) % tabs.length], l = next.getAttribute("data-lang");
        save(l); select(l, g);
      });
    });
  });
  var s = stored();
  select(s === "java" ? "java" : "csharp");
})();
