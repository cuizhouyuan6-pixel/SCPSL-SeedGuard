$ErrorActionPreference='Stop'
$project=$PSScriptRoot
$version='1.0.0'
$delivery=Join-Path $project '交付'
$package=Join-Path $delivery "SeedGuard_v$version"
if(Test-Path -LiteralPath $package){throw '交付目录已存在；请审核后使用新的版本目录。'}
@('EXILED/Plugins','Source','Verification') | ForEach-Object {New-Item -ItemType Directory -Path (Join-Path $package $_) -Force | Out-Null}
Copy-Item -LiteralPath (Join-Path $project 'build/SeedGuard.dll') -Destination (Join-Path $package 'EXILED/Plugins/SeedGuard.dll')
foreach($file in @('README.md','CHANGELOG.md','配置示例.yml','需求与设计.md','验证报告.md')){Copy-Item -LiteralPath (Join-Path $project $file) -Destination $package}
foreach($directory in @('src','tests')){Copy-Item -LiteralPath (Join-Path $project $directory) -Destination (Join-Path $package 'Source') -Recurse}
foreach($file in @('SeedGuard.csproj','build.ps1','package.ps1','README.md')){Copy-Item -LiteralPath (Join-Path $project $file) -Destination (Join-Path $package 'Source')}
Get-ChildItem -LiteralPath (Join-Path $project 'build/logs') -Filter *.log | Copy-Item -Destination (Join-Path $package 'Verification')
$rows=Get-ChildItem -LiteralPath $package -Recurse -File | ForEach-Object {[pscustomobject]@{Path=$_.FullName.Substring($package.Length+1).Replace('\','/');SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash;Bytes=$_.Length}}
$rows | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $package 'SHA256.json') -Encoding utf8
$zip="$package.zip"
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
# Write entries explicitly with '/' separators; Windows PowerShell 5.1 Compress-Archive stores '\'.
$writer=[IO.Compression.ZipFile]::Open($zip,[IO.Compression.ZipArchiveMode]::Create)
try {
 foreach($file in Get-ChildItem -LiteralPath $package -Recurse -File){
  $name=(Split-Path $package -Leaf)+'/'+$file.FullName.Substring($package.Length+1).Replace('\','/')
  [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($writer,$file.FullName,$name,[IO.Compression.CompressionLevel]::Optimal)
 }
} finally {$writer.Dispose()}
$archive=[IO.Compression.ZipFile]::OpenRead($zip)
try {
 $prefix=(Split-Path $package -Leaf)+'/'
 foreach($r in $rows){
  $entry=$archive.GetEntry($prefix+$r.Path)
  if(!$entry -or $entry.Length -ne $r.Bytes){throw "压缩包缺失或长度不符：$($r.Path)"}
  $stream=$entry.Open();$sha=[Security.Cryptography.SHA256]::Create()
  try{$hash=[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','')}finally{$stream.Dispose();$sha.Dispose()}
  if($hash -ne $r.SHA256){throw "压缩包哈希不符：$($r.Path)"}
 }
 Write-Output "压缩包已按 $($rows.Count) 个文件的 SHA256 清单逐一核对。"
} finally {$archive.Dispose()}
Get-Item -LiteralPath $zip | Select-Object FullName,Length
