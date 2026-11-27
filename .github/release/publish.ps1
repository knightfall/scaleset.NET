param([Parameter(Mandatory)][string]$ReleaseDirectory)
$ErrorActionPreference = 'Stop'
$policy = Get-Content "$PSScriptRoot/policy.json" -Raw | ConvertFrom-Json
$tag = "v$($policy.version)"
$manifestFile = Join-Path $ReleaseDirectory 'release.json'

function Assert-ExactReleaseAsset([string]$FilePath) {
    $name = Split-Path $FilePath -Leaf
    $previous = Join-Path $env:RUNNER_TEMP ('previous-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $previous | Out-Null
    gh release download $tag --repo $policy.repository --pattern $name --dir $previous
    if ($LASTEXITCODE -ne 0) { throw "Cannot verify existing release asset: $name" }
    $old = Join-Path $previous $name
    if ((Get-FileHash $old -Algorithm SHA256).Hash -ne (Get-FileHash $FilePath -Algorithm SHA256).Hash) {
        throw "Existing release asset differs from the candidate: $name. Rerun failed jobs or pass the original candidate_run_id."
    }
}

function Add-ReleaseAsset([string]$FilePath) {
    $name = Split-Path $FilePath -Leaf
    if ($script:releaseAssets -contains $name) {
        Assert-ExactReleaseAsset $FilePath
        return
    }

    gh release upload $tag $FilePath --repo $policy.repository
    if ($LASTEXITCODE -ne 0) { throw "Cannot attach release evidence: $name" }
    $script:releaseAssets += $name
}

# A symbol receipt is meaningful only when the current draft already identifies this candidate.
$releaseAssets = @()
gh release view $tag --repo $policy.repository *> $null
$releaseExists = $LASTEXITCODE -eq 0
if ($releaseExists) {
    $details = gh release view $tag --repo $policy.repository --json assets
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect existing release assets' }
    $releaseAssets = @(($details | ConvertFrom-Json).assets | ForEach-Object { $_.name })
    if ($releaseAssets -contains 'release.json') {
        Assert-ExactReleaseAsset $manifestFile
    } elseif ($releaseAssets -like '*.snupkg') {
        throw "Release $tag has symbol assets without a candidate manifest."
    }
}

$confirmedSymbols = @()
foreach ($id in $policy.packages) {
    $symbols = Join-Path $ReleaseDirectory "packages/$id.$($policy.version).snupkg"
    if ($releaseAssets -contains (Split-Path $symbols -Leaf)) {
        Assert-ExactReleaseAsset $symbols
        $confirmedSymbols += $id
    }
}

$existingPath = Join-Path $env:RUNNER_TEMP 'existing-packages.json'
python3 "$PSScriptRoot/check_existing.py" $ReleaseDirectory $existingPath
if ($LASTEXITCODE -ne 0) { throw 'Existing registry versions conflict with the verified candidate or could not be checked' }
$existing = Get-Content $existingPath -Raw | ConvertFrom-Json
if (!$releaseExists) {
    $notesFile = "docs/releases/$($policy.version).md"
    if (Test-Path $notesFile) {
        gh release create $tag --repo $policy.repository --verify-tag --draft --prerelease --title $tag --notes-file $notesFile
    } else {
        gh release create $tag --repo $policy.repository --verify-tag --draft --prerelease --title $tag --generate-notes
    }
    if ($LASTEXITCODE -ne 0) { throw 'Cannot create draft release' }
}
Add-ReleaseAsset $manifestFile

foreach ($id in $policy.packages) {
    $package = Join-Path $ReleaseDirectory "packages/$id.$($policy.version).nupkg"
    # Only duplicates already compared with this candidate may be skipped.
    # A new or not-yet-visible conflict must fail before subsequent uploads.
    $duplicateOption = @()
    if ($existing.NuGet -contains $id) { $duplicateOption = @('--skip-duplicate') }
    dotnet nuget push $package --source https://api.nuget.org/v3/index.json @duplicateOption --no-symbols
    if ($LASTEXITCODE -ne 0) { throw "NuGet publication failed: $id" }
    if ($confirmedSymbols -notcontains $id) {
        $symbols = Join-Path $ReleaseDirectory "packages/$id.$($policy.version).snupkg"
        $previousSymbolKey = $env:NUGET_SYMBOL_API_KEY
        try {
            # A package duplicate does not establish symbol identity.
            $env:NUGET_SYMBOL_API_KEY = $env:NUGET_API_KEY
            dotnet nuget push $symbols --source https://api.nuget.org/v3/index.json
            if ($LASTEXITCODE -ne 0) { throw "NuGet symbol publication failed: $id" }
        } finally {
            $env:NUGET_SYMBOL_API_KEY = $previousSymbolKey
        }
        # Attaching the original archive records acknowledgement for this exact symbol payload.
        Add-ReleaseAsset $symbols
    }
    $duplicateOption = @()
    if ($existing.GitHub -contains $id) { $duplicateOption = @('--skip-duplicate') }
    $nugetKey = $env:NUGET_API_KEY
    try {
        $env:NUGET_API_KEY = $env:GH_TOKEN
        dotnet nuget push $package --source "https://nuget.pkg.github.com/knightfall/index.json" @duplicateOption --no-symbols
        if ($LASTEXITCODE -ne 0) { throw "GitHub Packages publication failed: $id" }
    } finally {
        $env:NUGET_API_KEY = $nugetKey
    }
}
$assets = Join-Path $env:RUNNER_TEMP 'release-assets'
if (Test-Path $assets) { Remove-Item -Recurse -Force $assets }
New-Item -ItemType Directory -Path $assets | Out-Null
Copy-Item "$ReleaseDirectory/packages/*.nupkg" $assets
Copy-Item "$ReleaseDirectory/_manifest/spdx_2.2/manifest.spdx.json" "$assets/build.spdx.json"
foreach ($bundle in @(@('provenance', $env:PROVENANCE_BUNDLE), @('build-sbom', $env:BUILD_SBOM_BUNDLE))) {
    $digest = (Get-FileHash $bundle[1] -Algorithm SHA256).Hash.ToLowerInvariant()
    Copy-Item $bundle[1] (Join-Path $assets "$($bundle[0])-$digest.sigstore.json")
}
python3 "$PSScriptRoot/release_archive.py" $ReleaseDirectory "$assets/release-evidence.zip"
if ($LASTEXITCODE -ne 0) { throw 'Cannot archive the complete release evidence' }
foreach ($file in Get-ChildItem $assets -File) {
    Add-ReleaseAsset $file.FullName
}
