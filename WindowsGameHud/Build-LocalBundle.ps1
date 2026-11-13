# SPDX-License-Identifier: AGPL-3.0-only
# Added 2026-11-12 for the local Windows bundle.
[CmdletBinding()]
param(
    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'bin/Next Island'),
    [string] $ProviderCache = (Join-Path $PSScriptRoot 'obj/provider-cache')
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'NextIsland.csproj'
$OutputDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
$ProviderCache = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ProviderCache)
New-Item -ItemType Directory -Force -Path $ProviderCache | Out-Null

function Get-Asset([string] $Name, [string] $Url, [string] $Sha256) {
    $path = Join-Path $ProviderCache $Name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        $download = Join-Path $ProviderCache "$Name.download"
        Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $download
        Move-Item -LiteralPath $download -Destination $path -Force
    }
    if ($Sha256 -and (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $Sha256) {
        throw "Checksum mismatch for $path. Remove this cached file and rebuild."
    }
    return $path
}

$presentMon = Get-Asset 'PresentMon-2.5.1-x64.exe' `
    'https://github.com/GameTechDev/PresentMon/releases/download/v2.5.1/PresentMon-2.5.1-x64.exe' `
    '9bec3083069f58f911e6a512f4806db51a27bd096103087bc1d05ef54c80a191'
$presentMonLicense = Get-Asset 'PresentMon-LICENSE.txt' 'https://raw.githubusercontent.com/GameTechDev/PresentMon/v2.5.1/LICENSE.txt'
$mplLicense = Get-Asset 'MPL-2.0.txt' 'https://raw.githubusercontent.com/LibreHardwareMonitor/LibreHardwareMonitor/v0.9.6/LICENSE'

$intermediate = Join-Path $PSScriptRoot 'obj'
New-Item -ItemType Directory -Force -Path $intermediate | Out-Null
$bundleLock = Join-Path $intermediate 'packages.bundle.lock.json'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'packages.lock.json') -Destination $bundleLock -Force
dotnet publish $project -c Release -r win-x64 --self-contained true -o $OutputDirectory -warnaserror "-p:NuGetLockFilePath=$bundleLock"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit code $LASTEXITCODE)." }

$providers = Join-Path $OutputDirectory 'Providers'
$licenses = Join-Path $OutputDirectory 'Licenses'
New-Item -ItemType Directory -Force -Path $providers, $licenses | Out-Null
Copy-Item -LiteralPath $presentMon -Destination $providers -Force
Copy-Item -LiteralPath $presentMonLicense, $mplLicense -Destination $licenses -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../LICENSE'),
    (Join-Path $PSScriptRoot '../README.md'), (Join-Path $PSScriptRoot 'THIRD-PARTY.md') -Destination $OutputDirectory -Force

$assets = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'obj/project.assets.json') | ConvertFrom-Json
$deps = Get-Content -Raw -LiteralPath (Join-Path $OutputDirectory 'NextIsland.deps.json') | ConvertFrom-Json
$netPack = $deps.libraries.PSObject.Properties.Name | Where-Object { $_ -like 'runtimepack.Microsoft.NETCore.App.Runtime.win-x64/*' }
$wpfPack = $deps.libraries.PSObject.Properties.Name | Where-Object { $_ -like 'runtimepack.Microsoft.WindowsDesktop.App.Runtime.win-x64/*' }

function Copy-PackageNotice([string] $Package, [string] $Name, [string] $Destination) {
    foreach ($root in $assets.packageFolders.PSObject.Properties.Name) {
        $notice = Join-Path (Join-Path $root $Package.ToLowerInvariant()) $Name
        if (Test-Path -LiteralPath $notice -PathType Leaf) {
            Copy-Item -LiteralPath $notice -Destination (Join-Path $licenses $Destination) -Force
            return
        }
    }
    throw "Could not find $Name in $Package."
}

$netPackage = $netPack -replace '^runtimepack\.', ''
$wpfPackage = $wpfPack -replace '^runtimepack\.', ''
$wpfVersion = ($wpfPackage -split '/')[1]
Copy-PackageNotice $netPackage 'LICENSE.TXT' 'dotnet-LICENSE.txt'
Copy-PackageNotice $netPackage 'THIRD-PARTY-NOTICES.TXT' 'dotnet-THIRD-PARTY-NOTICES.txt'
Copy-PackageNotice $wpfPackage 'LICENSE' 'WPF-LICENSE.txt'
Copy-PackageNotice 'hidsharp/2.6.4' 'LICENSE.txt' 'HidSharp-LICENSE.txt'
$wpfNotice = Get-Asset "WPF-$wpfVersion-NOTICES.txt" "https://raw.githubusercontent.com/dotnet/wpf/v$wpfVersion/THIRD-PARTY-NOTICES.TXT"
Copy-Item -LiteralPath $wpfNotice -Destination (Join-Path $licenses 'WPF-THIRD-PARTY-NOTICES.txt') -Force

Write-Output (Join-Path $OutputDirectory 'NextIsland.exe')
