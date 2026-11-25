# AI and search access to this repository

Every file in this repository is public and licensed under Apache-2.0, and `robots.txt` allows all crawlers on all paths. Nothing needs restructuring to read, clone or download any part of it. This page lists every supported way in, and what to do once per service so it picks the repository up.

## Get the files

| Way | Address |
|---|---|
| Clone | `git clone https://github.com/Foundgine/Foundgine.git` |
| Zip | https://github.com/Foundgine/Foundgine/archive/refs/heads/main.zip |
| Tarball | https://github.com/Foundgine/Foundgine/archive/refs/heads/main.tar.gz |
| One file, raw | `https://raw.githubusercontent.com/Foundgine/Foundgine/main/<path>` |
| Complete file list (GitHub API) | https://api.github.com/repos/Foundgine/Foundgine/git/trees/main?recursive=1 |
| File list in the repo | [`repo-index.json`](../repo-index.json) (path and size of every file; `"large": true` at 1 MB or more) |
| Directory map in the repo | [`REPO-MAP.md`](../REPO-MAP.md) |

The raw benchmark JSON files are large (up to about 30 MB each). Agents should skip anything flagged `large` in `repo-index.json` and read the summaries in `docs-site/agent-benchmark/` and `docs/performance/` instead.

## Files that tell AI tools where things are

| File | Read by / purpose |
|---|---|
| [`llms.txt`](../llms.txt), [`llms-full.md`](../llms-full.md) | Concise and extended project context. GitMCP serves `llms.txt` first. Site copies: `docs-site/llms.txt`, `docs-site/llms-full.md`. |
| [`AGENTS.md`](../AGENTS.md) | Build and test commands and architectural rules for coding agents. `CLAUDE.md` and `.github/copilot-instructions.md` point to it. |
| [`robots.txt`](../robots.txt), [`docs-site/robots.txt`](../docs-site/robots.txt) | Explicit allow rules for search, AI-search, assistant and training crawlers, plus the sitemap. |
| [`docs-site/sitemap.xml`](../docs-site/sitemap.xml) | Website URLs. |
| [`context7.json`](../context7.json) | Context7 indexing rules (title, description, excluded build and raw-data folders, agent rules). |
| [`.devin/wiki.json`](../.devin/wiki.json) | Notes that steer the DeepWiki-generated wiki. |
| [`.mcp.json`](../.mcp.json), [`.cursor/mcp.json`](../.cursor/mcp.json) | Ready-made MCP client configs for GitMCP, DeepWiki and Context7. |
| [`mcp.json`](../mcp.json), [`.well-known/mcp.json`](../.well-known/mcp.json) | Description of Foundgine's own MCP server, not of this repository. |
| [`CITATION.cff`](../CITATION.cff) | Citation metadata (GitHub shows "Cite this repository"). |

## Hosted services

### GitMCP
No setup. Add `https://gitmcp.io/Foundgine/Foundgine` as an MCP server. The documentation site is reachable as `https://foundgine.gitmcp.io/Foundgine`. GitMCP serves `llms.txt` first and then the README, and can search the code. When it fetches pages from GitHub Pages sites it follows `robots.txt`, which here allows everything.

### DeepWiki (Cognition)
Open https://deepwiki.com/Foundgine/Foundgine once; a public repository that is not yet indexed is generated on first visit. Steering notes in `.devin/wiki.json` are read during generation. MCP endpoint: `https://mcp.deepwiki.com/mcp`, tools `read_wiki_structure`, `read_wiki_contents` and `ask_question`.

### Context7 (Upstash)
Submit the repository once at https://context7.com/add-library?tab=github. `context7.json` is read from the repository root. It deliberately replaces Context7's default exclusions, so `CHANGELOG.md` and the `benchmarks` folders are indexed; only build output and the raw benchmark artifacts are excluded. Inclusion and ranking are decided by Context7. To refresh after a release, use https://context7.com/refresh-library. MCP endpoint: `https://mcp.context7.com/mcp`.

## Search engines

- Submit https://foundgine.github.io/Foundgine/docs-site/sitemap.xml in Google Search Console and Bing Webmaster Tools.
- Every site page carries a canonical URL, a robots meta tag allowing full snippets, and links to `llms.txt` and `llms-full.md`. The landing page also carries `SoftwareSourceCode` structured data.

## Things this repository cannot do from inside itself

1. **`robots.txt` location.** Crawlers read `robots.txt` at the root of a host. This site is a GitHub Pages project site at `foundgine.github.io/Foundgine/`, so the file in this repository is served under `/Foundgine/` and is not the host-level file. The same text can be published at `https://foundgine.github.io/robots.txt` from a repository named `foundgine.github.io` in the Foundgine organization, or by moving the site to a custom domain. The repository file still documents policy for tools that read repository files, and GitMCP checks it.
2. **GitHub repository settings.** Set the About description, website (`https://foundgine.github.io/Foundgine/docs-site/`) and topics in the repository settings. Suggested topics: `semantic-layer`, `dotnet`, `java`, `graphql`, `mcp`, `model-context-protocol`, `ai-agents`, `authorization`, `llms-txt`.
3. **Keeping the index current.** Run `python tools/generate-repo-index.py` after adding, removing or moving files; `--check` exits non-zero when `repo-index.json` or `REPO-MAP.md` is stale.

## `.nojekyll`

An empty `.nojekyll` file at the repository root turns off Jekyll processing on GitHub Pages. Without it, Pages skips dot-folders such as `.well-known/`, and Markdown files that start with front matter are converted to HTML instead of being served as Markdown. Delete the file if you rely on that conversion.
