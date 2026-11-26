(function () {
  "use strict";
  function init() {
    var toc = document.querySelector(".toc");
    var main = document.querySelector("main");
    if (!toc || !main) return;
    var label = toc.querySelector(".toc-label");
    var returnLink = toc.querySelector(".toc-return");
    Array.prototype.slice.call(toc.children).forEach(function (child) {
      if (child !== label && child !== returnLink) child.remove();
    });
    var headings = Array.prototype.slice.call(main.querySelectorAll("h2, h3"));
    if (!headings.length) { toc.hidden = true; return; }
    var list = document.createElement("ul");
    list.className = "toc-list";
    headings.forEach(function (heading, index) {
      if (!heading.id) {
        var base = heading.textContent.trim().toLowerCase()
          .normalize("NFKD").replace(/[\u0300-\u036f]/g, "")
          .replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "") || "section";
        var id = base, suffix = 2;
        while (document.getElementById(id)) id = base + "-" + suffix++;
        heading.id = id;
      }
      var item = document.createElement("li");
      if (heading.tagName === "H3") item.className = "toc-subitem";
      var link = document.createElement("a");
      link.href = "#" + encodeURIComponent(heading.id);
      link.textContent = heading.textContent.trim();
      link.addEventListener("click", function () {
        var openMenus = document.querySelectorAll(".toc a[aria-current='location']");
        Array.prototype.forEach.call(openMenus, function (a) { a.removeAttribute("aria-current"); });
        link.setAttribute("aria-current", "location");
      });
      item.appendChild(link); list.appendChild(item);
    });
    toc.appendChild(list);
  }
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", init);
  else init();
})();
