param(
    [string] $Configuration = "Release",
    [string] $PackageVersion = "9999.0.0"
)

$ErrorActionPreference = "Stop"
if ($PackageVersion -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
    throw "PackageVersion must be a semantic version; paths are not permitted."
}

function Remove-SafeArtifact {
    param([string] $Path)
    $target = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetFullPath($artifactsRoot)
    if ($target -ne $root -and -not $target.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Artifact removal escaped its intended directory."
    }
    Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction SilentlyContinue
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $repoRoot "Aspire.Hosting.Railway.slnx"
$fixtureSource = Join-Path $repoRoot "tests/Aspire.Hosting.Railway/Fixtures/TypeScriptAppHost"
$artifactsRoot = Join-Path $repoRoot "artifacts/typescript-apphost-package"
$packageOutput = Join-Path $artifactsRoot "packages"
$fixtureWork = Join-Path $artifactsRoot "fixture"
$nugetPackages = Join-Path $artifactsRoot ".nuget-packages"
$packageId = "PinguApps.Aspire.Hosting.Railway"

Remove-SafeArtifact $artifactsRoot
New-Item $packageOutput -ItemType Directory -Force | Out-Null
New-Item $nugetPackages -ItemType Directory -Force | Out-Null

dotnet restore $solutionPath
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed." }
dotnet build $solutionPath -c $Configuration --no-restore -p:ContinuousIntegrationBuild=true
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed." }
dotnet pack $solutionPath -c $Configuration --no-build -p:Version=$PackageVersion -o $packageOutput
if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed." }

$packageFile = Join-Path $packageOutput "$packageId.$PackageVersion.nupkg"
$packageCacheId = $packageId.ToLowerInvariant()
$packageCachePath = Join-Path $nugetPackages "$packageCacheId/$PackageVersion"

Remove-SafeArtifact $packageCachePath
New-Item $packageCachePath -ItemType Directory -Force | Out-Null

Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::ExtractToDirectory($packageFile, $packageCachePath)
Copy-Item $packageFile (Join-Path $packageCachePath "$packageCacheId.$PackageVersion.nupkg")

$packageBytes = [IO.File]::ReadAllBytes($packageFile)
$packageHash = [Convert]::ToBase64String([System.Security.Cryptography.SHA512]::HashData($packageBytes))

Set-Content (Join-Path $packageCachePath "$packageCacheId.$PackageVersion.nupkg.sha512") $packageHash -Encoding ASCII

[ordered]@{
    version = 2
    contentHash = $packageHash
    source = (Resolve-Path $packageOutput).Path
} | ConvertTo-Json | Set-Content (Join-Path $packageCachePath ".nupkg.metadata") -Encoding UTF8

Copy-Item $fixtureSource $fixtureWork -Recurse
Remove-SafeArtifact (Join-Path $fixtureWork ".aspire")
Remove-SafeArtifact (Join-Path $fixtureWork ".modules")
Remove-SafeArtifact (Join-Path $fixtureWork "node_modules")

$aspireConfigPath = Join-Path $fixtureWork "aspire.config.json"
$aspireConfig = Get-Content $aspireConfigPath -Raw | ConvertFrom-Json
$aspireConfig.packages.$packageId = $PackageVersion
$aspireConfig | ConvertTo-Json -Depth 10 | Set-Content $aspireConfigPath -Encoding UTF8

$packageOutputFullPath = (Resolve-Path $packageOutput).Path
@"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local-package-gate" value="$packageOutputFullPath" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local-package-gate">
      <package pattern="PinguApps.Aspire.Hosting.Railway" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
"@ | Set-Content (Join-Path $fixtureWork "NuGet.Config") -Encoding UTF8

Push-Location $fixtureWork
try {
    $previousNuGetPackages = $env:NUGET_PACKAGES
    $env:NUGET_PACKAGES = $nugetPackages

    aspire restore --non-interactive
    if ($LASTEXITCODE -ne 0) { throw "aspire restore failed." }
    npm ci --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw "npm ci failed." }
    npm run typecheck
    if ($LASTEXITCODE -ne 0) { throw "TypeScript typecheck failed." }
    aspire publish --non-interactive --list-steps
    if ($LASTEXITCODE -ne 0) { throw "Aspire publish plan failed." }
    aspire deploy --non-interactive --list-steps
    if ($LASTEXITCODE -ne 0) { throw "Aspire deploy plan failed." }
}
finally {
    if ($null -eq $previousNuGetPackages) {
        Remove-Item Env:NUGET_PACKAGES -ErrorAction SilentlyContinue
    }
    else {
        $env:NUGET_PACKAGES = $previousNuGetPackages
    }

    Pop-Location
}
