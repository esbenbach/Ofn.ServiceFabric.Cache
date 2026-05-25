# Versioning & Release Flow

## Version Scheme

This project uses **Calendar Versioning (CalVer)** with the format:

```
yyyy.M.d.{run_number}
```

For example: `2026.5.25.42`

- `yyyy` — full year
- `M` — month (no leading zero)
- `d` — day (no leading zero)
- `run_number` — GitHub Actions workflow run number (monotonically increasing)

Preview (pre-release) packages append `-preview`: `2026.5.25.42-preview`

## How Packages Are Published

### Automatic (on every push to `main`)

1. GitHub Actions builds, tests, and packs the libraries.
2. Packages are versioned as `yyyy.M.d.{run_number}-preview`.
3. Preview packages are pushed to **GitHub Packages** (the repository's NuGet feed).

These are pre-release packages — consumers won't get them unless they opt in.

### Release (manual approval)

1. Go to the [Actions tab](https://github.com/esbenbach/Ofn.ServiceFabric.Cache/actions) on GitHub.
2. Find the workflow run you want to release (it corresponds to a specific commit on `main`).
3. Click **Review deployments** on the "Publish to NuGet.org" job.
4. Approve the deployment.
5. The workflow rebuilds the same commit with the release version (no `-preview` suffix).
6. Packages are pushed to **NuGet.org**.
7. A **GitHub Release** is automatically created with the `.nupkg` files attached.

No manual tagging, branching, or version entry is required.

## Where Packages End Up

| Package | Preview Feed | Release Feed |
|---------|--------------|--------------|
| `Ofn.ServiceFabric.Cache` | GitHub Packages | NuGet.org |
| `Ofn.ServiceFabric.Cache.Client` | GitHub Packages | NuGet.org |
| `Ofn.ServiceFabric.Cache.Abstractions` | GitHub Packages | NuGet.org |

## Local Development

When building locally without `PACKAGE_VERSION` set, packages are versioned `0.0.0-local`. This prevents accidentally publishing local builds.

```sh
dotnet build        # produces 0.0.0-local
dotnet pack         # produces 0.0.0-local.nupkg
```

To simulate a specific version locally:

```sh
$env:PACKAGE_VERSION = "1.0.0-test"
dotnet build
dotnet pack
```

## Configuration

### Secrets (GitHub Settings → Secrets → Actions)

| Secret | Purpose |
|--------|---------|
| `NUGET_API_KEY` | API key for pushing to NuGet.org |

`GITHUB_TOKEN` is provided automatically by GitHub Actions.

### Environment (GitHub Settings → Environments)

| Environment | Protection |
|-------------|------------|
| `NuGet-Release` | Required reviewer (you) — prevents accidental releases |

## Pipeline File

The workflow is defined in [`.github/workflows/ci-release.yml`](.github/workflows/ci-release.yml).

## Legacy

The Azure DevOps pipeline at [`Deploy/azure-pipelines.yml`](Deploy/azure-pipelines.yml) is deprecated and kept for reference only.
