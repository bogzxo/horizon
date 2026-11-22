#!/usr/bin/env pwsh
# Makes the engine's four NuGet packages (Horizon.HIDL, Horizon.Engine, Horizon.Platform.Desktop, Horizon.UI) into
# artifacts/nuget, or the folder given. The version is the one Directory.Build.props says. Used by hand and by the
# NuGet workflow, which is what publishes them.
param(
    [string]$Output = [IO.Path]::Combine($PSScriptRoot, '..', 'artifacts', 'nuget')
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path ([IO.Path]::Combine($PSScriptRoot, '..'))

# In the order they depend on each other, though each pack builds what it needs
$projects = @(
    'Horizon.HIDL/Horizon.HIDL.csproj',
    'Horizon/Horizon.csproj',
    'Horizon.Platform.Desktop/Horizon.Platform.Desktop.csproj',
    'Horizon.UI/Horizon.UI.csproj'
)

New-Item -ItemType Directory -Force $Output | Out-Null
Get-ChildItem $Output -Filter '*.nupkg' | Remove-Item

foreach ($project in $projects) {
    dotnet pack (Join-Path $root $project) --configuration Release --output $Output --nologo
    if ($LASTEXITCODE -ne 0) { throw "Packing $project failed." }
}

Get-ChildItem $Output -Filter '*.nupkg' | ForEach-Object { '{0}  {1:N2} MiB' -f $_.Name, ($_.Length / 1MB) }
