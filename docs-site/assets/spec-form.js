(function () {
  "use strict";
  function init() {
    var root = document.getElementById("foundgine-spec-form");
    if (!root) return;
    root.innerHTML = `
      <style>
        #foundgine-spec-form{color:inherit}#foundgine-spec-form .fsf-card{border:1px solid var(--border,#d7dce2);border-radius:14px;padding:20px;margin:16px 0;background:var(--surface,#fff)}
        #foundgine-spec-form .fsf-grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:14px}
        #foundgine-spec-form label{display:block;font-weight:600;margin:0 0 6px}
        #foundgine-spec-form input,#foundgine-spec-form select,#foundgine-spec-form textarea{box-sizing:border-box;width:100%;padding:10px 12px;border:1px solid var(--border,#cbd2d9);border-radius:8px;background:var(--background,#fff);color:inherit;font:inherit}
        #foundgine-spec-form textarea{min-height:92px;resize:vertical}#foundgine-spec-form .fsf-field{margin-bottom:14px}
        #foundgine-spec-form .fsf-actions{display:flex;gap:10px;flex-wrap:wrap;margin-top:14px}
        #foundgine-spec-form button{border:0;border-radius:8px;padding:11px 16px;font:inherit;font-weight:650;cursor:pointer;background:var(--accent,#2457a6);color:white}
        #foundgine-spec-form button.secondary{background:var(--surface-2,#e9edf2);color:inherit;border:1px solid var(--border,#cbd2d9)}
        #foundgine-spec-form .fsf-output{min-height:260px;font-family:ui-monospace,SFMono-Regular,Consolas,monospace;font-size:.9rem}
        #foundgine-spec-form .fsf-help{opacity:.78;font-size:.92rem}#foundgine-spec-form .fsf-status{min-height:1.5em;margin-top:8px;font-weight:600}
        @media(max-width:650px){#foundgine-spec-form .fsf-grid{grid-template-columns:1fr}}
      </style>
      <div class="fsf-card">
        <h2>Describe your application</h2>
        <p class="fsf-help">Fill in what you know. Generate a structured implementation brief you can give to an AI coding assistant together with the Foundgine repository. You can leave optional fields blank.</p>
        <form id="fsf-form">
          <div class="fsf-grid">
            <div class="fsf-field"><label for="fsf-name">Application name</label><input id="fsf-name" name="name" placeholder="e.g. Supply Chain ERP" required></div>
            <div class="fsf-field"><label for="fsf-stack">Technology stack</label><select id="fsf-stack" name="stack"><option>.NET 10</option><option>Java 27</option><option>Both .NET and Java</option><option>Other / decide for me</option></select></div>
            <div class="fsf-field"><label for="fsf-domain">Business domain</label><input id="fsf-domain" name="domain" placeholder="e.g. procurement, finance, CRM"></div>
            <div class="fsf-field"><label for="fsf-database">Database / persistence</label><input id="fsf-database" name="database" placeholder="e.g. PostgreSQL with EF Core"></div>
            <div class="fsf-field"><label for="fsf-entities">Main entities</label><textarea id="fsf-entities" name="entities" placeholder="Customer, Order, Product, Supplier..."></textarea></div>
            <div class="fsf-field"><label for="fsf-relationships">Relationships and important joins</label><textarea id="fsf-relationships" name="relationships" placeholder="Customer has many Orders; Order references Products..."></textarea></div>
            <div class="fsf-field"><label for="fsf-roles">Users, roles and access rules</label><textarea id="fsf-roles" name="roles" placeholder="Admin, sales manager, tenant-scoped customer..."></textarea></div>
            <div class="fsf-field"><label for="fsf-intents">Operations / business intents</label><textarea id="fsf-intents" name="intents" placeholder="Search orders, approve refunds, update shipment status..."></textarea></div>
            <div class="fsf-field"><label for="fsf-protocols">Protocols and interfaces</label><input id="fsf-protocols" name="protocols" placeholder="REST, GraphQL, MCP"></div>
            <div class="fsf-field"><label for="fsf-integrations">Integrations</label><input id="fsf-integrations" name="integrations" placeholder="Keycloak, Temporal, Redis, OpenTelemetry..."></div>
          </div>
          <div class="fsf-field"><label for="fsf-notes">Additional requirements</label><textarea id="fsf-notes" name="notes" placeholder="Performance, tenancy, audit, workflows, deployment, constraints..."></textarea></div>
          <div class="fsf-actions"><button type="submit">Generate implementation brief</button><button class="secondary" type="reset">Clear form</button></div>
        </form>
      </div>
      <div class="fsf-card" id="fsf-result-card" hidden>
        <h2>Your Foundgine implementation brief</h2>
        <p class="fsf-help">Review the generated brief, then copy it into your AI coding assistant. Include the Foundgine repository and ask it to implement and test the application.</p>
        <textarea class="fsf-output" id="fsf-output" aria-label="Generated implementation brief" spellcheck="false"></textarea>
        <div class="fsf-actions"><button type="button" id="fsf-copy">Copy brief</button><button type="button" class="secondary" id="fsf-download">Download .md</button></div>
        <div class="fsf-status" id="fsf-status" role="status" aria-live="polite"></div>
      </div>`;
    var form = root.querySelector("#fsf-form"), output = root.querySelector("#fsf-output"), result = root.querySelector("#fsf-result-card"), status = root.querySelector("#fsf-status");
    function value(name) { return (new FormData(form).get(name) || "").toString().trim() || "Not specified"; }
    form.addEventListener("submit", function (event) {
      event.preventDefault();
      if (!form.reportValidity()) return;
      var prompt = `# Foundgine application implementation brief\n\nBuild **${value("name")}** using Foundgine. Treat the repository's current source and documentation as authoritative; inspect the actual APIs before implementing. Do not invent APIs.\n\n## Application context\n- **Stack:** ${value("stack")}\n- **Business domain:** ${value("domain")}\n- **Persistence:** ${value("database")}\n\n## Domain model\n### Entities\n${value("entities")}\n\n### Relationships and joins\n${value("relationships")}\n\n## Identity and authorization\n${value("roles")}\n\n## Business intents and operations\n${value("intents")}\n\n## Protocols and interfaces\n${value("protocols")}\n\n## Integrations\n${value("integrations")}\n\n## Additional requirements\n${value("notes")}\n\n## Implementation expectations\n1. Start with a concise architecture and implementation plan based on the repository's real APIs.\n2. Configure semantic entities, fields, aliases, dimensions, relationships and capabilities using Foundgine's fluent configuration where appropriate.\n3. Keep authentication and authorization decisions in the host application. Semantic discovery must never grant execution authority.\n4. Reuse the selected persistence technology; do not create a parallel data-access layer.\n5. Add only the requested protocol adapters and integrations.\n6. Implement validation, error handling, observability and audit evidence appropriate to the requirements.\n7. Add meaningful automated tests, including denied and cross-tenant operations where applicable.\n8. Build and run the tests; report exact commands, results, assumptions and any unsupported requirements.\n9. Prefer a simple direct API integration when a full semantic execution layer is not needed; explain the trade-offs.\n`;
      output.value = prompt; result.hidden = false; status.textContent = "Brief generated. Review it before copying or downloading."; result.scrollIntoView({ behavior: "smooth", block: "start" });
    });
    form.addEventListener("reset", function () { result.hidden = true; status.textContent = ""; });
    root.querySelector("#fsf-copy").addEventListener("click", function () {
      var text = output.value;
      if (navigator.clipboard && window.isSecureContext) navigator.clipboard.writeText(text).then(function(){status.textContent="Copied to clipboard.";}).catch(function(){output.select();status.textContent="Select and copy the brief manually.";});
      else { output.select(); try { document.execCommand("copy"); status.textContent="Copied to clipboard."; } catch (_) { status.textContent="Select and copy the brief manually."; } }
    });
    root.querySelector("#fsf-download").addEventListener("click", function () {
      var blob = new Blob([output.value], {type:"text/markdown;charset=utf-8"}), url = URL.createObjectURL(blob), a = document.createElement("a");
      a.href=url; a.download="foundgine-application-brief.md"; document.body.appendChild(a); a.click(); a.remove(); URL.revokeObjectURL(url); status.textContent="Markdown brief downloaded.";
    });
  }
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", init); else init();
})();
