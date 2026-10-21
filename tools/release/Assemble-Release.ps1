[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][string]$HelperPath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$RuntimeLicenseDirectory
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$publish = (Resolve-Path -LiteralPath $PublishDirectory).Path
$helper = (Resolve-Path -LiteralPath $HelperPath).Path
$licenses = (Resolve-Path -LiteralPath $RuntimeLicenseDirectory).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
$zip = "$output.zip"
if ((Test-Path -LiteralPath $output) -or (Test-Path -LiteralPath $zip)) {
    throw 'Choose a new output directory and ZIP path; existing packages are never deleted.'
}
if ($output.StartsWith($publish.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The release directory must not be inside the publish input.'
}
if (-not (Test-Path -LiteralPath (Join-Path $publish 'desk_Puff.exe') -PathType Leaf)) {
    throw 'The publish input must contain desk_Puff.exe.'
}
$copies = @()
$excluded = @()
foreach ($directory in Get-ChildItem -LiteralPath $publish -Recurse -Directory) {
    if (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Reparse-point input directories are not supported.' }
}
foreach ($file in Get-ChildItem -LiteralPath $publish -Recurse -File) {
    if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Reparse-point publish inputs are not supported.'
    }
    $relative = [IO.Path]::GetRelativePath($publish, $file.FullName)
    if ($file.Extension -in '.pdb', '.log', '.trx', '.ps1' -or $file.Name -eq 'release-manifest.json') {
        $excluded += [ordered]@{ path = $relative; reason = 'Build symbols, developer tools or diagnostic output; not needed to run the app.' }
        continue
    }
    if ($file.Extension -eq '.json' -and $file.Name -notmatch '\.(deps|runtimeconfig)\.json$') {
        throw 'Unexpected JSON in publish input; local profiles and captures must not enter a release.'
    }
    if ($relative -eq 'ble\desk-puff-ble.exe') {
        if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $helper).Hash) { throw 'The published helper differs from the supplied helper.' }
        continue
    }
    if ($relative -in 'LICENSE.md', 'NOTICE', 'THIRD_PARTY_NOTICES.md' -or $relative.StartsWith('licenses\', [StringComparison]::OrdinalIgnoreCase)) { continue }
    $copies += @{ source = $file.FullName; path = $relative; reason = 'Published runtime file.' }
}
$copies += @{ source = $helper; path = 'ble\desk-puff-ble.exe'; reason = 'Required fixed-path Bluetooth transport helper.' }
foreach ($name in 'LICENSE.md', 'NOTICE', 'THIRD_PARTY_NOTICES.md') {
    $copies += @{ source = (Join-Path $repo $name); path = $name; reason = 'Project license or third-party attribution.' }
}
foreach ($name in 'LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT') {
    $copies += @{ source = (Join-Path $licenses $name); path = "licenses\dotnet\$name"; reason = 'Bundled .NET runtime license or notices.' }
}
# Retain supplied notices and package attribution from the locked application
# dependencies. This includes available native Skia/HarfBuzz license texts.
$packageCache = Split-Path -Parent (Split-Path -Parent $licenses)
$lock = Get-Content -LiteralPath (Join-Path $repo 'src/DeskPuff.App/packages.lock.json') -Raw | ConvertFrom-Json
foreach ($entry in $lock.dependencies.'net10.0'.PSObject.Properties) {
    if ($entry.Value.type -eq 'Project') { continue }
    $id = $entry.Name.ToLowerInvariant()
    $version = $entry.Value.resolved
    $package = Join-Path $packageCache "$id/$version"
    foreach ($file in Get-ChildItem -LiteralPath $package -File) {
        if ($file.Extension -eq '.nuspec' -or $file.Name -match '^(LICENSE|THIRD-PARTY-NOTICES|NOTICE|COPYING|OFL)(\.|$)') {
            $copies += @{ source = $file.FullName; path = "licenses/nuget/$id/$version/$($file.Name)"; reason = 'Locked application dependency attribution or supplied license text.' }
        }
    }
}
$seen = @{}
foreach ($copy in $copies) {
    if (-not (Test-Path -LiteralPath $copy.source -PathType Leaf)) { throw "Missing release input: $($copy.path)" }
    if ($seen.ContainsKey($copy.path)) { throw "Duplicate release path: $($copy.path)" }
    $seen[$copy.path] = $true
}
New-Item -ItemType Directory -Path $output | Out-Null
$included = @()
foreach ($copy in $copies) {
    $destination = Join-Path $output $copy.path
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
    Copy-Item -LiteralPath $copy.source -Destination $destination
    $file = Get-Item -LiteralPath $destination
    $included += [ordered]@{ path = $copy.path; bytes = $file.Length; sha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant(); reason = $copy.reason }
}
$manifest = [ordered]@{ format = 1; platform = 'win-x64'; selfContained = $true; included = @($included); excluded = @($excluded) }
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'release-manifest.json') -Encoding utf8
[IO.Compression.ZipFile]::CreateFromDirectory($output, $zip, [IO.Compression.CompressionLevel]::SmallestSize, $false)
$files = @(Get-ChildItem -LiteralPath $output -Recurse -File)
[ordered]@{ files = $files.Count; folderBytes = ($files | Measure-Object Length -Sum).Sum; zipBytes = (Get-Item -LiteralPath $zip).Length; zipSha256 = (Get-FileHash -LiteralPath $zip).Hash.ToLowerInvariant() } | ConvertTo-Json
