# Compile AutoOverlay.exe avec le compilateur C# fourni par Windows (.NET Framework 4).
$ErrorActionPreference = 'Stop'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe introuvable : $csc" }
& $csc /nologo /target:winexe /optimize+ /platform:x64 `
    "/out:$PSScriptRoot\AutoOverlay.exe" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    "$PSScriptRoot\src\Program.cs"
if ($LASTEXITCODE -ne 0) { throw "Echec de la compilation" }
Write-Host "OK : $PSScriptRoot\AutoOverlay.exe"
