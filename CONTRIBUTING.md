# Contributing to Azure DevOps Forager

Thanks for considering a contribution. This is a small, self-hostable project, so the bar for
process is deliberately low — but a few conventions keep the codebase consistent.

## Building and testing

Full build/test instructions — including per-project `dotnet build` commands, framework notes,
and the running-Indexer file-lock gotcha — live in the
[Developer Guide, section 6 (Build & Test)](docs/DEVELOPER_GUIDE.md#6-build--test). Read that
before your first build; it covers things (like the `System.Text.Json` version pin) that aren't
obvious from the project files alone.

At minimum, before opening a PR:

```bash
dotnet build AzureDevOpsForager.Core
dotnet build AzureDevOpsForager.Server
dotnet build AzureDevOpsForager.Indexer     # net8.0-windows — build on Windows
dotnet build AzureDevOpsForager.WinForms    # net48 — build on Windows
dotnet test  AzureDevOpsForager.Tests
```

## Code style

Match the surrounding code:

- Allman braces, with a space inside parens — `if( condition )`, `foreach( var x in y )`.
- File-scoped `namespace` declarations.
- Verbose XML doc comments (`<summary>`, `<param>`, `<returns>`) on every public type and member —
  explain *why*, not just *what*.
- `#region` blocks for data members / constructor / public methods, matching the existing files.
- Keep methods under ~60 lines (85 is the hard ceiling); split out a helper rather than exceed it.
- No hardcoded credentials, connection strings, or tokens anywhere in source. All configuration
  goes through `config.json` (see `config.sample.json`) or environment variables / `secrets.enc`.

## Pull requests

- Keep PRs scoped to one change; unrelated cleanups make review harder.
- Include a one-line "why," not just "what," in the PR description.
- Add or update tests in `AzureDevOpsForager.Tests` for any behavior change that can be tested
  without live SQL or the ONNX/reranker models (see existing suites for the pattern).
- Update `docs/DEVELOPER_GUIDE.md`, `docs/USER_GUIDE.md`, or `docs/FUNCTIONAL_MATRIX.md` if the
  change affects architecture, setup, or user-facing behavior.
- Make sure `dotnet build` is warning-free and `dotnet test` passes before requesting review.

## Reporting bugs / requesting features

Open a GitHub issue with enough detail to reproduce (config shape, source type, error text/logs).
Please don't include real connection strings, tokens, or PATs in issues — redact them.

## Security issues

Do not open a public issue for a security vulnerability — see [SECURITY.md](SECURITY.md) for how
to report privately.
