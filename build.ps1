param([string]$RefsPath=(Join-Path $PSScriptRoot '../../refs'))
$ErrorActionPreference='Stop'
Push-Location $PSScriptRoot
try {
 $sdkLine = & dotnet --list-sdks | Select-Object -Last 1
 if($sdkLine -notmatch '^(\S+) \[(.+)\]'){throw '找不到 .NET SDK。'}
 $compiler=Join-Path $Matches[2] "$($Matches[1])/Roslyn/bincore/csc.dll"
 $references=Get-ChildItem -LiteralPath $RefsPath -Filter *.dll | ForEach-Object { '-r:'+ $_.FullName }
 $sources=Get-ChildItem src/*.cs | ForEach-Object FullName
 New-Item -ItemType Directory -Force build | Out-Null
 & dotnet $compiler -nologo -nostdlib -noconfig -target:library -langversion:9.0 -utf8output -warnaserror+ -deterministic -out:build/SeedGuard.dll @references @sources
 if($LASTEXITCODE -ne 0){throw '插件编译失败。'}
 Write-Output 'SeedGuard.dll 编译成功。'
} finally { Pop-Location }
