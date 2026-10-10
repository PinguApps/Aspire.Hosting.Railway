param(
    [string] $WorkDirectory,
    [string] $ProofVersion = "v1",
    [string] $PackageVersion = "1.1.0"
)

$ErrorActionPreference = "Stop"
foreach ($name in @("LIVE_RAILWAY_PROJECT_ID", "LIVE_RAILWAY_ENVIRONMENT_ID", "LIVE_RAILWAY_TOKEN", "LIVE_SITE_KEY", "LIVE_DASHBOARD_BROWSER_TOKEN", "LIVE_DASHBOARD_OTLP_TOKEN")) {
    if (-not [Environment]::GetEnvironmentVariable($name)) { throw "Missing live fixture input: $name" }
}
if ($PackageVersion -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw "PackageVersion must be a semantic version." }
$repoRoot = Split-Path -Parent $PSScriptRoot
$packages = Join-Path $repoRoot "artifacts/live-source-builds/packages"
New-Item -ItemType Directory -Path $packages -Force | Out-Null
dotnet pack (Join-Path $repoRoot "src/Aspire.Hosting.Railway/Aspire.Hosting.Railway.csproj") -c Release "-p:Version=$PackageVersion" -o $packages
if ($LASTEXITCODE -ne 0) { throw "Packing the local Railway package failed." }

if (-not $WorkDirectory) {
    $allocation = "$env:LIVE_RAILWAY_PROJECT_ID-$env:LIVE_RAILWAY_ENVIRONMENT_ID"
    if ($allocation -notmatch '^[0-9a-fA-F-]+$') { throw "Use an explicit WorkDirectory for non-GUID allocation IDs." }
    $WorkDirectory = Join-Path ([IO.Path]::GetTempPath()) "pinguapps-railway-source-$allocation"
}
$work = [IO.Path]::GetFullPath($WorkDirectory)
$repo = [IO.Path]::GetFullPath($repoRoot)
if ($work -eq $repo -or $work.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "The live fixture must run outside the repository so credentials and generated files remain external."
}
$marker = Join-Path $work ".pinguapps-railway-source-fixture"
if ((Test-Path -LiteralPath $work) -and -not (Test-Path -LiteralPath $marker)) {
    throw "WorkDirectory already exists and is not this disposable live fixture."
}
New-Item -ItemType Directory -Path $work -Force | Out-Null
Set-Content -LiteralPath $marker -Value "Railway source build fixture" -Encoding UTF8
$contexts = [IO.Path]::GetFullPath((Join-Path $work "Contexts"))
if (-not $contexts.StartsWith($work + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Fixture context cleanup escaped WorkDirectory."
}
Remove-Item -LiteralPath $contexts -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item -Path (Join-Path $PSScriptRoot "SourceBuildFixture/*") -Destination $work -Recurse -Force
$project = Join-Path $work "SourceBuildFixture.csproj"
$source = Get-Content -LiteralPath $project -Raw
$source = $source.Replace('Include="PinguApps.Aspire.Hosting.Railway" Version="1.1.0"', "Include=`"PinguApps.Aspire.Hosting.Railway`" Version=`"$PackageVersion`"")
Set-Content -LiteralPath $project -Value $source -Encoding UTF8
$feed = [System.Security.SecurityElement]::Escape([IO.Path]::GetFullPath($packages))
@"
<configuration>
  <packageSources><clear /><add key="local" value="$feed" /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources>
  <packageSourceMapping><packageSource key="local"><package pattern="PinguApps.Aspire.Hosting.Railway" /></packageSource><packageSource key="nuget.org"><package pattern="*" /></packageSource></packageSourceMapping>
</configuration>
"@ | Set-Content -LiteralPath (Join-Path $work "NuGet.Config") -Encoding UTF8
$previousPackages = $env:NUGET_PACKAGES
$previousProof = $env:LIVE_PROOF_VERSION
$env:NUGET_PACKAGES = Join-Path $work (".nuget-packages-" + [Guid]::NewGuid().ToString("N"))
$env:LIVE_PROOF_VERSION = $ProofVersion
Push-Location $work
try {
    dotnet restore $project --no-cache
    if ($LASTEXITCODE -ne 0) { throw "Live fixture restore failed." }
    aspire deploy --non-interactive
    if ($LASTEXITCODE -ne 0) { throw "Live Railway source deployment failed. Keep its Aspire state for exact-request recovery." }
}
finally {
    $env:NUGET_PACKAGES = $previousPackages
    $env:LIVE_PROOF_VERSION = $previousProof
    Pop-Location
}
