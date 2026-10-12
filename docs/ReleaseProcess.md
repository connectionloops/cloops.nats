# Release Process

The CLOOPS NATS SDK uses a fully automated CI/CD pipeline for version management and NuGet package publishing. This document outlines the release workflow, versioning strategy, and troubleshooting procedures.

## 🚀 Automated Release Workflow

### Overview

The release process is designed to be hands-off and reliable, automatically handling versioning, building, testing, and publishing whenever code changes are pushed to the main branch. The workflow is implemented using GitHub Actions and runs automatically on every push to `main`.

### Release Triggers

Releases are automatically triggered when: **Code is pushed to `main` branch** - Primary release trigger

The workflow consists of two jobs:

1. **Build Job**: Builds the project, runs tests, and validates the code
2. **Publish Job**: Creates NuGet package, publishes to NuGet.org, creates GitHub tag, and creates GitHub release

### Pipeline Stages

```mermaid
graph LR
    A[Push to Main] --> B[Trigger GitHub Actions]
    B --> C[Calculate Version]
    C --> D[Build & Test Job]
    D --> E{Tests Pass?}
    E -->|Yes| F[Publish Job]
    E -->|No| G[Pipeline Fails]
    F --> H[Pack NuGet]
    H --> I[Publish to NuGet.org]
    I --> J[Create GitHub Tag]
    J --> K[Create GitHub Release]
    K --> L[Release Complete]
```

## 📋 Version Management Strategy

Version management is completely controlled by GitHub Actions workflow environment variables defined in `.github/workflows/ci.yml`.

### Version Format

The version follows the semantic versioning pattern: `MAJOR.MINOR.PATCH`

Where:

- **MAJOR**: Defined in workflow env as `MAJOR` (currently `2`)
- **MINOR**: Defined in workflow env as `MINOR` (currently `0`)
- **PATCH**: Calculated by the build job: the highest existing `v{MAJOR}.{MINOR}.*` tag plus one, or `0` when no tag exists for that `MAJOR.MINOR` yet

**Example**: With `MAJOR=2`, `MINOR=0` and no `v2.0.*` tag, the first release is `2.0.0`, the next `2.0.1`, and so on. Bumping `MINOR` to `1` starts again at `2.1.0`.

Up to 1.4 the third component was the GitHub Actions run number, which never resets (last 1.x release: `1.4.13`). Runs are serialized (`concurrency` in `ci.yml`) so two quick merges cannot compute the same version, and the publish job reuses the build job's version so package, tag and release always agree.

### Version Components

| Component | Description               | Management                                              |
| --------- | ------------------------- | ------------------------------------------------------- |
| **Major** | Major version number      | Manual update in `.github/workflows/ci.yml` env section |
| **Minor** | Minor version number      | Manual update in `.github/workflows/ci.yml` env section |
| **Patch** | Next free patch for `MAJOR.MINOR` | Automatic (from existing `v{MAJOR}.{MINOR}.*` tags)     |

### Updating Version

To update the major or minor version:

1. Edit `.github/workflows/ci.yml`
2. Update the `MAJOR` or `MINOR` values in the `env` section; the patch restarts at `0` automatically
3. Add a `CHANGELOG.md` entry naming the new version
4. Commit and push to `main`
5. The next workflow run will use the new version numbers

> As a philosophy, all events, messages and subjects are always backward compatible. A new SDK version always \*adds\*\* new events, messages never modifies existing events and messages, unless deprecation is authorized confirming absolutely no one is using old payload

## 📦 NuGet Package Management

### Package Information

- **Package ID**: `cloops.nats`
- **Target Framework**: .NET 9.0+
- **Dependencies**: Automatically managed via .csproj files
- **Package Repository**: NuGet.org (public feed)
- **Authentication**: NuGet [Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing) - no stored API key.
  The policy on nuget.org (owner `cloops`, account `gskalele`) trusts `connectionloops/cloops.nats` → workflow `ci.yml` → environment `production`.
  Each run exchanges its GitHub OIDC token for a short-lived key via `NuGet/login@v1`; there is nothing to rotate or expire.
  If the repository, workflow filename, or environment name changes, the policy on nuget.org must be updated to match or publishing fails with 403.

### Publishing Process

The publish job automatically:

1. Calculates the package version using the workflow's version strategy
2. Builds the project in Release configuration
3. Creates the NuGet package (`.nupkg` file)
4. Uploads the package as a build artifact
5. Publishes to NuGet.org using a short-lived key from Trusted Publishing (OIDC)
6. Skips duplicate packages if the same version already exists

### Package Contents

Each published package includes:

- **SDK Libraries**: Compiled .NET assemblies
- **XML Documentation**: IntelliSense support for consumers
- **Dependencies**: Referenced packages and versions
- **Metadata**: Package description, authors, license information

## 🏷️ GitHub Release Management

### Automatic Tag Creation

After successfully publishing to NuGet, the workflow automatically:

1. Creates a Git tag in the format `v{version}` (e.g., `v2.0.0`)
2. Tags the commit that triggered the workflow
3. Skips tag creation if the tag already exists (idempotent)

### Automatic Release Creation

The workflow also automatically creates a GitHub Release:

- **Tag**: Uses the version tag created above
- **Title**: `Release v{version}`
- **Body**: Includes version, build number, and commit SHA
- **Status**: Published (not draft or prerelease)
- **Idempotent**: Skips creation if release already exists

The release notes are automatically generated and include:

- Version number
- Build number (GitHub Actions run number)
- Commit SHA that triggered the release

## Consumer patterns

> Since all versions are backward compatible, consumers should always stay on latest version of SDK.
