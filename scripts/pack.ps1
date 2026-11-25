#!/usr/bin/env pwsh
# Makes the engine's NuGet packages (bogzxo.Horizon.HIDL, bogzxo.Horizon.Engine, the platforms bogzxo.Horizon.Platform.Desktop,
# .Android and .iOS, bogzxo.Horizon.UI, bogzxo.Horizon.Terminal, bogzxo.Horizon.Tooling and the dotnet tool
# bogzxo.Horizon.Shaders.Build) into artifacts/nuget, or the folder given. The version is the one Directory.Build.props
# says. Used by hand and by the NuGet workflow, which is what publishes them. The Android and iOS platforms need their
# workloads (dotnet workload install android ios), and iOS a Mac or Windows; -SkipMobile leaves them out.
param(
    [string]$Output = [IO.Path]::Combine($PSScriptRoot, '..', 'artifacts', 'nuget'),
    [switch]$SkipMobile
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path ([IO.Path]::Combine($PSScriptRoot, '..'))

# In the order they depend on each other, though each pack builds what it needs
$projects = @(
    'Horizon.HIDL/Horizon.HIDL.csproj',
    'Horizon/Horizon.csproj',
    'Horizon.Platform.Desktop/Horizon.Platform.Desktop.csproj',
    'Horizon.UI/Horizon.UI.csproj',
    'Horizon.Terminal/Horizon.Terminal.csproj',
    'Horizon.Tooling/Horizon.Tooling.csproj',
    'Horizon.Shaders.Build/Horizon.Shaders.Build.csproj'
)
if (-not $SkipMobile) {
    $projects += @(
        'Horizon.Platform.Android/Horizon.Platform.Android.csproj',
        'Horizon.Platform.iOS/Horizon.Platform.iOS.csproj'
    )
}

New-Item -ItemType Directory -Force $Output | Out-Null
Get-ChildItem $Output -Filter '*.nupkg' | Remove-Item

foreach ($project in $projects) {
    dotnet pack (Join-Path $root $project) --configuration Release --output $Output --nologo
    if ($LASTEXITCODE -ne 0) { throw "Packing $project failed." }
}

Get-ChildItem $Output -Filter '*.nupkg' | ForEach-Object { '{0}  {1:N2} MiB' -f $_.Name, ($_.Length / 1MB) }
