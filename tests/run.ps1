param([string]$RefsPath=(Join-Path $PSScriptRoot '../../../refs'),[string]$MonoPath='D:/Mono/bin/mono.exe',[string]$Drive='R:',[switch]$SkipMono)
# Full offline regression: plugin build, restricted game runtime, CLR + Mono runs and the static audit.
$ErrorActionPreference='Stop'
$project=Split-Path $PSScriptRoot
$refs=[IO.Path]::GetFullPath($RefsPath)
$build=Join-Path $project 'build'
$logs=Join-Path $build 'logs'
$cecil='C:/Program Files/Unity/Hub/Editor/2021.3.17f1/Editor/Data/MonoBleedingEdge/lib/mono/gac/Mono.Cecil/0.11.1.0__0738eb9f132ed756/Mono.Cecil.dll'
if($Drive -notmatch '^[A-Za-z]:$'){throw 'Drive 必须是盘符，例如 R:'}
$sdkLine=& dotnet --list-sdks | Select-Object -Last 1
if($sdkLine -notmatch '^(\S+) \[(.+)\]'){throw '找不到 .NET SDK。'}
$compiler=Join-Path $Matches[2] "$($Matches[1])/Roslyn/bincore/csc.dll"
$baseRefs=@('mscorlib.dll','System.dll','System.Core.dll') | ForEach-Object {'-r:'+(Join-Path $refs $_)}
$gameRefs=Get-ChildItem -LiteralPath $refs -Filter *.dll | Where-Object {$_.Name -notmatch '^(Exiled\..*|0Harmony)\.dll$'} | ForEach-Object {'-r:'+$_.FullName}
$runtimeFiles=Get-ChildItem -LiteralPath $refs -Filter *.dll | Where-Object {$_.Name -notmatch '^(Assembly-CSharp-Publicized|mscorlib|System.*|netstandard|Exiled\..*|0Harmony)\.dll$'}

function Compile([string]$Out,[string]$Target,[string[]]$References,[string[]]$Sources,[string]$Define){
 $arguments=@('-nologo','-nostdlib','-noconfig',"-target:$Target",'-langversion:9.0','-utf8output','-warnaserror+',"-out:$Out")
 if($Define){$arguments+="-define:$Define"}
 & dotnet $compiler @arguments @References @Sources
 if($LASTEXITCODE -ne 0){throw "编译失败：$Out"}
}
function Run([string]$Name,[scriptblock]$Command){
 $log=Join-Path $logs "$Name.log"
 $output=& $Command
 $code=$LASTEXITCODE
 $output | Set-Content -LiteralPath $log -Encoding UTF8
 $output | ForEach-Object {Write-Output "  $_"}
 if($code -ne 0){throw "$Name 失败，详见 $log"}
}
function WithDrive([scriptblock]$Body){
 if(Test-Path "$Drive\"){throw "临时盘符 $Drive 已被占用"}
 # Mono on this host cannot load assemblies from non-ASCII paths; SUBST only adds a temporary alias.
 & subst $Drive $project
 if($LASTEXITCODE -ne 0){throw '无法建立临时盘符映射'}
 try {& $Body} finally {& subst $Drive /D}
}

Push-Location $project
try {
 New-Item -ItemType Directory -Force -Path $logs,(Join-Path $build 'tests'),(Join-Path $build 'tools'),(Join-Path $build 'not-installed'),(Join-Path $build 'runtime/inputs') | Out-Null
 Write-Output '== 编译插件'
 & (Join-Path $project 'build.ps1') -RefsPath $refs
 if($LASTEXITCODE -ne 0){throw '插件编译失败'}
 Write-Output '== 编译测试程序'
 Compile 'build/tools/Restrict.exe' 'exe' ($baseRefs+"-r:$cecil") @('tests/Restrict.cs')
 Copy-Item -LiteralPath $cecil -Destination 'build/tools/Mono.Cecil.dll' -Force
 Compile 'build/not-installed/SeedGuardMissingDependency.dll' 'library' $baseRefs @('tests/BrokenDependencyFixture.cs') 'DEPENDENCY'
 Compile 'build/tests/BrokenUnrelatedPlugin.dll' 'library' ($baseRefs+'-r:build/not-installed/SeedGuardMissingDependency.dll') @('tests/BrokenDependencyFixture.cs')
 $gameSources=@('src/GuardCore.cs','src/GuardService.cs','src/SeedChannel.cs','src/GameWorld.cs','tests/Harness.cs','tests/GameTests.cs','tests/NaiveAccess.cs')
 Compile 'build/tests/GameTests.exe' 'exe' $gameRefs $gameSources
 $pluginSources=@(Get-ChildItem src/*.cs | ForEach-Object {'src/'+$_.Name})+@('tests/Harness.cs','tests/ExiledDoubles.cs','tests/PluginTests.cs')
 Compile 'build/tests/PluginTests.exe' 'exe' $gameRefs $pluginSources
 Write-Output '== 生成非公开权限测试运行时（Mono + Mono.Cecil）'
 Get-ChildItem -LiteralPath $refs -Filter *.dll | Copy-Item -Destination 'build/runtime/inputs' -Force
 WithDrive {
  Push-Location "$Drive\"
  try {
   Run 'restrict' {& $MonoPath "$Drive\build\tools\Restrict.exe" "$Drive\build\runtime\inputs\Assembly-CSharp-Publicized.dll" "$Drive\build\runtime\inputs\LabApi.dll" "$Drive\build\runtime\restricted\Assembly-CSharp.dll"}
   Run 'restrict-guardless' {& $MonoPath "$Drive\build\tools\Restrict.exe" "$Drive\build\runtime\inputs\Assembly-CSharp-Publicized.dll" "$Drive\build\runtime\inputs\LabApi.dll" "$Drive\build\runtime\guardless\Assembly-CSharp.dll" --remove-client-guard}
  } finally {Pop-Location}
 }
 foreach($dir in @('build/runtime/restricted','build/runtime/guardless')){
  $runtimeFiles | Copy-Item -Destination $dir -Force
  Copy-Item -LiteralPath 'build/tests/GameTests.exe','build/tests/PluginTests.exe','build/tests/BrokenUnrelatedPlugin.dll' -Destination $dir -Force
 }
 Write-Output '== CLR 运行'
 Run 'clr-game' {& 'build/runtime/restricted/GameTests.exe' --all}
 Run 'clr-naive' {& 'build/runtime/restricted/GameTests.exe' --naive}
 Run 'clr-guardless' {& 'build/runtime/guardless/GameTests.exe' --guardless}
 Run 'clr-plugin' {& 'build/runtime/restricted/PluginTests.exe'}
 if(!$SkipMono){
  Write-Output '== Mono 运行'
  WithDrive {
   Push-Location "$Drive\"
   try {
    Run 'mono-version' {& $MonoPath --version}
    Run 'mono-game' {& $MonoPath "$Drive\build\runtime\restricted\GameTests.exe" --all}
    Run 'mono-naive' {& $MonoPath "$Drive\build\runtime\restricted\GameTests.exe" --naive}
    Run 'mono-guardless' {& $MonoPath "$Drive\build\runtime\guardless\GameTests.exe" --guardless}
    Run 'mono-plugin' {& $MonoPath "$Drive\build\runtime\restricted\PluginTests.exe"}
   } finally {Pop-Location}
  }
 }
 Write-Output '== 静态审计'
 Run 'audit' {& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Audit.ps1') -RefsPath $refs}
 Write-Output '全部检查通过。'
} finally {Pop-Location}
