(function () {
  "use strict";

  var STEPS = ["Intent", "Resolution", "Authorization", "Plan", "Execution"];

  var CASES = {
    allow: {
      ask: "Show me overdue purchase orders from our top supplier in Texas.",
      who: "AI agent acting for a purchasing manager, tenant-7",
      stages: [
        {
          title: "The agent sends intent, not SQL",
          text: "The caller describes what it wants in structured form. It does not name tables, joins or security context.",
          verdict: "intent accepted",
          code: '{\n  "operation": "query",\n  "resource": "purchaseOrders",\n  "filter": { "supplier": "top", "state": "TX", "status": "overdue" },\n  "select": ["number", "supplier.name", "daysLate"]\n}'
        },
        {
          title: "Meaning comes from the application",
          text: "\u201Ctop supplier\u201D and \u201Coverdue\u201D are resolved against the configured semantic model. Retrieval may suggest candidates; it never grants access.",
          verdict: "resolved to PurchaseOrder \u2192 Supplier",
          code: '{\n  "entities": ["PurchaseOrder", "Supplier"],\n  "topSupplier": "by configured measure",\n  "overdue": "dueDate < today AND status != received",\n  "ambiguity": "none"\n}'
        },
        {
          title: "The host decides what is allowed",
          text: "Identity comes from the application. The request is authorized against that identity before any plan exists.",
          verdict: "allowed \u00B7 tenant-7 slice",
          code: '{\n  "actor": "purchasing-manager",\n  "tenant": "tenant-7",\n  "decision": "allow",\n  "constraint": "tenant = tenant-7"\n}'
        },
        {
          title: "A provider-independent plan",
          text: "The authorized meaning becomes a semantic plan bound to the authorization decision, then ExecutionIR. Nothing physical has been chosen yet.",
          verdict: "plan bound to authorization",
          code: '{\n  "plan": ["scan PurchaseOrder", "join Supplier", "filter", "project"],\n  "authorizationBinding": "attached",\n  "rewrites": "security-preserving only"\n}'
        },
        {
          title: "A provider executes the result",
          text: "Only now does a provider (SQL, EF Core, JPA) compile and run the work. The result returns with evidence of what was decided.",
          verdict: "result + evidence",
          code: '{\n  "provider": "PostgreSQL",\n  "rows": 3,\n  "evidence": ["resolution", "authorization", "plan"]\n}'
        }
      ]
    },
    deny: {
      ask: "Transfer $5,000 from account 9 to my account.",
      who: "AI agent acting for a customer, tenant-7",
      stages: [
        {
          title: "The agent sends intent, not SQL",
          text: "A mutation is expressed as intent too. The agent has no special channel to the database.",
          verdict: "intent accepted",
          code: '{\n  "operation": "mutation",\n  "capability": "TransferFunds",\n  "arguments": { "from": "acct-9", "to": "acct-2", "amount": 5000 }\n}'
        },
        {
          title: "Meaning comes from the application",
          text: "The capability and accounts resolve against the semantic model. Resolving a target does not mean the caller may touch it.",
          verdict: "resolved to Account \u2192 Account",
          code: '{\n  "capability": "TransferFunds",\n  "from": "Account acct-9",\n  "to": "Account acct-2",\n  "ambiguity": "none"\n}'
        },
        {
          title: "The host refuses",
          text: "acct-9 is not owned by this caller. Authorization stops the request at the boundary, so no plan is built and no provider is called.",
          verdict: "denied \u00B7 ownership",
          gate: true,
          code: '{\n  "actor": "customer",\n  "decision": "deny",\n  "reason": "caller does not own acct-9",\n  "mutated": false\n}'
        },
        {
          title: "No plan exists",
          text: "A denied request never becomes ExecutionIR. There is nothing to rewrite, compile or retry.",
          verdict: "not produced",
          skipped: true,
          code: "// no plan: the request was refused before planning"
        },
        {
          title: "The database is untouched",
          text: "The refusal is returned with evidence. State is unchanged because execution was never reached.",
          verdict: "not reached",
          skipped: true,
          code: "// no provider call\n// no state change"
        }
      ]
    }
  };

  var root = document.getElementById("fx-demo");
  if (!root) return;

  var tabs = root.querySelectorAll(".fx-case");
  var ask = root.querySelector(".fx-ask-text");
  var who = root.querySelector(".fx-who");
  var rail = root.querySelector(".fx-rail");
  var title = root.querySelector(".fx-detail-text h3");
  var text = root.querySelector(".fx-detail-text .fx-body");
  var verdict = root.querySelector(".fx-verdict");
  var code = root.querySelector(".fx-detail pre code");
  var replay = root.querySelector(".fx-replay");

  var current = "allow";
  var step = 0;
  var timer = null;
  var reduce = window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches;

  function stopAt(c) {
    for (var i = 0; i < CASES[c].stages.length; i++) {
      if (CASES[c].stages[i].gate) return i;
    }
    return -1;
  }

  function render() {
    var c = CASES[current];
    var gate = stopAt(current);
    ask.textContent = "\u201C" + c.ask + "\u201D";
    who.textContent = c.who;
    var items = rail.querySelectorAll("li");
    for (var i = 0; i < items.length; i++) {
      var li = items[i];
      li.className = "";
      var btn = li.querySelector("button");
      btn.setAttribute("aria-current", i === step ? "step" : "false");
      if (c.stages[i].skipped) li.className = "is-skipped";
      else if (i < step) li.className = "is-done";
      if (i === step) li.className = c.stages[i].gate ? "is-stopped" : "is-current";
      if (gate >= 0 && i === gate && i < step) li.className = "is-stopped";
    }
    var s = c.stages[step];
    title.textContent = s.title;
    text.textContent = s.text;
    verdict.textContent = s.verdict;
    verdict.className = "fx-verdict" + (s.gate ? " is-gate" : "");
    code.textContent = s.code;
  }

  function stopTimer() {
    if (timer) {
      clearInterval(timer);
      timer = null;
    }
  }

  function play() {
    stopTimer();
    step = 0;
    render();
    if (reduce) return;
    timer = setInterval(function () {
      if (step >= STEPS.length - 1) return stopTimer();
      step++;
      render();
    }, 1600);
  }

  Array.prototype.forEach.call(tabs, function (t) {
    t.addEventListener("click", function () {
      current = t.getAttribute("data-case");
      Array.prototype.forEach.call(tabs, function (o) {
        o.setAttribute("aria-selected", o === t ? "true" : "false");
      });
      play();
    });
  });

  rail.addEventListener("click", function (e) {
    var b = e.target.closest("button[data-step]");
    if (!b) return;
    stopTimer();
    step = parseInt(b.getAttribute("data-step"), 10);
    render();
  });

  replay.addEventListener("click", play);

  var started = false;
  function start() {
    if (started) return;
    started = true;
    play();
  }
  if ("IntersectionObserver" in window) {
    var io = new IntersectionObserver(function (entries) {
      if (entries[0].isIntersecting) {
        start();
        io.disconnect();
      }
    }, { threshold: 0.4 });
    io.observe(root);
    render();
  } else {
    start();
  }
})();
