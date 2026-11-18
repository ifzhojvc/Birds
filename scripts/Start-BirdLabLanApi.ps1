param(
    [Parameter(Mandatory = $true)]
    [string]$TrustedSubnet,
    [string]$BindAddress,
    [ValidateRange(1, 65535)]
    [int]$Port = 5100,
    [ValidateRange(1, 65535)]
    [int]$UpstreamPort = 5100,
    [switch]$AllowPublicProfile
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'BirdLabLanApi.Common.ps1')
$context = Get-BirdLanContext
$config = Get-BirdLanConfig $context $TrustedSubnet $BindAddress $Port $UpstreamPort
$addresses = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop | Where-Object {
    if ([int]$_.AddressState -ne 4 -or ($BindAddress -and $_.IPAddress -ne $BindAddress)) { return $false }
    try {
        $bytes = [Net.IPAddress]::Parse($_.IPAddress).GetAddressBytes()
        $number = [double]$bytes[0] * 16777216 + [double]$bytes[1] * 65536 + [double]$bytes[2] * 256 + $bytes[3]
        return $number -ge $config.subnet.networkNumber -and $number -le $config.subnet.broadcastNumber
    }
    catch { return $false }
})
if ($addresses.Count -ne 1) { throw 'Specify one active -BindAddress inside TrustedSubnet; automatic LAN selection is missing or ambiguous.' }
$BindAddress = [string]$addresses[0].IPAddress
$config = Get-BirdLanConfig $context $TrustedSubnet $BindAddress $Port $UpstreamPort
$profiles = @(Get-NetConnectionProfile -InterfaceIndex $addresses[0].InterfaceIndex -ErrorAction Stop)
if ($profiles.Count -ne 1) { throw 'The selected LAN interface must have one unambiguous network profile.' }
$category = [string]$profiles[0].NetworkCategory
if ($category -eq 'Public' -and !$AllowPublicProfile) { throw 'Public network profile requires explicit -AllowPublicProfile and an explicitly trusted -TrustedSubnet; no listener was started.' }
if ($category -notin @('Private', 'Public')) { throw 'Only a trusted Private or explicitly approved Public LAN profile is supported.' }
$firewall = Get-NetFirewallProfile -PolicyStore ActiveStore -Name $category -ErrorAction Stop
if ([int]$firewall.Enabled -ne 1 -or [int]$firewall.DefaultInboundAction -ne 4) { throw 'Windows Firewall must remain enabled with default inbound Block; no settings were changed.' }
$mutex = [Threading.Mutex]::new($false, $context.MutexName)
$ownsMutex = $false
try {
    try { $ownsMutex = $mutex.WaitOne([TimeSpan]::FromSeconds(20)) }
    catch [Threading.AbandonedMutexException] { $ownsMutex = $true }
    if (!$ownsMutex) { throw 'Another manual LAN operation is in progress.' }
    Add-Type -AssemblyName System.Net.Http
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.UseProxy = $false
    $http = [Net.Http.HttpClient]::new($handler)
    $http.Timeout = [TimeSpan]::FromSeconds(3)
    try {
        $response = $http.GetAsync("http://127.0.0.1:$UpstreamPort/api/health/live").GetAwaiter().GetResult()
        try {
            $live = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
            if ([int]$response.StatusCode -ne 200 -or $live.central -ne $true) { throw 'The loopback Bird Lab API is not live; no LAN listener was started.' }
        }
        finally { $response.Dispose() }
    }
    finally { $http.Dispose() }
    $state = Read-BirdLanState $context
    $existing = Get-BirdLanProcess $state
    if ($null -ne $existing) {
        if ($state.BindAddress -ne $BindAddress -or $state.TrustedSubnet -ne $config.trustedSubnet -or $state.Port -ne $Port -or $state.UpstreamPort -ne $UpstreamPort) { throw 'A different manual LAN configuration is already running; stop it explicitly first.' }
        $binding = @(Get-NetTCPConnection -State Listen -LocalAddress $BindAddress -LocalPort $Port -ErrorAction SilentlyContinue | Where-Object { $_.OwningProcess -eq $existing.ProcessId })
        if ($binding.Count -ne 1) { throw 'The recorded process does not own its approved LAN listener.' }
        [pscustomobject]@{ Status = 'AlreadyRunning'; Pid = [int]$existing.ProcessId; BindAddress = $BindAddress; Port = $Port; TrustedSubnet = $config.trustedSubnet; NetworkProfile = $category; FirewallChanged = $false; AutomaticStartup = $false } | ConvertTo-Json -Compress
        return
    }
    $occupied = @(Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue | Where-Object { $_.LocalAddress -in @($BindAddress, '0.0.0.0', '::') })
    if ($occupied.Count) { throw 'Another process owns the requested LAN address; it was not stopped or replaced.' }
    New-Item -ItemType Directory -Path $context.RuntimeRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $context.LogRoot -Force | Out-Null
    $stdinPath = Join-Path $context.RuntimeRoot 'lan-api-proxy.stdin'
    [IO.File]::WriteAllBytes($stdinPath, [byte[]]@())
    $state = [pscustomobject]@{
        SchemaVersion = 1
        ProcessId = 0
        ProcessStartedUtc = ''
        NodePath = $context.NodePath
        ProxyPath = $context.ProxyPath
        BindAddress = $BindAddress
        TrustedSubnet = $config.trustedSubnet
        Port = $Port
        UpstreamPort = $UpstreamPort
    }
    $arguments = @('"' + $context.ProxyPath + '"') + @(Get-BirdLanArguments $state)
    $proxy = Start-Process -FilePath $context.NodePath -ArgumentList $arguments -WorkingDirectory $context.RepoRoot -WindowStyle Hidden -RedirectStandardInput $stdinPath -RedirectStandardOutput (Join-Path $context.LogRoot 'proxy.stdout.log') -RedirectStandardError (Join-Path $context.LogRoot 'proxy.stderr.log') -PassThru
    try {
        $null = $proxy.SafeHandle
        $state.ProcessId = $proxy.Id
        $state.ProcessStartedUtc = $proxy.StartTime.ToUniversalTime().ToString('o')
        Start-Sleep -Seconds 2
        $proxy.Refresh()
        if ($proxy.HasExited) { throw 'The manual LAN proxy exited before becoming ready.' }
        $created = Get-BirdLanProcess $state
        $binding = @(Get-NetTCPConnection -State Listen -LocalAddress $BindAddress -LocalPort $Port -ErrorAction SilentlyContinue | Where-Object { $_.OwningProcess -eq $proxy.Id })
        if ($null -eq $created -or $binding.Count -ne 1) { throw 'The new process did not own its approved LAN listener.' }
        Write-BirdLanState $context $state
        [pscustomobject]@{ Status = 'Started'; Pid = $proxy.Id; BindAddress = $BindAddress; Port = $Port; TrustedSubnet = $config.trustedSubnet; NetworkProfile = $category; FirewallChanged = $false; AutomaticStartup = $false } | ConvertTo-Json -Compress
    }
    catch {
        if (!$proxy.HasExited) {
            $created = Get-CimInstance Win32_Process -Filter "ProcessId = $($proxy.Id)" -ErrorAction SilentlyContinue
            if (Test-BirdLanProcessIdentity $created $state ($proxy.StartTime.ToUniversalTime().ToString('o'))) { $proxy.Kill(); $null = $proxy.WaitForExit(5000) }
        }
        throw
    }
    finally { $proxy.Dispose() }
}
finally {
    if ($ownsMutex) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
