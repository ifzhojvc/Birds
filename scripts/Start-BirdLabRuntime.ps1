param(
    [string]$Distribution = 'BirdLabRuntime',
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [switch]$CheckOnly
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'BirdLabStackTools.psm1') -Force
Assert-BirdStackDistribution $Distribution -RequireRunning:$CheckOnly
if ($CheckOnly) { return }
$RepoRoot = [IO.Path]::GetFullPath($RepoRoot)
$pattern = '(?i)(?:^|\s)-d\s+"?' + [regex]::Escape($Distribution) + '"?\s+-u\s+root\s+--exec\s+/bin/sleep\s+infinity\s*$'
$matching = @(Get-CimInstance Win32_Process -Filter "Name = 'wsl.exe'" | Where-Object { $_.CommandLine -match $pattern })
$roots = @($matching | Where-Object { $matching.ProcessId -notcontains $_.ParentProcessId })
if ($roots.Count -gt 1) { throw 'Multiple independent WSL keepalives require inspection; no process was changed.' }
$runtime = Join-Path $RepoRoot 'data\runtime'
New-Item -ItemType Directory -Path $runtime -Force | Out-Null
$pidPath = Join-Path $runtime ($Distribution + '-keepalive.pid')
if ($roots.Count -eq 1) {
    [IO.File]::WriteAllText($pidPath, [string]$roots[0].ProcessId, [Text.UTF8Encoding]::new($false))
    return
}
$keeper = Start-Process -FilePath "$env:SystemRoot\System32\wsl.exe" -ArgumentList @('-d', $Distribution, '-u', 'root', '--exec', '/bin/sleep', 'infinity') -WindowStyle Hidden -PassThru
Start-Sleep -Seconds 3
$keeper.Refresh()
if ($keeper.HasExited) { throw 'The selected WSL runtime did not remain running.' }
[IO.File]::WriteAllText($pidPath, [string]$keeper.Id, [Text.UTF8Encoding]::new($false))