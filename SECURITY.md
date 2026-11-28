# Security policy

## Reporting a vulnerability

Please do not open a public issue. Use GitHub's private vulnerability reporting for this repository
(Security tab, "Report a vulnerability"). Include affected version, language (.NET or Java), a reproduction and the
impact. <!-- TODO: add a security contact email if you want one. -->

## Scope and testing

What is tested, how, and what is not covered is in [docs/SECURITY-TESTING.md](docs/SECURITY-TESTING.md).
AI-agent testing uses Strix and PentAGI (see the harness in `tools/security/ai-pentest`). Foundgine has not had an
independent penetration test or audit.
