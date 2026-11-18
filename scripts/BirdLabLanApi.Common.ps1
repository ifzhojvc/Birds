function Get-BirdLanContext {
    $repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
    $runtimeRoot = Join-Path $repoRoot 'data\runtime'
    $nodePath = (Get-Command node.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $hash = [Security.Cryptography.SHA256]::Create()
    try { $identity = [BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($repoRoot.ToUpperInvariant()))).Replace('-', '').Substring(0, 24) }
    finally { $hash.Dispose() }
    return [pscustomobject]@{
        RepoRoot = $repoRoot
        RuntimeRoot = $runtimeRoot
        ProxyPath = Join-Path $PSScriptRoot 'birdlab-lan-api-proxy.cjs'
        NodePath = $nodePath
        StatePath = Join-Path $runtimeRoot 'lan-api-proxy.state.json'
        LogRoot = Join-Path $repoRoot '.test-artifacts\lan-api'
        MutexName = 'Local\BirdLabManualLanApi.' + $identity
    }
}

function Get-BirdLanConfig($Context, [string]$TrustedSubnet, [string]$BindAddress, [int]$Port, [int]$UpstreamPort) {
    $arguments = @($Context.ProxyPath, '--trusted-subnet', $TrustedSubnet, '--port', [string]$Port, '--upstream-port', [string]$UpstreamPort, '--check-config')
    if ($BindAddress) { $arguments += @('--bind', $BindAddress) }
    $result = & $Context.NodePath @arguments
    if ($LASTEXITCODE -ne 0) { throw 'The explicitly trusted private LAN configuration is invalid.' }
    return $result | ConvertFrom-Json
}

function Get-BirdLanArguments($State) {
    return @('--bind', [string]$State.BindAddress, '--trusted-subnet', [string]$State.TrustedSubnet, '--port', [string]$State.Port, '--upstream-port', [string]$State.UpstreamPort, '--manual')
}

function Test-BirdLanProcessIdentity($Process, $State, [string]$StartedUtc) {
    if ($null -eq $Process -or [int]$Process.ProcessId -ne [int]$State.ProcessId -or $Process.Name -ne 'node.exe' -or !$StartedUtc) { return $false }
    if (![string]::Equals([string]$Process.ExecutablePath, [string]$State.NodePath, [StringComparison]::OrdinalIgnoreCase)) { return $false }
    if ($StartedUtc -cne [string]$State.ProcessStartedUtc) { return $false }
    $pattern = '(?i)^\s*"?' + [regex]::Escape([string]$State.NodePath) + '"?\s+"?' + [regex]::Escape([string]$State.ProxyPath) + '"?'
    foreach ($argument in @(Get-BirdLanArguments $State)) { $pattern += '\s+' + [regex]::Escape($argument) }
    return [string]$Process.CommandLine -match ($pattern + '\s*$')
}

function Read-BirdLanState($Context) {
    if (!(Test-Path -LiteralPath $Context.StatePath)) { return $null }
    $jsonOptions = @{}
    if ((Get-Command ConvertFrom-Json -CommandType Cmdlet).Parameters.ContainsKey('DateKind')) { $jsonOptions.DateKind = 'String' }
    $state = [IO.File]::ReadAllText($Context.StatePath) | ConvertFrom-Json @jsonOptions
    if ($state.ProcessStartedUtc -is [DateTime]) { $state.ProcessStartedUtc = $state.ProcessStartedUtc.ToUniversalTime().ToString('o') }
    if ($state.SchemaVersion -ne 1 -or [int]$state.ProcessId -le 0 -or !$state.ProcessStartedUtc) { throw 'Manual LAN process metadata is invalid; no process was changed.' }
    if (![string]::Equals([string]$state.ProxyPath, $Context.ProxyPath, [StringComparison]::OrdinalIgnoreCase)) { throw 'Manual LAN metadata belongs to a different launcher; no process was changed.' }
    if (![IO.Path]::IsPathRooted([string]$state.NodePath) -or [IO.Path]::GetFileName([string]$state.NodePath) -ne 'node.exe') { throw 'Manual LAN metadata has an invalid executable; no process was changed.' }
    $null = Get-BirdLanConfig $Context ([string]$state.TrustedSubnet) ([string]$state.BindAddress) ([int]$state.Port) ([int]$state.UpstreamPort)
    return $state
}

function Get-BirdLanProcess($State) {
    if ($null -eq $State) { return $null }
    $actual = Get-CimInstance Win32_Process -Filter "ProcessId = $([int]$State.ProcessId)" -ErrorAction SilentlyContinue
    if ($null -eq $actual) { return $null }
    $process = [Diagnostics.Process]::GetProcessById([int]$State.ProcessId)
    try {
        $null = $process.SafeHandle
        if (!(Test-BirdLanProcessIdentity $actual $State ($process.StartTime.ToUniversalTime().ToString('o')))) { throw 'The recorded PID identity changed; no process was changed.' }
        return $actual
    }
    finally { $process.Dispose() }
}

function Write-BirdLanState($Context, $State) {
    $temporary = $Context.StatePath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllText($temporary, ($State | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporary -Destination $Context.StatePath -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
    }
}
