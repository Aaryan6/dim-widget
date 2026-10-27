param([switch]$Live)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $root 'build.ps1')

$testExe = Join-Path $root 'bin\UpdateChecks.exe'
$testArguments = @($arguments | Where-Object { $_ -notlike '/out:*' -and $_ -notlike '/target:*' })
$testArguments += @('/target:exe', '/main:DimWidget.UpdateChecks', ('/out:' + $testExe),
    (Join-Path $root 'tests\UpdateChecks.cs'))
& $csc.FullName @testArguments
if ($LASTEXITCODE -ne 0) { throw 'Update test compilation failed.' }
if ($Live) { & $testExe --live } else { & $testExe }
if ($LASTEXITCODE -ne 0) { throw 'Update checks failed.' }
