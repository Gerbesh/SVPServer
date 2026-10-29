$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ /out:SVPServer.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Net.Http.dll /reference:System.Web.Extensions.dll /reference:System.Windows.Forms.DataVisualization.dll TVControl.cs TVRemote.cs
if ($LASTEXITCODE -ne 0) { throw 'Ошибка сборки SVPServer' }
