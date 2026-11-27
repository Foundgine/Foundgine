#!/usr/bin/env python3
"""Apply the shared Foundgine page template to every page in docs-site/.

The site is static HTML with the header and footer copied into each page, so this
script is the single place that defines the shared parts. It is idempotent: run it
again after editing a template string below and every page is updated.

    python tools/apply-site-template.py            # rewrite pages in place
    python tools/apply-site-template.py --check    # exit 1 if any page is out of date

What it manages on every page:
  * <title> (and the matching og:title, twitter:title and JSON-LD headline)
  * <meta name="theme-color"> and a meta description where one is missing
  * the "same rules" band shown above the footer (not on the home page, which tells
    the same story in its hero)
  * the site footer
On the home page it also owns the hero and the "story" section.
"""
from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

SITE = Path(__file__).resolve().parent.parent / "docs-site"
VERSION = "3.4.2"
TAGLINE = "The same rules. Applied every time."
SUBTAG = "One shared checkpoint for your apps, APIs, databases and AI assistants."

# ---------------------------------------------------------------- titles
# One pattern for every page: "<what this page is> — Foundgine".
TITLES = {
    "agent-benchmark/index.html": "Agent benchmark: five experiments, one checkpoint — Foundgine",
    "agent-benchmark/run-1/index.html": "Run 1: agent end-to-end baseline — Foundgine",
    "agent-benchmark/run-2/index.html": "Run 2: agent scalability by customer tier — Foundgine",
    "agent-benchmark/run-3/index.html": "Run 3: agent cost and efficiency — Foundgine",
    "agent-benchmark/run-4/index.html": "Run 4: graph execution and scalability — Foundgine",
    "agent-benchmark/run-5/index.html": "Run 5: high-assurance Postgres and TransferFunds — Foundgine",
    "agent-benchmark/run-5b/index.html": "Run 5b: individual calls vs semantic batch — Foundgine",
    "agent-benchmark/supply-chain/index.html": "Supply chain end to end: agent, MCP, Foundgine, PostgreSQL — Foundgine",
    "ai-agents/index.html": "AI agents and MCP — Foundgine",
    "architecture/index.html": "Architecture — Foundgine",
    "case-studies/supply-chain/index.html": "Supply chain case study — Foundgine",
    "getting-started/index.html": "Getting started — Foundgine",
    "how-it-works/index.html": "How a request is checked and carried out — Foundgine",
    "packages/index.html": "Packages — Foundgine",
    "performance/index.html": "Performance and benchmark evidence — Foundgine",
    "samples/index.html": "Samples — Foundgine",
    "samples/pentest/index.html": "Security pen test for MCP and GraphQL — Foundgine",
    "samples/semantic/authorization.html": "Supply chain authorization cases — Foundgine",
    "samples/semantic/index.html": "Supply chain semantic sample — Foundgine",
    "security/index.html": "Security and authorization — Foundgine",
    "spec-form/index.html": "App generator — Foundgine",
    "walkthrough/index.html": "Walkthrough: from a plain request to a checked result — Foundgine",
    "what-is-foundgine.html": "What is Foundgine? One shared checkpoint for your rules",
    "index.html": f"Foundgine — {TAGLINE}",
    "404.html": "Page not found — Foundgine",
    "verified/index.html": "Verified claims: what Foundgine promises and where it is enforced — Foundgine",
}
HOME_DESC = (
    "Foundgine is one shared checkpoint for your apps, APIs, GraphQL, workflows and AI "
    "assistants: the same meaning, validation and permissions on every request. "
    "A toolkit for .NET and Java."
)
FALLBACK_DESC = "Foundgine: one shared checkpoint that applies the same rules to every request, from any app, API or AI assistant."

# ---------------------------------------------------------------- shared parts
BRAND_MARK = (
    '<svg aria-hidden="true" class="brand-mark" width="20" height="20" viewBox="0 0 32 32">'
    '<g fill="none" stroke="currentColor" stroke-width="4" stroke-linecap="round">'
    '<path d="M6 6v20M6 6h8M6 26h8"/></g><circle cx="24" cy="16" r="4.5" fill="currentColor"/></svg>'
)

RULES_BAND = f"""<!-- fx:rules -->
    <aside class="fx-rules" aria-labelledby="fx-rules-title">
      <div class="fx-rules-inner">
        <div class="fx-rules-copy">
          <h2 id="fx-rules-title">{TAGLINE}</h2>
          <p>You describe what things mean, what is valid and who may do what once. Foundgine checks every request against it, whether it comes from an app, an API, GraphQL, a workflow or an AI assistant.</p>
          <p class="fx-rules-actions"><a class="fx-rules-primary" href="/getting-started/">Get started</a><a href="/how-it-works/">See how a request is checked</a></p>
        </div>
        <div class="fx-rules-path">
          <div class="fx-rules-col">
            <h3>Clients ask</h3>
            <ul><li>Your apps</li><li>APIs and GraphQL</li><li>Workflows</li><li>AI assistants (MCP)</li></ul>
          </div>
          <div class="fx-rules-gate">
            <h3>One checkpoint</h3>
            <ul><li>Same meaning</li><li>Same validation</li><li>Same permissions</li></ul>
          </div>
          <div class="fx-rules-col">
            <h3>You keep your stack</h3>
            <ul><li>Your databases</li><li>EF Core or JPA</li><li>Your sign-in system</li><li>Your workflow tools</li></ul>
          </div>
        </div>
      </div>
    </aside>
    <!-- /fx:rules -->"""

FOOTER = f"""<!-- fx:footer -->
    <footer class="site-footer">
      <div class="footer-top">
        <div class="footer-brand">
          <a class="brand" href="/">{BRAND_MARK}Foundgine</a>
          <p class="footer-tag">{TAGLINE}</p>
          <p>{SUBTAG}</p>
        </div>
        <nav class="footer-grid" aria-label="Footer">
          <div><h2>Learn</h2><a href="/what-is-foundgine">What is Foundgine</a><a href="/getting-started/">Getting started</a><a href="/how-it-works/">How it works</a><a href="/walkthrough/">Walkthrough</a></div>
          <div><h2>Build</h2><a href="/ai-agents/">AI agents</a><a href="/security/">Security</a><a href="/samples/">Samples</a><a href="/spec-form/">App generator</a></div>
          <div><h2>Reference</h2><a href="/foundgine-overview">Technical overview</a><a href="/architecture/">Architecture</a><a href="/packages/">Packages</a><a href="/llms.txt">llms.txt</a></div>
          <div><h2>Evidence</h2><a href="/agent-benchmark/">Benchmarks</a><a href="/performance/">Performance</a><a href="/verified/">Verified claims</a><a href="https://github.com/Foundgine/Foundgine">GitHub ↗</a></div>
        </nav>
      </div>
      <div class="footer-meta">Foundgine {VERSION} · .NET 10 &amp; Java 27 · Apache-2.0</div>
    </footer>
    <!-- /fx:footer -->"""

# ---------------------------------------------------------------- home story
HOME_HERO = f"""<section class="fx-hero fx-hero-simple" aria-labelledby="hero-title">
        <div class="fx-wrap">
          <div class="fx-hero-head">
            <p class="fx-version">v{VERSION} &middot; .NET 10 &amp; Java 27</p>
            <h1 id="hero-title">{TAGLINE}</h1>
            <p class="fx-lede">Whether a request comes from an app, an API, GraphQL, a workflow or an AI assistant, Foundgine applies the same meaning, validation and permissions before anything happens. It is a toolkit for .NET and Java developers.</p>
            <div class="fx-cta">
              <a class="fx-btn primary" href="/getting-started/">Get started</a>
              <a class="fx-btn ghost" href="/foundgine-overview">Read the technical overview</a>
              <a class="fx-btn ghost" href="/walkthrough/">See the walkthrough</a>
              <a class="fx-btn ghost" href="https://github.com/Foundgine/Foundgine" rel="noopener">View on GitHub</a>
            </div>
          </div>
        </div>
      </section>

      """

STORY = [
    ("Say what things mean once.",
     "Describe your business things (customers, orders), their details, how they connect and what can be done with them. That single description is the source for everything else."),
    ("One definition, the same result everywhere.",
     "Your apps, APIs, GraphQL, workflows and AI tools all read that one description. None of them keeps its own copy of what is valid or who is allowed to do what, so they cannot drift apart."),
    ("Every request goes through the same checks, whichever door it uses.",
     "App code, HTTP, GraphQL, MCP, agent tools and workflow steps all call the same engine. There is no shortcut for internal tools or AI agents, and nothing runs until the request has been checked."),
    ("An AI asking is never the same as an AI being allowed.",
     "Your application still decides who someone is and what they may do. AI assistants (through MCP) face exactly the same rules as every other client."),
    ("Keep what you already have.",
     "Keep your apps, databases, data tools (EF Core or JPA), sign-in system and workflow tools. Foundgine connects to a specific database or tool only at the final step, so you can start small and add more over time."),
    ("Plug in your clients. The rules do not change.",
     "MCP, GraphQL, HTTP, Temporal workflows and SDKs for TypeScript, Python and WebAssembly all reach the same checkpoint, so a client has nothing to re-implement. Another technology connects through a small adapter."),
]

def home_story() -> str:
    cards = "\n".join(
        f"            <section>\n              <h3>{h}</h3>\n              <p>{p}</p>\n            </section>"
        for h, p in STORY
    )
    return f"""<section class="fx-section alt" id="why-foundgine">
        <div class="fx-wrap">
          <h2 id="the-story">Define once. Check every request. Keep your stack.</h2>
          <p class="fx-intro">Your customer system (CRM), business system (ERP), app connections (APIs), databases and AI assistants should not each have their own copy of what things mean, what is valid and who is allowed to do what. Foundgine is one shared checkpoint, which you set up, between the things your application can do and everything that asks it to do them.</p>
          <div class="fx-docs fx-story">
{cards}
          </div>
          <div class="fx-measure">
            <p><strong>Is it worth it for your business? Measure it.</strong> Try Foundgine on one small but realistic project. Compare before and after: work done more than once, effort needed to make changes, how well security is tested, and running costs. Foundgine does not promise a set percentage of savings for everyone.</p>
            <p><a href="/verified/">Every claim above, with its proof</a> &middot; <a href="https://github.com/Foundgine/Foundgine/blob/main/docs/wiki/why-foundgine.md">Business and architecture case</a> &middot; <a href="https://github.com/Foundgine/Foundgine/blob/main/docs/PRODUCT-POSITIONING-AND-COMMERCIAL-STRATEGY.md">Commercial strategy and measurement model</a></p>
          </div>
        </div>
      </section>

      """

# ---------------------------------------------------------------- helpers
def esc_attr(text: str) -> str:
    return text.replace("&", "&amp;").replace('"', "&quot;")

def set_title(html: str, rel: str) -> str:
    m = re.search(r"<title>(.*?)</title>", html, re.S)
    new = TITLES.get(rel)
    if not m or not new:
        return html
    old = m.group(1).strip()
    new_html = new.replace("&", "&amp;")
    if old != new_html:
        # Replace the old text wherever it is repeated (og:title, twitter:title, JSON-LD).
        html = html.replace(old, new_html)
        old_json = old.replace("&amp;", "&")
        if old_json != old:
            html = html.replace(old_json, new)
    return html

def set_description(html: str, rel: str) -> str:
    if rel == "index.html":
        pat = re.compile(r'(content=")[^"]*("\s+(?:name="description"|property="og:description"|name="twitter:description"))', re.S)
        html = pat.sub(lambda m: m.group(1) + esc_attr(HOME_DESC) + m.group(2), html)
        html = re.sub(r'("description":\s*")Foundgine gives \.NET[^"]*(")', lambda m: m.group(1) + HOME_DESC + m.group(2), html)
        return html
    if 'name="description"' not in html:
        tag = f'<meta content="{esc_attr(FALLBACK_DESC)}" name="description" />\n    '
        html = html.replace("</title>", "</title>\n    " + tag.rstrip(), 1)
    return html

ICONS = (
    '<link href="/assets/favicon.svg" rel="icon" type="image/svg+xml" />\n'
    '    <link href="/favicon.ico" rel="icon" sizes="48x48" />\n'
    '    <link href="/assets/favicon-32.png" rel="icon" sizes="32x32" type="image/png" />\n'
    '    <link href="/apple-touch-icon.png" rel="apple-touch-icon" />'
)

def set_icons(html: str) -> str:
    html = re.sub(r'\s*<link[^>]*rel="(?:icon|apple-touch-icon)"[^>]*/?>', "", html)
    return html.replace("</head>", "    " + ICONS + "\n  </head>", 1)

def set_theme_color(html: str) -> str:
    if 'name="theme-color"' in html:
        return html
    return html.replace("</title>", '</title>\n    <meta content="#0a1020" name="theme-color" />', 1)

def swap_block(html: str, marker: str, block: str, fallback_regex: str | None, anchor: str | None) -> str:
    """Replace <!-- marker -->...<!-- /marker --> or, on first run, the legacy block."""
    pat = re.compile(rf"<!-- {marker} -->.*?<!-- /{marker} -->", re.S)
    if pat.search(html):
        return pat.sub(lambda _m: block, html, count=1)
    if fallback_regex and re.search(fallback_regex, html, re.S):
        return re.sub(fallback_regex, lambda _m: block, html, count=1, flags=re.S)
    if anchor and anchor in html:
        return html.replace(anchor, block + "\n    " + anchor, 1)
    raise SystemExit(f"cannot place {marker}")

def apply_page(path: Path) -> str:
    rel = path.relative_to(SITE).as_posix()
    html = path.read_text(encoding="utf-8")
    html = set_title(html, rel)
    html = set_description(html, rel)
    html = set_theme_color(html)
    html = set_icons(html)
    link = '<a href="/verified/">Verified claims</a>'
    if link not in html.split("</header>")[0]:
        html = html.replace('<a href="/performance/">Performance reports</a>', link + '\n              <a href="/performance/">Performance reports</a>', 1)

    if rel == "index.html":
        a = html.index('<section class="fx-hero fx-hero-simple"')
        b = html.index('<section class="fx-section dark" id="model-once"')
        html = html[:a] + HOME_HERO + home_story() + html[b:]

    # Footer first, so the band can be placed in front of it.
    legacy_footer = r'<footer class="site-footer">.*?</footer>'
    html = swap_block(html, "fx:footer", FOOTER, legacy_footer, '<script src="/assets/site-nav.js"')

    band_pat = re.compile(r"\s*<!-- fx:rules -->.*?<!-- /fx:rules -->", re.S)
    html = band_pat.sub("", html)
    if rel != "index.html":
        html = html.replace("<!-- fx:footer -->", RULES_BAND + "\n    <!-- fx:footer -->", 1)
    return html

def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true")
    args = ap.parse_args()
    stale = []
    for path in sorted(SITE.rglob("*.html")):
        old = path.read_text(encoding="utf-8")
        new = apply_page(path)
        if new != old:
            stale.append(path.relative_to(SITE).as_posix())
            if not args.check:
                path.write_text(new, encoding="utf-8")
    if args.check:
        if stale:
            print("out of date:", *stale, sep="\n  ")
            return 1
        print("all pages match the template")
        return 0
    print(f"updated {len(stale)} pages")
    return 0

if __name__ == "__main__":
    sys.exit(main())
