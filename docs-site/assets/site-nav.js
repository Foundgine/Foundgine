(function () {
  "use strict";
  function init() {
    var header = document.querySelector(".site-header");
    var nav = document.getElementById("site-nav");
    var toggle = document.querySelector(".nav-toggle");
    if (!header || !nav || !toggle) return;
    var items = Array.prototype.slice.call(nav.querySelectorAll(".has-submenu"));

    // Technical overview submenu: one topic group open at a time.
    var tabs = Array.prototype.slice.call(nav.querySelectorAll(".ovm-tab"));
    var wide = window.matchMedia("(min-width:1081px)");
    function showGroup(active) {
      tabs.forEach(function (tab) {
        var on = tab === active;
        tab.setAttribute("aria-expanded", String(on));
        document.getElementById(tab.getAttribute("aria-controls")).hidden = !on;
      });
    }
    tabs.forEach(function (tab) {
      tab.addEventListener("click", function () {
        showGroup(!wide.matches && tab.getAttribute("aria-expanded") === "true" ? null : tab);
      });
      tab.addEventListener("mouseenter", function () { if (wide.matches) showGroup(tab); });
    });

    function setItem(item, open) {
      if (open && item.classList.contains("ov-item")) showGroup(wide.matches ? tabs[0] : null);
      item.classList.toggle("is-open", open);
      item.querySelector(".submenu-toggle").setAttribute("aria-expanded", String(open));
    }
    function closeItems(except) {
      items.forEach(function (item) { if (item !== except) setItem(item, false); });
    }
    function setMenu(open) {
      nav.classList.toggle("is-open", open);
      toggle.setAttribute("aria-expanded", String(open));
      toggle.setAttribute("aria-label", open ? "Close navigation" : "Open navigation");
      if (!open) closeItems();
    }

    toggle.addEventListener("click", function () { setMenu(toggle.getAttribute("aria-expanded") !== "true"); });
    items.forEach(function (item) {
      item.querySelector(".submenu-toggle").addEventListener("click", function () {
        var open = !item.classList.contains("is-open");
        closeItems(item);
        setItem(item, open);
      });
    });
    nav.addEventListener("click", function (event) { if (event.target.closest("a")) setMenu(false); });
    document.addEventListener("click", function (event) { if (!header.contains(event.target)) setMenu(false); });
    document.addEventListener("keydown", function (event) {
      if (event.key !== "Escape") return;
      var openItem = nav.querySelector(".has-submenu.is-open");
      var menuOpen = nav.classList.contains("is-open");
      setMenu(false);
      if (menuOpen) toggle.focus();
      else if (openItem) openItem.querySelector(".submenu-toggle").focus();
    });
  }
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", init, { once: true });
  else init();
})();
