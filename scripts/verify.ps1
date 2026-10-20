<#
.SYNOPSIS
  Repeatable build + test verification for CodeForge Studio (Windows).
.DESCRIPTION
  Runs restore, a Release build and both test projects, then prints a summary and writes
  artifacts\verification.txt. Exits non-zero if any step fails.
  Usage:  powershell -ExecutionPolicy Bypass -File scripts\verify.ps1
#>
$ErrorActionPreference = 'Continue'
Set-Location (Split-Path -Parent $PSScriptRoot)
New-Item -ItemType Directory -Force -Path artifacts | Out-Null
$log = Join-Path 'artifacts' 'verification.txt'

$steps = @(
    @{ Name = 'restore';            Args = @('restore', 'CodeForgeStudio.sln') },
    @{ Name = 'build (Release)';    Args = @('build', 'CodeForgeStudio.sln', '-c', 'Release', '--no-restore', '--nologo') },
    @{ Name = 'tests: CodeForge.Tests';        Args = @('test', 'tests\CodeForge.Tests', '-c', 'Release', '--no-build', '--nologo') },
    @{ Name = 'tests: CodeForge.Studio.Tests'; Args = @('test', 'tests\CodeForge.Studio.Tests', '-c', 'Release', '--no-build', '--nologo') }
)

$out = New-Object System.Collections.Generic.List[string]
$out.Add("CodeForge Studio verification - $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')")
$out.Add("OS: $([System.Runtime.InteropServices.RuntimeInformation]::OSDescription)")
$out.Add("dotnet SDK: $(dotnet --version)")
$out.Add('')

$failed = $false
foreach ($s in $steps) {
    $cmd = 'dotnet ' + ($s.Args -join ' ')
    Write-Host "==> $($s.Name): $cmd"
    $lines = & dotnet @($s.Args) 2>&1 | ForEach-Object { "$_" }
    $code = $LASTEXITCODE
    $lines | Write-Host
    $summary = $lines | Where-Object { $_ -match 'Passed!|Failed!|Build succeeded|Build FAILED|Warning\(s\)|Error\(s\)' }
    $out.Add("[$($s.Name)] exit=$code  command: $cmd")
    foreach ($l in $summary) { $out.Add("    " + $l.Trim()) }
    if ($code -ne 0) { $failed = $true; $out.Add('    -> FAILED'); break }
}

$out.Add('')
$out.Add($(if ($failed) { 'RESULT: FAILED' } else { 'RESULT: PASSED' }))
$out | Set-Content -Encoding UTF8 $log
Write-Host ''
$out | Write-Host
if ($failed) { exit 1 }
