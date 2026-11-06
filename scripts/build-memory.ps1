# Memory limits for builds and tests, dot-sourced by test-all.ps1 and package.ps1.
#
# On a 10 GB machine the full test run was stopped for low memory in cargo test: MSBuild worker nodes and the compiler
# server stayed resident after the host checks, and cargo ran one job per core, each linking a large test binary.
# Worker nodes now exit after each build, the build servers are shut down before the Rust steps, and cargo gets about
# 4 GB per job (two jobs passed on that machine, four did not). A CARGO_BUILD_JOBS already set is kept. package.ps1
# was stopped the same way on 2026-11-06, and its MSBuild nodes were still holding about 250 MB afterwards.
$env:MSBUILDDISABLENODEREUSE = '1'
if (-not $env:CARGO_BUILD_JOBS) {
    # Rounded first: Windows reports a little under the installed size, and a 16 GB machine should keep four jobs.
    $memoryGb = [Math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB)
    $jobs = [Math]::Max(1, [Math]::Min([Environment]::ProcessorCount, [int][Math]::Floor($memoryGb / 4)))
    if ($jobs -lt [Environment]::ProcessorCount) {
        $env:CARGO_BUILD_JOBS = "$jobs"
        Write-Host "cargo: $jobs parallel jobs for $memoryGb GB of memory." -ForegroundColor Yellow
    }
}

# Shuts down the .NET compiler and MSBuild servers so their memory is free for the Rust build.
function Stop-DotnetBuildServers {
    # A client-only run may have no .NET SDK on PATH; under ErrorActionPreference Stop that would end the script.
    if (Get-Command dotnet -ErrorAction SilentlyContinue) {
        dotnet build-server shutdown *> $null
    }
}
