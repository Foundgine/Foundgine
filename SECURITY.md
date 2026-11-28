# Security policy

## Supported versions

Security fixes are made against the latest released version (currently 3.4.x) in each language. Upgrade to the latest
release before reporting, and say which version you tested.

## Reporting a vulnerability

Please do not open a public issue. Use GitHub's private vulnerability reporting for this repository
(Security tab, "Report a vulnerability"). Include the affected version, language (.NET, Java, TypeScript, Python or
WebAssembly), a reproduction and the impact.

Reports are triaged by the maintainers. Fixes are released with a CHANGELOG entry that states the impact and the
affected versions.

## Scope and testing

What is tested, how, and what is not covered is in [docs/SECURITY-TESTING.md](docs/SECURITY-TESTING.md).
Penetration and red-team suites run in CI for .NET and Java, and AI-agent testing uses Strix and PentAGI (harness in
`tools/security/ai-pentest`, results in the security testing log). Foundgine has not had an independent penetration
test or audit.
