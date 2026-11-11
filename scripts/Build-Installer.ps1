[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\installer'))
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\installer\ParalivesAccess.Installer.csproj'
& dotnet build $project -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$source = Join-Path $PSScriptRoot '..\installer\bin\Release\net48\ParalivesAccess-Installer.exe'
$destination = Join-Path $OutputDirectory 'ParalivesAccess-Installer.exe'
Copy-Item -LiteralPath $source -Destination $destination -Force
$hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($destination + '.sha256', "$hash  ParalivesAccess-Installer.exe`r`n", [Text.UTF8Encoding]::new($false))
[pscustomobject]@{Path=[IO.Path]::GetFullPath($destination);Bytes=(Get-Item -LiteralPath $destination).Length;SHA256=$hash} | ConvertTo-Json
