# SPDX-License-Identifier: AGPL-3.0-only
[CmdletBinding()]
param(
    [string] $OutputDirectory,
    [string] $ProviderCache = (Join-Path $PSScriptRoot 'obj/provider-cache'),
    [string] $ExpectedTag,
    [ValidatePattern('^[0-9A-Fa-f]{40}$')]
    [string] $SigningCertificateThumbprint
)

$ErrorActionPreference = 'Stop'
[xml] $project = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'NextIsland.csproj')
$version = $project.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
$velopackVersion = $project.SelectSingleNode("//PackageReference[@Include='Velopack']").Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Expected a stable app version, got '$version'." }
if ($ExpectedTag -and $ExpectedTag -cne "v$version") { throw "Tag '$ExpectedTag' must match app version v$version." }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot "bin/installer/$version" }
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)

$signingArguments = @()
if ($SigningCertificateThumbprint) {
    $certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$SigningCertificateThumbprint" -ErrorAction Stop
    $chain = [Security.Cryptography.X509Certificates.X509Chain]::new()
    try {
        $chain.ChainPolicy.ApplicationPolicy.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3')) | Out-Null
        if (-not $certificate.HasPrivateKey -or $certificate.Subject -eq $certificate.Issuer -or -not $chain.Build($certificate)) {
            throw 'Signing requires a trusted code-signing certificate with an available private key.'
        }
    }
    finally { $chain.Dispose() }
    $signingArguments = @('--signParams', "/sha1 $SigningCertificateThumbprint /fd SHA256 /tr http://timestamp.digicert.com /td SHA256")
}

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
        --shortcuts StartMenuRoot --delta None --noPortable --outputDir $output @signingArguments
    if ($LASTEXITCODE -ne 0) { throw "Packaging Next Island failed (exit code $LASTEXITCODE)." }

    $setup = Get-Item -LiteralPath (Join-Path $output 'NextIsland-win-Setup.exe')
    $package = Get-Item -LiteralPath (Join-Path $output "NextIsland-$version-full.nupkg")
    if ($SigningCertificateThumbprint) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($package.FullName)
        try {
            $signedFiles = @($setup.FullName)
            foreach ($name in @('NextIsland.exe', 'NextIsland.dll', 'Squirrel.exe', 'Next Island_ExecutionStub.exe')) {
                $entry = $archive.GetEntry("lib/app/$name")
                if (-not $entry) { throw "The update package is missing $name." }
                $path = Join-Path $publish "verify-$name"
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $path)
                $signedFiles += $path
            }
            foreach ($file in $signedFiles) {
                $signature = Get-AuthenticodeSignature -LiteralPath $file
                if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $SigningCertificateThumbprint) {
                    throw "$(Split-Path -Leaf $file) does not have a valid signature from the selected certificate."
                }
            }
        }
        finally { $archive.Dispose() }
    }
    $setup
    $package
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
