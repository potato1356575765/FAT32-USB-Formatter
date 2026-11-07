# Builds Fat32UsbFormatter.exe with the C# compiler that ships with Windows (.NET Framework 4.x). No SDK needed.
$ErrorActionPreference = 'Stop'
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
Set-Location $PSScriptRoot
& $csc /nologo /target:winexe /optimize+ /platform:anycpu `
    /out:Fat32UsbFormatter.exe /win32manifest:src\app.manifest `
    /r:System.Management.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    src\Fat32Writer.cs src\Volume.cs src\MainForm.cs
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Write-Host "Built $PSScriptRoot\Fat32UsbFormatter.exe"
