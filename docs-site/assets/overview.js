(function () {
  "use strict";
  var side = document.getElementById("ov-side");
  var main = document.getElementById("content");
  if (!side || !main) return;
  var openBtn = document.querySelector(".ov-open");
  var closeBtn = side.querySelector(".ov-close");
  var scrim = document.querySelector(".ov-scrim");
  var filter = side.querySelector(".ov-filter");
  var empty = side.querySelector(".ov-empty");
  var where = document.querySelector(".ov-where");
  var groups = Array.prototype.slice.call(side.querySelectorAll("details"));
  var links = Array.prototype.slice.call(side.querySelectorAll("li a"));

  // Wide tables scroll inside their own box instead of widening the page.
  main.querySelectorAll("table").forEach(function (table) {
    if (table.parentElement.classList.contains("table-wrap")) return;
    var box = document.createElement("div");
    box.className = "ov-scroll";
    table.parentNode.insertBefore(box, table);
    box.appendChild(table);
  });

  function drawer(open) {
    side.classList.toggle("is-open", open);
    scrim.hidden = !open;
    openBtn.setAttribute("aria-expanded", String(open));
    document.body.style.overflow = open ? "hidden" : "";
  }
  openBtn.addEventListener("click", function () { drawer(true); closeBtn.focus(); });
  closeBtn.addEventListener("click", function () { drawer(false); openBtn.focus(); });
  scrim.addEventListener("click", function () { drawer(false); });
  document.addEventListener("keydown", function (e) {
    if (e.key === "Escape" && side.classList.contains("is-open")) { drawer(false); openBtn.focus(); }
  });
  side.addEventListener("click", function (e) { if (e.target.closest("li a")) drawer(false); });
  window.matchMedia("(min-width:1001px)").addEventListener("change", function () { drawer(false); });

  var openBefore = null;
  filter.addEventListener("input", function () {
    var q = filter.value.trim().toLowerCase();
    if (q && !openBefore) openBefore = groups.map(function (g) { return g.open; });
    var total = 0;
    groups.forEach(function (group, i) {
      var shown = 0;
      group.querySelectorAll("li").forEach(function (li) {
        var match = !q || li.textContent.toLowerCase().indexOf(q) > -1;
        li.hidden = !match;
        if (match) shown++;
      });
      group.hidden = !shown;
      if (q) group.open = true;
      else if (openBefore) group.open = openBefore[i];
      total += shown;
    });
    if (!q) openBefore = null;
    empty.hidden = total > 0;
  });

  // Highlight the topic being read.
  var targets = links.map(function (a) {
    return { link: a, el: document.getElementById(a.getAttribute("href").slice(1)) };
  }).filter(function (t) { return t.el; });
  targets.sort(function (a, b) { return a.el.compareDocumentPosition(b.el) & 4 ? -1 : 1; });
  var current, queued = false;
  function spy() {
    queued = false;
    var found = targets[0];
    for (var i = 0; i < targets.length; i++) {
      if (targets[i].el.getBoundingClientRect().top <= 220) found = targets[i];
    }
    if (!found || found === current) return;
    if (current) current.link.removeAttribute("aria-current");
    if (current) current.link.classList.remove("is-active");
    current = found;
    current.link.classList.add("is-active");
    current.link.setAttribute("aria-current", "location");
    current.link.closest("details").open = true;
    where.textContent = current.link.textContent;
  }
  window.addEventListener("scroll", function () {
    if (!queued) { queued = true; requestAnimationFrame(spy); }
  }, { passive: true });
  spy();
})();
