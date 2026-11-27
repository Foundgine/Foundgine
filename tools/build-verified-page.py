#!/usr/bin/env python3
"""Generate the claims audit from one list of claims.

Writes:
  docs/CLAIMS-AUDIT.md            (repository copy, links are relative to the repo)
  docs-site/verified/index.html   (website page, links point at GitHub)

Run `python tools/build-verified-page.py` and then `python tools/apply-site-template.py`
so the new page also gets the shared footer and "same rules" band.

Every claim lists the code that enforces it and the tests that exercise it. When code
moves, update the paths here; `--check` fails if a listed path no longer exists.
"""
from __future__ import annotations

import argparse
import html
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
GH = "https://github.com/Foundgine/Foundgine/blob/main/"
VERSION = "3.4.2"
CHECKED = "27 November 2026"

CS = "src/csharp/"
JV = "src/java/"

# Each claim: id, bold statement as shown on the website, plain meaning, code, tests, scope.
CLAIMS = [
    {
        "id": "same-checks",
        "claim": "Every request goes through the same checks, whichever door it uses.",
        "meaning": "App code, HTTP, GraphQL, MCP, agent tools and workflow steps all end in the same engine, which limits the request, verifies the caller's warrant, authorizes it, plans it, re-checks authorization immediately before dispatch and only then calls the data provider. Mutations use the same policy through the mutation engine.",
        "code": [
            (CS + "Foundgine.Runtime/Execution/FoundgineEngine.cs", "ExecuteAsync: resource limits, warrant, authorization, planning, revalidation, provider"),
            (CS + "Foundgine.Runtime/Execution/FoundgineMutationEngine.cs", "AuthorizeAndPlan before ExecutePlanAsync"),
            (CS + "Foundgine.Providers/Tools/MCP/FoundgineMcpTools.cs", "MCP reads call IFoundgine.ExecuteAsync"),
            (CS + "Foundgine.Providers/Tools/MCP/FoundgineMcpMutationTools.cs", "MCP writes call IFoundgineMutations"),
            (CS + "Foundgine.Extensions.GraphQL/HotChocolate/Execution/FoundgineGraphQLQueryExecutor.cs", "GraphQL reads call IFoundgineExecutor"),
            (CS + "Foundgine.Extensions.GraphQL/HotChocolate/Execution/FoundgineHotChocolateMutationExecutor.cs", "GraphQL writes call IFoundgineMutations"),
            (CS + "Foundgine.Providers/Tools/Http/FoundgineIntentEndpoints.cs", "HTTP intent endpoints, 401 when unauthenticated"),
            (CS + "Foundgine.Providers.AgentFramework/Tools/FoundgineAgentTools.cs", "agent tools use IFoundgine and IFoundgineMutations"),
            (CS + "Foundgine.Extensions.Temporal/Activities/FoundgineOperationActivities.cs", "workflow steps get a single-use warrant per Foundgine call"),
            (JV + "foundgine-runtime/src/main/java/com/foundgine/runtime/FoundgineEngine.java", "Java engine with the same pipeline"),
        ],
        "tests": [
            (CS + "tests/Foundgine.Security.Tests/Penetration", "deterministic penetration suite"),
            (CS + "security/pentest/ATTACK-MATRIX.md", "attack matrix SEC-01 to SEC-36"),
            (CS + "tests/Foundgine.GraphQL.HotChocolate.Tests", "GraphQL adapter tests"),
            (CS + "tests/Foundgine.MCP.Tests", "MCP adapter tests"),
        ],
        "scope": "The checks apply to every request that reaches Foundgine. Code in your application that talks to your database directly, without calling Foundgine, is not covered. A workflow step is checked when it calls Foundgine, and you decide what the rest of the step does. Two live-environment items (network exposure and known-CVE scans, SEC-23 and SEC-24) are manual and need your own deployment.",
    },
    {
        "id": "ai-not-allowed",
        "claim": "An AI asking is never the same as an AI being allowed.",
        "meaning": "The caller's identity, tenant, audience and warrant come from your host application, not from the AI. If the host supplies none, the request fails. The MCP tools tell the model never to supply identity, and the adapter replaces any security data in the intent with the host's.",
        "code": [
            (CS + "Foundgine.Core/Semantic/Security/Execution/SecurityExecutionContextProviderExtensions.cs", "throws when the host supplies no security context"),
            (CS + "Foundgine.Providers/Tools/MCP/FoundgineMcpTools.cs", "intent is rebuilt with the host's Security"),
            (CS + "Foundgine.Extensions.GraphQL/HotChocolate/Execution/FoundgineGraphQLQueryExecutor.cs", "host context replaces whatever the request carried"),
            (CS + "Foundgine.Runtime/Hosting/IFoundgineIntentPrincipalProvider.cs", "your application owns the principal"),
            (CS + "Foundgine.Providers/Tools/Http/HttpFoundgineIntentPrincipalProvider.cs", "HTTP principal comes from your sign-in"),
        ],
        "tests": [
            (CS + "tests/Foundgine.Security.Tests/AdversarialAgentBoundaryTests.cs", "adversarial agent boundary"),
            (CS + "security/pentest/ATTACK-MATRIX.md", "SEC-01 capability escalation, SEC-17 MCP authority injection"),
        ],
        "scope": "Foundgine decides with the identity your application provides. If your application provides the wrong identity, Foundgine cannot know.",
    },
    {
        "id": "one-definition",
        "claim": "One definition, the same result everywhere.",
        "meaning": "Every adapter turns its input into the same semantic request, which is checked against the one semantic model. The .NET and Java implementations are held to the same shared test vectors.",
        "code": [
            (CS + "Foundgine.Runtime/Api/IFoundgine.cs", "one executor interface for every adapter"),
            (CS + "Foundgine.Core/Semantic/Query/SemanticValueValidator.cs", "value validation"),
            (CS + "Foundgine.Core/Semantic/Query/SemanticFilterValidator.cs", "filter validation"),
            (CS + "Foundgine.Core/Semantic/Query/SemanticQueryOptionsValidator.cs", "paging and ordering validation"),
        ],
        "tests": [
            ("src/conformance/semantic-query-vectors.json", "shared vectors"),
            (CS + "tests/Foundgine.Semantics.Tests/SemanticQueryConformanceTests.cs", ".NET runs the vectors"),
            (JV + "foundgine-core/src/test/java/com/foundgine/core/semantic/ir/SemanticQueryConformanceTest.java", "Java runs the same vectors"),
            ("src/conformance/tenant-first-gate-scenarios.json", "shared tenant-first gate scenarios"),
        ],
        "scope": "Holds for the .NET 10 and Java implementations in this repository. The TypeScript, Python and WebAssembly SDKs are thin clients and contain no rules of their own.",
    },
    {
        "id": "keep-your-stack",
        "claim": "Keep what you already have.",
        "meaning": "Foundgine reaches a database or tool only through a provider at the last step. EF Core and JPA have providers, and your application keeps supplying who the caller is.",
        "code": [
            (CS + "Foundgine.Core/Execution/IExecutionProvider.cs", "the provider boundary"),
            (CS + "Foundgine.Providers.EntityFrameworkCore/EfCoreProvider.cs", "EF Core provider"),
            (JV + "foundgine-providers/src/main/java/com/foundgine/providers/storage/orm/jpa/JpaOrmProvider.java", "JPA provider"),
            (CS + "Foundgine.Providers/Storage/Sql/SqlExecutionProvider.cs", "plain SQL provider"),
            (CS + "Foundgine.Extensions.Keycloak", "Keycloak sign-in integration"),
            (CS + "Foundgine.Extensions.Temporal", "Temporal workflow integration"),
        ],
        "tests": [
            (CS + "tests/Foundgine.Orm.Tests", "ORM and EF Core provider tests"),
            (CS + "tests/Foundgine.Sql.Tests", "SQL provider tests"),
            (CS + "tests/Foundgine.E2E.Tests", "end-to-end tests"),
        ],
        "scope": "Keycloak is the sign-in integration that ships. Other sign-in systems connect by supplying the caller's security context. Temporal is the workflow integration that ships.",
    },
    {
        "id": "plug-in-clients",
        "claim": "Plug in your clients. The rules do not change.",
        "meaning": "MCP, GraphQL, HTTP and Temporal adapters ship for .NET and Java. TypeScript, Python and WebAssembly SDKs talk to a running Foundgine server and do not contain the runtime, so they cannot apply different rules.",
        "code": [
            ("src/typescript/README.md", "TypeScript client: MCP and workflow HTTP"),
            ("src/python/README.md", "Python client: MCP and workflow HTTP"),
            ("src/wasm/README.md", "WebAssembly client core"),
            (JV + "foundgine-extensions/src/main/java/com/foundgine/extensions/graphql/FoundgineGraphQLQueryExecutor.java", "Java GraphQL adapter, host security required"),
            (JV + "foundgine-providers/src/main/java/com/foundgine/providers/tools/mcp/FoundgineMcpTools.java", "Java MCP tools, host security required"),
        ],
        "tests": [
            ("src/conformance/mcp-session-scenarios.json", "shared MCP session scenarios"),
            (CS + "tests/Foundgine.MCP.Tests/McpSessionConformanceTests.cs", ".NET runs them"),
            (JV + "foundgine-providers/src/test/java/com/foundgine/providers/tools/mcp/McpSessionConformanceTest.java", "Java runs them"),
        ],
        "scope": "These are the adapters and SDKs that exist today. A technology with none of them needs a small adapter, which is the same pattern the existing ones follow.",
    },
    {
        "id": "measure-it",
        "claim": "Is it worth it? Measure it.",
        "meaning": "The site promises no percentage of savings. It gives you a way to measure work done more than once, effort to change, security testing and running cost on one small project.",
        "code": [
            ("docs/wiki/why-foundgine.md", "business and architecture case"),
            ("docs/PRODUCT-POSITIONING-AND-COMMERCIAL-STRATEGY.md", "commercial strategy and measurement model"),
        ],
        "tests": [],
        "scope": "Benchmark pages report what was measured in those runs. They are not a forecast for your system.",
    },
    {
        "id": "versions",
        "claim": f"Foundgine {VERSION}, .NET 10 and Java 27.",
        "meaning": f"Version {VERSION} is set in the .NET build, the Java build, and the TypeScript, Python and WebAssembly packages. .NET projects target net10.0 and the Java build compiles for release 27.",
        "code": [
            ("Directory.Build.props", "VersionPrefix and net10.0"),
            ("src/java/pom.xml", "release 27"),
            ("src/typescript/package.json", "version"),
            ("src/python/pyproject.toml", "version"),
            ("src/wasm/package.json", "version"),
            ("CHANGELOG.md", f"{VERSION} entry"),
        ],
        "tests": [],
        "scope": "Package pages tell you to confirm a version is published in NuGet or Maven Central before you pin it.",
    },
]

CAVEAT = ("How this was checked: by reading the code paths listed here in the "
          f"{VERSION} source on {CHECKED}. The listed tests are part of the repository. "
          "Run them in your own pipeline for your build before relying on a claim in production.")


def check_paths() -> list[str]:
    missing = []
    for c in CLAIMS:
        for group in ("code", "tests"):
            for path, _ in c[group]:
                if not (ROOT / path).exists():
                    missing.append(path)
    return missing


def md() -> str:
    out = [f"# Claims audit\n\nWhat the Foundgine website says, where it is enforced and how it is tested. Version {VERSION}, checked {CHECKED}.\n\n> {CAVEAT}\n"]
    for c in CLAIMS:
        out.append(f"## {c['claim']}\n\n{c['meaning']}\n")
        out.append("**Enforced in**\n")
        out += [f"- [`{p}`](../{p}): {d}" for p, d in c["code"]]
        if c["tests"]:
            out.append("\n**Tests and evidence**\n")
            out += [f"- [`{p}`](../{p}): {d}" for p, d in c["tests"]]
        out.append(f"\n**Scope**\n\n{c['scope']}\n")
    return "\n".join(out) + "\n"


def li(items):
    return "".join(
        f'<li><a href="{GH}{html.escape(p)}"><code>{html.escape(p)}</code></a>: {html.escape(d)}</li>'
        for p, d in items
    )


def page() -> str:
    index = (ROOT / "docs-site/index.html").read_text(encoding="utf-8")
    header = re.search(r'<header class="site-header">.*?</header>', index, re.S).group(0)
    header = header.replace(' aria-current="page"', "")
    sections = []
    for c in CLAIMS:
        tests = f"<h3>Tests and evidence</h3><ul>{li(c['tests'])}</ul>" if c["tests"] else ""
        sections.append(
            f'<section><h2 id="{c["id"]}">{html.escape(c["claim"])}</h2>'
            f'<p>{html.escape(c["meaning"])}</p>'
            f'<h3>Enforced in</h3><ul>{li(c["code"])}</ul>{tests}'
            f'<h3>Scope</h3><p>{html.escape(c["scope"])}</p></section>'
        )
    desc = "Each claim on the Foundgine website, the code that enforces it, the tests that exercise it and where it stops applying."
    return f"""<!doctype html>

<html lang="en">
  <head>
    <meta charset="utf-8" />
    <meta content="width=device-width,initial-scale=1" name="viewport" />
    <title>Verified claims — Foundgine</title>
    <meta content="{html.escape(desc, quote=True)}" name="description" />
    <link href="https://foundgine.io/verified/" rel="canonical" />
    <meta content="Verified claims — Foundgine" property="og:title" />
    <meta content="{html.escape(desc, quote=True)}" property="og:description" />
    <meta content="website" property="og:type" />
    <meta content="https://foundgine.io/verified/" property="og:url" />
    <meta content="Foundgine" property="og:site_name" />
    <meta content="https://foundgine.io/Foundgine.webp" property="og:image" />
    <meta content="summary_large_image" name="twitter:card" />
    <meta content="Verified claims — Foundgine" name="twitter:title" />
    <meta content="{html.escape(desc, quote=True)}" name="twitter:description" />
    <meta content="https://foundgine.io/Foundgine.webp" name="twitter:image" />
    <link href="https://fonts.googleapis.com" rel="preconnect" />
    <link crossorigin="" href="https://fonts.gstatic.com" rel="preconnect" />
    <link href="https://fonts.googleapis.com/css2?family=Space+Grotesk:wght@500;600;700&amp;family=Source+Serif+4:opsz,wght@8..60,400;8..60,600&amp;family=IBM+Plex+Mono:wght@500;600&amp;display=swap" rel="stylesheet" />
    <link href="/assets/favicon.svg" rel="icon" type="image/svg+xml" />
    <link href="/assets/style.css" rel="stylesheet" />
    <link href="/assets/foundgine-theme.css" rel="stylesheet" />
    <meta name="robots" content="index, follow, max-snippet:-1, max-image-preview:large, max-video-preview:-1" />
    <link href="/assets/site-chrome.css" rel="stylesheet" />
  </head>
  <body>
    <a class="skip-link" href="#content">Skip to content</a>
    {header}
    <div class="page">
      <main id="content" class="article">
        <h1>Verified claims</h1>
        <p class="subtitle">Foundgine applies the same rules to every request. This page lists each claim we make, the code that enforces it, the tests that exercise it and where it stops applying.</p>
        <p>{html.escape(CAVEAT)}</p>
{chr(10).join(sections)}
      </main>
    </div>
    <script src="/assets/site-nav.js" defer></script>
  </body>
</html>
"""


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true")
    args = ap.parse_args()
    missing = check_paths()
    if missing:
        print("missing evidence paths:", *missing, sep="\n  ")
        return 1
    if args.check:
        print("all evidence paths exist")
        return 0
    (ROOT / "docs/CLAIMS-AUDIT.md").write_text(md(), encoding="utf-8")
    out = ROOT / "docs-site/verified/index.html"
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(page(), encoding="utf-8")
    print("wrote docs/CLAIMS-AUDIT.md and docs-site/verified/index.html")
    return 0


if __name__ == "__main__":
    sys.exit(main())
