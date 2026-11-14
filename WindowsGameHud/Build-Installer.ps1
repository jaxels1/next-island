# SPDX-License-Identifier: AGPL-3.0-only
[CmdletBinding()]
param(
    [string] $OutputDirectory,
    [string] $ProviderCache = (Join-Path $PSScriptRoot 'obj/provider-cache'),
    [string] $ExpectedTag
)

$ErrorActionPreference = 'Stop'
[xml] $project = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'NextIsland.csproj')
$version = $project.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
$velopackVersion = $project.SelectSingleNode("//PackageReference[@Include='Velopack']").Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Expected a stable app version, got '$version'." }
if ($ExpectedTag -and $ExpectedTag -cne "v$version") { throw "Tag '$ExpectedTag' must match app version v$version." }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot "bin/installer/$version" }
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)

$toolDirectory = Join-Path $PSScriptRoot "obj/vpk/$velopackVersion"
$vpk = Join-Path $toolDirectory 'vpk.exe'
if (-not (Test-Path -LiteralPath $vpk -PathType Leaf)) {
    dotnet tool install vpk --tool-path $toolDirectory --version $velopackVersion
    if ($LASTEXITCODE -ne 0) { throw "Could not install vpk $velopackVersion." }
}

$publishParent = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'obj/installer'))
$publish = Join-Path $publishParent ([Guid]::NewGuid().ToString('N'))
try {
    & (Join-Path $PSScriptRoot 'Build-LocalBundle.ps1') -OutputDirectory $publish -ProviderCache $ProviderCache
    & $vpk pack --packId NextIsland --packTitle 'Next Island' --packAuthors 'Next Island contributors' `
        --packVersion $version --runtime win-x64 --channel win --packDir $publish --mainExe NextIsland.exe `
        --shortcuts StartMenuRoot --delta None --noPortable --outputDir $output
    if ($LASTEXITCODE -ne 0) { throw "Packaging Next Island failed (exit code $LASTEXITCODE)." }

    Get-Item -LiteralPath (Join-Path $output 'NextIsland-win-Setup.exe')
    Get-Item -LiteralPath (Join-Path $output "NextIsland-$version-full.nupkg")
    Get-Item -LiteralPath (Join-Path $output 'releases.win.json')
}
finally {
    if (Test-Path -LiteralPath $publish -PathType Container) {
        $resolvedPublish = (Resolve-Path -LiteralPath $publish).Path
        if ([IO.Path]::GetDirectoryName($resolvedPublish) -eq $publishParent) {
            Remove-Item -LiteralPath $resolvedPublish -Recurse -Force
        }
    }
}
