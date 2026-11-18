function Get-BirdStackDistributions([switch]$Running) {
    $arguments = @('--list', '--quiet')
    if ($Running) { $arguments += '--running' }
    $output = & wsl.exe @arguments
    if ($LASTEXITCODE -ne 0) { throw 'WSL distribution inventory failed.' }
    return @($output | ForEach-Object { ([string]$_).Replace([string][char]0, '').Trim() } | Where-Object { $_ })
}
function Assert-BirdStackDistribution([string]$Distribution, [switch]$RequireRunning) {
    if ($Distribution -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]*$' -or $Distribution -match '^docker-desktop($|-)') { throw 'Select an existing application WSL distribution, not Docker Desktop storage.' }
    if (@(Get-BirdStackDistributions) -notcontains $Distribution) { throw "WSL distribution '$Distribution' is not installed. Use Docker mode or install your own runtime separately." }
    if ($RequireRunning -and @(Get-BirdStackDistributions -Running) -notcontains $Distribution) { throw 'CheckOnly does not start a stopped WSL distribution.' }
}
function ConvertTo-BirdStackWslPath([string]$Distribution, [string]$Path) {
    $value = & wsl.exe -d $Distribution -u root --exec /usr/bin/wslpath -a $Path
    if ($LASTEXITCODE -ne 0 -or @($value).Count -ne 1) { throw 'The repository path is not accessible from the selected WSL runtime.' }
    return ([string]$value).Trim()
}
function Invoke-BirdStackCompose($Context, [string[]]$Arguments) {
    if ($Context.Mode -eq 'WSL') {
        & wsl.exe @($Context.Prefix) @($Context.Compose) @Arguments
    } else {
        & $Context.Executable @($Context.Compose) @Arguments
    }
    if ($LASTEXITCODE -ne 0) { throw "Docker Compose failed with exit code $LASTEXITCODE." }
}
Export-ModuleMember -Function Get-BirdStackDistributions, Assert-BirdStackDistribution, ConvertTo-BirdStackWslPath, Invoke-BirdStackCompose