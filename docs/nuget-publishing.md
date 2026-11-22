# Publishing the engine to nuget.org

The engine goes out as four packages of one version, made by `scripts/pack.ps1` and published by the `NuGet` workflow (`.github/workflows/nuget.yml`).

| Package | Project | Depends on |
|---|---|---|
| `bogzxo.Horizon.HIDL` | `Horizon.HIDL/Horizon.HIDL.csproj` | nothing |
| `bogzxo.Horizon.Engine` | `Horizon/Horizon.csproj` | `bogzxo.Horizon.HIDL` |
| `bogzxo.Horizon.Platform.Desktop` | `Horizon.Platform.Desktop/Horizon.Platform.Desktop.csproj` | `bogzxo.Horizon.Engine` |
| `bogzxo.Horizon.UI` | `Horizon.UI/Horizon.UI.csproj` | `bogzxo.Horizon.Engine`, `bogzxo.Horizon.HIDL` |

The IDs start with `bogzxo.` because somebody else has reserved the `Horizon` prefix on nuget.org (a push under it is refused with 409 "The package ID is reserved"). The engine's assembly is still `Horizon.dll`. The packages depend on each other at exactly their own version.

## One-time setup

### 1. The trusted publishing policy

At https://www.nuget.org/account/trustedpublishing, add a policy for GitHub Actions with these values. Copy them as they are.

| Field | Value |
|---|---|
| Policy name (if asked) | `horizon-github-actions` |
| Package owner | your nuget.org account |
| Repository Owner | `bogzxo` |
| Repository | `horizon` |
| Workflow File | `nuget.yml` |
| Environment | leave empty |

Workflow File is the file's name only, without `.github/workflows/`. Matching ignores case.

If the repository is private, the policy starts out active for seven days and becomes permanent at the first publish. If nothing is published in that time it goes inactive, and can be started again from the same page.

### 2. The username secret

The workflow needs your nuget.org username, the profile name and not the email address, as a repository secret.

- Name: `NUGET_USER`
- Value: your nuget.org username

In the repository: Settings, Secrets and variables, Actions, New repository secret. Or from a shell:

```
gh secret set NUGET_USER --repo bogzxo/horizon
```

No API key is stored anywhere. Each run gets one from nuget.org that lasts an hour.

## Publishing a version

1. Set the version in `Directory.Build.props` (`<Version>`), and the same number in `docs/nuget-readme.md`. Commit and push.
2. Tag that commit `v` and the version, and push the tag:

```
git tag -a v1.5.0 -m "Horizon 1.5.0"
git push origin v1.5.0
```

The workflow checks the tag against the version, packs the four packages and pushes them. A version nuget.org already has, or once had, fails the push with 409 Conflict and fails the run: raise the version and tag again.

A published version cannot be replaced, only unlisted. To publish a change, raise the version.

## Packing without publishing

```
pwsh scripts/pack.ps1
```

The packages land in `artifacts/nuget`. The same happens when the workflow is run by hand from the Actions tab with "Publish to nuget.org as well" left off: the packages are the run's `nuget-packages` artifact.

To upload by hand instead, use https://www.nuget.org/packages/manage/upload, in the order of the table above.

## Using the packages

```xml
<ItemGroup>
  <PackageReference Include="bogzxo.Horizon.Engine" Version="1.5.0" />
  <PackageReference Include="bogzxo.Horizon.Platform.Desktop" Version="1.5.0" />
  <PackageReference Include="bogzxo.Horizon.UI" Version="1.5.0" />
</ItemGroup>
```

`bogzxo.Horizon.Engine` copies its shaders, fonts and UI skins next to the program when it is built and published, at the paths the engine looks for them at (`shaders/`, `Assets/global/`, `Assets/fonts/`, `Assets/uix/`).

## What is not decided

- A licence. The packages name none, and nuget.org shows that. Add `<PackageLicenseExpression>` or a licence file to `Directory.Build.props` once there is one.
- An icon for the packages.
