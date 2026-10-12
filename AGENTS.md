# Agent Instructions

Rules for anyone, human or automated, who changes this repository.

## Repository at a glance

- `sdk/`: the `cloops.nats` NuGet package (`sdk/cloops.nats.sdk.csproj`, net9.0, NATS.Net 3.x).
- `tests/cloops.nats.sdk.Tests/`: xUnit tests. `JetStreamIntegrationTests` need a JetStream server and skip unless `NATS_TEST_URL` is set (see `docs/LocalSetup.md`).
- `examples/`: runnable examples (Debug configuration only).
- `docs/`: functionality, environment variables, local setup and the release process.
- `.github/workflows/ci.yml`: builds, tests and, on `main`, publishes `MAJOR.MINOR.PATCH` to NuGet (see `docs/ReleaseProcess.md`).

## Build and test

```bash
dotnet build sdk/cloops.nats.sdk.csproj
dotnet test tests/cloops.nats.sdk.Tests/cloops.nats.sdk.Tests.csproj
# with a JetStream server for the integration tests:
docker run -d --name nats-it -p 14333:4222 nats:2.11.7-alpine -js
NATS_TEST_URL=nats://127.0.0.1:14333 dotnet test tests/cloops.nats.sdk.Tests/cloops.nats.sdk.Tests.csproj
```

All tests must pass before a change is merged.

## Compatibility

- Keep the public API source and binary compatible. Add a new overload instead of an optional parameter on a shipped member: optional arguments bind at the call site, and adding one breaks already-compiled callers with `MissingMethodException`.
- A breaking change, including a major-version bump of a dependency whose types the SDK exposes (NATS.Net), needs a `MAJOR` bump in `.github/workflows/ci.yml`. A new feature bumps `MINOR`. The patch number is calculated automatically.

## Changelog rule

**Every change or PR must add an entry to [CHANGELOG.md](CHANGELOG.md).** A change without a changelog entry is not complete.

1. Add a new row at the **top** of the index table (newest first): `| YYYY-MM-DD | [One-line description](#anchor) |`.
2. Add a new section directly **below the table**, above the previous newest section, with a one-line heading that contains the date: `## YYYY-MM-DD — One-line description (vX.Y.Z)`.
3. Under the heading, write **up to 10 bullets**: what changed, why, impact on consumers (call out anything breaking or any changed default), and the version if known. Base them on the actual diff, not just the commit subject.
4. The table link must match GitHub's heading anchor: lowercase; drop every character except letters, digits, spaces, `-` and `_`; then replace each space with `-`. The em dash and the spaces around it therefore become `--`, and dots and parentheses disappear. For example, `## 2026-10-11 — Upgrade to NATS.Net 3.3.0 (v2.0.0)` becomes `#2026-10-11--upgrade-to-natsnet-330-v200`. Two headings with the same text get `-1`, `-2`, ... appended, so keep headings unique.
5. Use the date the change is committed or merged. If the release version is not known yet, use the version CI will publish (`MAJOR.MINOR` from `ci.yml`, plus the next patch), or `(unreleased)`.

Template to copy:

```markdown
| YYYY-MM-DD | [Short description of the change (vX.Y.Z)](#yyyy-mm-dd--short-description-of-the-change-vxyz) |
```

```markdown
## YYYY-MM-DD — Short description of the change (vX.Y.Z)

- **What:** the change itself, naming the public types, methods or settings involved.
- **Why:** the problem it solves or the incident that prompted it.
- **Impact:** what consumers notice; say explicitly if nothing changes for them.
- **Breaking / defaults:** any breaking change or changed default, and what consumers must do.
- **Tests:** what proves it works.
- **Version:** the published or expected package version.
```

## Commits and PRs

- Use conventional commit messages (`feat:`, `fix:`, `docs:`, `test:`, `ci:`, `build:`, `chore:`; `!` or a `BREAKING CHANGE:` footer for breaking changes).
- Do not add AI or tool attribution to commits, PR descriptions or files (no `Co-Authored-By` lines for tools, no "Generated with" footers, no session links).
- PR descriptions cover the motivation, the version impact, the code changes, breaking changes for consumers and test evidence.
