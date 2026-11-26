(function () {
  "use strict";

  function closeSubmenus(except) {
    document.querySelectorAll(".submenu-toggle").forEach(function (button) {
      if (button === except) return;
      button.setAttribute("aria-expanded", "false");
      var parent = button.closest(".has-submenu");
      var menuId = button.getAttribute("aria-controls");
      var menu = menuId ? document.getElementById(menuId) : null;
      if (parent) parent.classList.remove("is-open", "open");
      if (menu) menu.classList.remove("is-open", "open");
    });
  }

  function initNavigation() {
    var nav = document.getElementById("site-nav");
    var navToggle = document.querySelector(".nav-toggle");

    if (nav && navToggle) {
      navToggle.addEventListener("click", function () {
        var opening = navToggle.getAttribute("aria-expanded") !== "true";
        navToggle.setAttribute("aria-expanded", String(opening));
        navToggle.setAttribute("aria-label", opening ? "Close navigation" : "Open navigation");
        nav.classList.toggle("is-open", opening);
        nav.classList.toggle("open", opening);
        if (!opening) closeSubmenus();
      });

      nav.addEventListener("click", function (event) {
        if (event.target.closest("a")) {
          navToggle.setAttribute("aria-expanded", "false");
          navToggle.setAttribute("aria-label", "Open navigation");
          nav.classList.remove("is-open", "open");
        }
      });
    }

    document.querySelectorAll(".submenu-toggle").forEach(function (button) {
      button.addEventListener("click", function (event) {
        event.preventDefault();
        event.stopPropagation();
        var opening = button.getAttribute("aria-expanded") !== "true";
        closeSubmenus(button);
        button.setAttribute("aria-expanded", String(opening));
        var parent = button.closest(".has-submenu");
        var menuId = button.getAttribute("aria-controls");
        var menu = menuId ? document.getElementById(menuId) : null;
        if (parent) parent.classList.toggle("is-open", opening);
        if (menu) menu.classList.toggle("is-open", opening);
      });
    });

    document.addEventListener("click", function (event) {
      if (!event.target.closest(".has-submenu")) closeSubmenus();
      if (nav && navToggle && !event.target.closest(".site-header")) {
        navToggle.setAttribute("aria-expanded", "false");
        navToggle.setAttribute("aria-label", "Open navigation");
        nav.classList.remove("is-open", "open");
      }
    });

    document.addEventListener("keydown", function (event) {
      if (event.key === "Escape") {
        closeSubmenus();
        if (nav && navToggle) {
          navToggle.setAttribute("aria-expanded", "false");
          navToggle.setAttribute("aria-label", "Open navigation");
          nav.classList.remove("is-open", "open");
          navToggle.focus();
        }
      }
    });
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", initNavigation, { once: true });
  } else {
    initNavigation();
  }
})();
