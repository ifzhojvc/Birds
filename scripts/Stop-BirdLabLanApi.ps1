param(
    [string]$BindAddress,
    [int]$Port = 0
)
$ErrorActionPreference = 'Stop'
if ($PSBoundParameters.ContainsKey('Port') -and ($Port -lt 1 -or $Port -gt 65535)) { throw 'Port must be from 1 to 65535.' }
. (Join-Path $PSScriptRoot 'BirdLabLanApi.Common.ps1')
$context = Get-BirdLanContext
$mutex = [Threading.Mutex]::new($false, $context.MutexName)
$ownsMutex = $false
try {
    try { $ownsMutex = $mutex.WaitOne([TimeSpan]::FromSeconds(20)) }
    catch [Threading.AbandonedMutexException] { $ownsMutex = $true }
    if (!$ownsMutex) { throw 'Another manual LAN operation is in progress.' }
    $state = Read-BirdLanState $context
    if ($null -ne $state) {
        if (($BindAddress -and $BindAddress -ne $state.BindAddress) -or ($Port -and $Port -ne $state.Port)) { throw 'Requested Stop parameters differ from the recorded proxy; no process was changed.' }
    }
    $existing = Get-BirdLanProcess $state
    if ($null -eq $existing) {
        [pscustomobject]@{ Status = 'NotRunning'; Stopped = $false; UnrelatedProcessesChanged = $false } | ConvertTo-Json -Compress
        return
    }
    $process = [Diagnostics.Process]::GetProcessById([int]$state.ProcessId)
    try {
        $null = $process.SafeHandle
        $rechecked = Get-CimInstance Win32_Process -Filter "ProcessId = $($process.Id)" -ErrorAction SilentlyContinue
        if (!(Test-BirdLanProcessIdentity $rechecked $state ($process.StartTime.ToUniversalTime().ToString('o')))) { throw 'The PID identity changed; no process was stopped.' }
        $stoppedId = $process.Id
        $process.Kill()
        if (!$process.WaitForExit(5000)) { throw 'The validated manual proxy did not stop in time.' }
    }
    finally { $process.Dispose() }
    if (@(Get-NetTCPConnection -State Listen -LocalAddress $state.BindAddress -LocalPort $state.Port -ErrorAction SilentlyContinue | Where-Object { $_.OwningProcess -eq $stoppedId }).Count) { throw 'The stopped process still owns a LAN listener.' }
    $current = Read-BirdLanState $context
    if ($current.ProcessId -eq $state.ProcessId -and $current.ProcessStartedUtc -ceq $state.ProcessStartedUtc) { Remove-Item -LiteralPath $context.StatePath }
    [pscustomobject]@{ Status = 'Stopped'; Pid = $stoppedId; BindAddress = $state.BindAddress; Port = $state.Port; UnrelatedProcessesChanged = $false } | ConvertTo-Json -Compress
}
finally {
    if ($ownsMutex) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
