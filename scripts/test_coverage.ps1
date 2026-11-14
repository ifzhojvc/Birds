param(
    [ValidateSet('python', 'dotnet', 'web', 'mobile', 'solidity')]
    [string[]]$Component = @('python', 'dotnet', 'web', 'mobile', 'solidity'),
    [string]$ReportDirectory,
    [string]$DotnetDockerNetwork
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$project = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $project
try {
    if (!$ReportDirectory) {
        $ReportDirectory = 'coverage-reports/run-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff')
    }
    $output = (New-Item -ItemType Directory -Force -Path $ReportDirectory).FullName
    $python = if (Test-Path '.venv/Scripts/python.exe') { (Resolve-Path '.venv/Scripts/python.exe').Path }
    elseif (Test-Path '.venv/bin/python') { (Resolve-Path '.venv/bin/python').Path }
    else { 'python' }

    $failed = @()
    foreach ($name in $Component) {
        $report = Join-Path $output $name
        if (Test-Path -LiteralPath $report) { throw "Report directory already exists: $report" }
        New-Item -ItemType Directory -Path $report | Out-Null
        Write-Host "=== $name ==="
        try {
            switch ($name) {
                'python' {
                    $options = @('-q', '--tb=short', '--cov=birdlab', '--cov=ai-server', '--cov=app', '--cov-branch',
                        "--cov-report=html:$report/html", "--cov-report=json:$report/coverage.json",
                        "--junitxml=$report/tests.xml", "--basetemp=$report/tmp")
                    $runRealModels = $env:RUN_REAL_MODELS
                    try {
                        $env:RUN_REAL_MODELS = '0'
                        & $python -X utf8 -m pytest @options *> "$report/run.log"
                    } finally { $env:RUN_REAL_MODELS = $runRealModels }
                }
                'dotnet' {
                    $resultsPath = if ($DotnetDockerNetwork) { '/reports' } else { $report }
                    $options = @('--settings', 'central-server.Tests/coverage.runsettings', '--collect', 'XPlat Code Coverage',
                        '--results-directory', $resultsPath, '--logger', 'trx;LogFileName=tests.trx')
                    if ($DotnetDockerNetwork) {
                        $sdk = (Get-Content -Raw -LiteralPath 'global.json' | ConvertFrom-Json).sdk.version
                        $copy = (New-Item -ItemType Directory -Force ".test-artifacts/coverage-dotnet-$([guid]::NewGuid().ToString('N'))").FullName
                        $sources = @(git -c core.quotepath=false ls-files --cached --others --exclude-standard -- global.json central-server central-server.Tests)
                        if ($LASTEXITCODE -ne 0 -or !$sources.Count) { throw 'Cannot inventory the .NET sources in Git.' }
                        foreach ($source in ($sources | Sort-Object -Unique)) {
                            $target = Join-Path $copy $source
                            New-Item -ItemType Directory -Force (Split-Path -Parent $target) | Out-Null
                            Copy-Item -LiteralPath $source -Destination $target
                        }
                        $dockerOptions = @('--rm', '--network', $DotnetDockerNetwork,
                            '--env', 'BIRDLAB_TEST_SQL', '--env', 'BIRDLAB_TEST_AMQP', '--env', 'BIRDLAB_TEST_RABBIT_HTTP',
                            '--mount', "type=bind,source=$copy,target=/src", '--mount', "type=bind,source=$report,target=/reports",
                            '--workdir', '/src', "mcr.microsoft.com/dotnet/sdk:$sdk")
                        docker run @dockerOptions dotnet test central-server.Tests/BirdLab.Api.Tests.csproj @options *> "$report/run.log"
                    } else {
                        dotnet test central-server.Tests/BirdLab.Api.Tests.csproj @options *> "$report/run.log"
                    }
                }
                'web' {
                    Push-Location web-app
                    try {
                        $options = @('--coverage', "--coverage.reportsDirectory=$report/coverage",
                            '--reporter=default', '--reporter=junit', "--outputFile.junit=$report/tests.xml")
                        node node_modules/vitest/vitest.mjs run @options *> "$report/run.log"
                    } finally { Pop-Location }
                }
                'mobile' {
                    Push-Location mobile-app
                    try {
                        $options = @('--runInBand', '--coverage', '--json',
                            "--outputFile=$report/tests.json", "--coverageDirectory=$report/coverage")
                        node node_modules/jest/bin/jest.js @options *> "$report/run.log"
                    } finally { Pop-Location }
                }
                'solidity' {
                    Push-Location contracts
                    try {
                        forge test --junit > "$report/tests.xml" 2> "$report/test.log"
                        $testExit = $LASTEXITCODE
                        $options = @('--report', 'summary', '--report', 'lcov', '--report-file', "$report/lcov.info",
                            '--exclude-tests', '--no-match-coverage', '(^|[/\\])(MockUSDC\.sol|Deploy[^/\\]*\.s\.sol)$')
                        forge coverage @options *> "$report/run.log"
                        if ($testExit -ne 0) { $failed += "$name (forge test)" }
                    } finally { Pop-Location }
                }
            }
            if ($LASTEXITCODE -ne 0) { $failed += "$name (exit $LASTEXITCODE)" }
            if (Test-Path -LiteralPath "$report/run.log") { Get-Content -Tail 17 -LiteralPath "$report/run.log" }
        } catch {
            $failed += $name
            $_ | Out-String | Add-Content -LiteralPath "$report/run.log"
            Write-Warning "$name failed. See $report/run.log"
        }
    }
    & $python -X utf8 scripts/coverage_summary.py "$output" --components @Component --check
    $reportExit = $LASTEXITCODE
    Write-Host "Reports: $output"
    if ($failed.Count) { Write-Warning ("Failed test commands: " + ($failed -join ', ')) }
    if ($reportExit -ne 0 -or $failed.Count) { exit 1 }
    exit 0
} finally {
    Pop-Location
}
