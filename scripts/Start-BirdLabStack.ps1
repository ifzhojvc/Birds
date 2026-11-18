param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [ValidateSet('Auto', 'Docker', 'WSL')]
    [string]$DockerMode = 'Auto',
    [string]$Distribution = 'BirdLabRuntime',
    [ValidatePattern('^[a-z0-9][a-z0-9_-]*$')]
    [string]$ProjectName = 'birds',
    [string]$EnvFile,
    [string[]]$ComposeOverride = @(),
    [switch]$Build,
    [switch]$CheckOnly
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'BirdLabStackTools.psm1') -Force
$RepoRoot = [IO.Path]::GetFullPath($RepoRoot)
$transitionEnv = Join-Path $RepoRoot 'data\runtime\.env.transition'
if (!$EnvFile) {
    $EnvFile = if (Test-Path -LiteralPath $transitionEnv) { $transitionEnv } else { Join-Path $RepoRoot '.env' }
} elseif (![IO.Path]::IsPathRooted($EnvFile)) { $EnvFile = Join-Path $RepoRoot $EnvFile }
$EnvFile = [IO.Path]::GetFullPath($EnvFile)
if (!(Test-Path -LiteralPath $EnvFile -PathType Leaf)) { throw 'Create local .env with scripts/setup_env.py or supply -EnvFile. Secret files are intentionally excluded from Git.' }
$transition = [string]::Equals($EnvFile, [IO.Path]::GetFullPath($transitionEnv), [StringComparison]::OrdinalIgnoreCase)
if ($DockerMode -eq 'Auto') { $DockerMode = if ($transition) { 'WSL' } else { 'Docker' } }
if ($Build -and $transition) { throw 'Build transition images explicitly after review; this preservation launcher never replaces existing containers.' }
$files = @((Join-Path $RepoRoot 'compose.yaml'), (Join-Path $RepoRoot 'compose.override.yaml'))
if ($transition) { $files += Join-Path $PSScriptRoot 'compose.runtime.yaml' }
foreach ($override in $ComposeOverride) {
    $files += if ([IO.Path]::IsPathRooted($override)) { $override } else { Join-Path $RepoRoot $override }
}
foreach ($file in $files) { if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Required tracked Compose configuration is missing: $file" } }
$executable = $null
$prefix = @()
if ($DockerMode -eq 'WSL') {
    Assert-BirdStackDistribution $Distribution -RequireRunning:$CheckOnly
    if (!$CheckOnly) { & (Join-Path $PSScriptRoot 'Start-BirdLabRuntime.ps1') -Distribution $Distribution -RepoRoot $RepoRoot }
    $linuxRoot = ConvertTo-BirdStackWslPath $Distribution $RepoRoot
    $linuxEnv = ConvertTo-BirdStackWslPath $Distribution $EnvFile
    $linuxFiles = @($files | ForEach-Object { ConvertTo-BirdStackWslPath $Distribution $_ })
    $prefix = @('-d', $Distribution, '-u', 'root', '--exec', '/usr/bin/docker', '--host', 'unix:///var/run/docker.sock')
    $compose = @('compose', '--project-directory', $linuxRoot, '--project-name', $ProjectName, '--env-file', $linuxEnv)
    foreach ($file in $linuxFiles) { $compose += @('-f', $file) }
} else {
    $executable = (Get-Command docker -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $compose = @('compose', '--project-directory', $RepoRoot, '--project-name', $ProjectName, '--env-file', $EnvFile)
    foreach ($file in $files) { $compose += @('-f', $file) }
}
$context = [pscustomobject]@{ Mode = $DockerMode; Executable = $executable; Prefix = $prefix; Compose = $compose }
Invoke-BirdStackCompose $context @('config', '--quiet')
if ($CheckOnly) { return }
$up = @('up', '-d', '--no-recreate')
$up += if ($Build) { '--build' } else { '--no-build' }
Invoke-BirdStackCompose $context $up
Invoke-BirdStackCompose $context @('wait', 'blockchain-bind')
Invoke-BirdStackCompose $context @('ps', '-a')