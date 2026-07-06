# Security Policy

## Reporting a vulnerability

If you believe you've found a security vulnerability in Azure DevOps Forager, please **do not**
open a public GitHub issue or pull request describing it.

Instead, report it privately using GitHub's
[private vulnerability reporting](../../security/advisories/new) for this repository (under the
**Security** tab → **Report a vulnerability**). This opens a private advisory that only the
maintainer can see until a fix is ready.

Please include, where possible:

- A description of the vulnerability and its potential impact.
- Steps to reproduce, or a minimal proof of concept.
- The version/commit you tested against.

## What to expect

This is a small, self-hostable project maintained by one person, so response times are best-effort
rather than SLA-backed. You'll get an acknowledgment, an assessment of impact, and — once a fix is
available — credit in the advisory unless you'd prefer to stay anonymous.

## Scope notes

- Secrets (the Groq API key, the Hugging Face token, SQL connection strings) are meant to live only
  in environment variables or the encrypted `secrets.enc` / `config.json` (gitignored) on the
  machine you deploy to — never commit them or paste them into a public issue.
- If a report involves a third-party dependency (ONNX Runtime, a NuGet package, etc.) rather than
  this project's own code, please still report it here first so it can be triaged and, if needed,
  forwarded upstream.
