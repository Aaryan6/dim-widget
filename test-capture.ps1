$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $root 'build.ps1')

$testExe = Join-Path $root 'bin\CaptureChecks.exe'
$testArguments = @($arguments | Where-Object { $_ -notlike '/out:*' -and $_ -notlike '/target:*' })
$testArguments += @('/target:exe', '/main:DimWidget.CaptureChecks', ('/out:' + $testExe),
    (Join-Path $root 'tests\CaptureChecks.cs'))
& $csc.FullName @testArguments
if ($LASTEXITCODE -ne 0) { throw 'Capture test compilation failed.' }
& $testExe
if ($LASTEXITCODE -ne 0) { throw 'Capture checks failed.' }
