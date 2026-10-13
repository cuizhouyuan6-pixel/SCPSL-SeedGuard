param([string]$RefsPath=(Join-Path $PSScriptRoot '../../../refs'))
# Static audit: target versions, accessibility evidence for every direct game reference, EXILED doubles,
# forbidden APIs, and the vanilla behaviour SeedGuard's safety argument depends on.
$ErrorActionPreference='Stop'
Add-Type -Path 'C:/Program Files/Unity/Hub/Editor/2021.3.17f1/Editor/Data/MonoBleedingEdge/lib/mono/gac/Mono.Cecil/0.11.1.0__0738eb9f132ed756/Mono.Cecil.dll'
$project=Split-Path $PSScriptRoot
$refs=[IO.Path]::GetFullPath($RefsPath)
$resolver=[Mono.Cecil.DefaultAssemblyResolver]::new()
$resolver.AddSearchDirectory($refs)
$rp=[Mono.Cecil.ReaderParameters]::new()
$rp.AssemblyResolver=$resolver
function Load($path){[Mono.Cecil.AssemblyDefinition]::ReadAssembly($path,$rp)}
$plugin=Load (Join-Path $project 'build/SeedGuard.dll')
$game=Load (Join-Path $refs 'Assembly-CSharp-Publicized.dll')
$labApi=Load (Join-Path $refs 'LabApi.dll')
$mirror=Load (Join-Path $refs 'Mirror.dll')
$exiled=@('Exiled.API.dll','Exiled.Events.dll','Exiled.Permissions.dll') | ForEach-Object {Load (Join-Path $refs $_)}
$doubles=Load (Join-Path $project 'build/tests/PluginTests.exe')
$commandSystem=Load (Join-Path $refs 'CommandSystem.Core.dll')
$unity=Load (Join-Path $refs 'UnityEngine.CoreModule.dll')
# Cecil's own Resolve() needs Mono-only BCL members, so external members are looked up by name and signature.
$external=@{'Mirror'=$mirror;'CommandSystem.Core'=$commandSystem;'UnityEngine.CoreModule'=$unity;'Exiled.API'=$exiled[0];'Exiled.Events'=$exiled[1];'Exiled.Permissions'=$exiled[2]}
$script:checks=0
function Assert($condition,$message){$script:checks++;if(!$condition){throw "审计失败：$message"}}
function Types($list){foreach($t in $list){$t;Types $t.NestedTypes}}
function TypeOf($asm,$name){$t=$asm.MainModule.GetType($name);Assert ($null -ne $t) "找不到类型 $name";return $t}
function Method($type,$name){$m=@($type.Methods | Where-Object Name -eq $name);Assert ($m.Count -eq 1) "方法 $($type.FullName)::$name 不存在或不唯一";return $m[0]}
function Ops($method){return @($method.Body.Instructions)}
function IsCall($i,$declaring,$name){
 return ($i.OpCode.Code -in @('Call','Callvirt','Newobj')) -and ($i.Operand -is [Mono.Cecil.MethodReference]) -and ($i.Operand.DeclaringType.FullName -like $declaring) -and ($i.Operand.Name -eq $name)
}
function Field($i,$code,$name){return ($i.OpCode.Code -eq $code) -and ($i.Operand -is [Mono.Cecil.FieldReference]) -and ($i.Operand.Name -eq $name)}
function ScopeName($type){while($type.DeclaringType){$type=$type.DeclaringType};return $type.Scope.Name}
function Owner($reference){$t=$reference.DeclaringType;if($t -is [Mono.Cecil.GenericInstanceType]){$t=$t.ElementType};return $t}
function Signature($method){return (@($method.Parameters | ForEach-Object {$_.ParameterType.FullName}) -join ',')}
function FindMember($reference){
 $owner=Owner $reference
 $asm=$external[(ScopeName $owner)]
 if($null -eq $asm){return $null}
 $type=$asm.MainModule.GetType($owner.FullName)
 if($null -eq $type){return $null}
 if($reference -is [Mono.Cecil.FieldReference]){return @($type.Fields | Where-Object Name -eq $reference.Name)[0]}
 $method=$reference
 if($method -is [Mono.Cecil.GenericInstanceMethod]){$method=$method.ElementMethod}
 $signature=Signature $method
 return @($type.Methods | Where-Object {$_.Name -eq $method.Name -and (Signature $_) -eq $signature})[0]
}
function IsAccessible($definition){
 if($definition.IsPublic){return $true}
 # Base-class constructors and overrides of the EXILED plugin type may be protected.
 return ($definition -is [Mono.Cecil.MethodDefinition]) -and ($definition.IsFamily -or $definition.IsFamilyOrAssembly) -and ($definition.DeclaringType.FullName -eq 'Exiled.API.Features.Plugin`1')
}

# 1. Exact environment the plugin was verified against.
$ver=Ops (Method (TypeOf $game 'GameCore.Version') '.cctor') | Select-Object -First 6 | Where-Object {$_.OpCode.Name.StartsWith('ldc.i4')}
$numbers=@($ver | ForEach-Object {if($null -ne $_.Operand){[int]$_.Operand}else{[int]$_.OpCode.Name.Split('.')[-1]}})
Assert (($numbers -join '.') -eq '14.2.7') "游戏版本已变化：$($numbers -join '.')"
Assert ($exiled[0].Name.Version.ToString() -eq '9.14.2.0') 'EXILED 版本已变化'
Assert ($labApi.Name.Version.ToString() -eq '1.1.7.0') 'LabAPI 版本已变化'
Assert ($plugin.Name.Version.ToString() -eq '1.0.0.0') '插件版本号'
Assert (@($plugin.MainModule.AssemblyReferences | Where-Object Name -like '*Harmony*').Count -eq 0) '插件不应依赖 Harmony'

# 2. Runtime evidence of public accessibility: LabApi.dll is compiled against the real game assembly.
$proven=[System.Collections.Generic.HashSet[string]]::new()
foreach($r in $labApi.MainModule.GetMemberReferences()){
 $o=(Owner $r).FullName
 [void]$proven.Add("$o::$($r.Name)")
 if($r.Name -like 'add_*'){[void]$proven.Add("$o::remove_"+$r.Name.Substring(4))}
}
$provenTypes=[System.Collections.Generic.HashSet[string]]::new()
foreach($t in $labApi.MainModule.GetTypeReferences()){if((ScopeName $t) -eq 'Assembly-CSharp'){[void]$provenTypes.Add($t.FullName)}}
$direct=[System.Collections.Generic.List[string]]::new()
$externalCount=0
foreach($r in $plugin.MainModule.GetMemberReferences()){
 $owner=Owner $r
 $scope=ScopeName $owner
 if($scope -eq 'Assembly-CSharp'){
  $key="$($owner.FullName)::$($r.Name)"
  Assert $proven.Contains($key) "插件直接引用了缺少公开证据的游戏成员：$key"
  $direct.Add($key)
 } elseif($external.ContainsKey($scope)){
  $definition=FindMember $r
  Assert ($null -ne $definition) "在 $scope 中找不到外部成员：$r"
  Assert (IsAccessible $definition) "外部成员非公开：$r"
  Assert ($definition.DeclaringType.IsPublic -or $definition.DeclaringType.IsNestedPublic) "外部类型非公开：$r"
  $externalCount++
 }
}
foreach($t in $plugin.MainModule.GetTypeReferences()){
 if((ScopeName $t) -eq 'Assembly-CSharp'){Assert $provenTypes.Contains($t.FullName) "插件直接引用了缺少公开证据的游戏类型：$($t.FullName)"}
}

# 3. EXILED doubles used by the lifecycle tests must match the real public API.
$realMethods=[System.Collections.Generic.HashSet[string]]::new()
foreach($asm in $exiled){foreach($t in (Types $asm.MainModule.Types)){foreach($m in $t.Methods){if($m.IsPublic){[void]$realMethods.Add($m.FullName)}}}}
$doubleCount=0
foreach($t in (Types $doubles.MainModule.Types)){
 if(!$t.Namespace.StartsWith('Exiled')){continue}
 foreach($m in $t.Methods){
  if(!$m.IsPublic -or $m.IsConstructor -or $m.Name -in @('BeginInvoke','EndInvoke')){continue}
  Assert $realMethods.Contains($m.FullName) "EXILED 替身签名与真实程序集不一致：$($m.FullName)"
  $doubleCount++
 }
}
Assert ($doubleCount -ge 15) "EXILED 替身核对数量异常：$doubleCount"

# 4. Forbidden APIs and explicit reflection lookups.
foreach($t in (Types $plugin.MainModule.Types)){
 foreach($m in $t.Methods){
  if(!$m.HasBody){continue}
  foreach($i in $m.Body.Instructions){
   if($i.Operand -isnot [Mono.Cecil.MethodReference]){continue}
   $o=$i.Operand
   $name=$o.DeclaringType.FullName+'::'+$o.Name
   Assert ($name -notin @('System.AppDomain::GetAssemblies','System.Reflection.Assembly::GetTypes','System.Reflection.Assembly::GetExportedTypes','System.Reflection.Module::GetTypes')) "禁止全域类型扫描：$name"
   Assert ($o.DeclaringType.FullName -ne 'UnityEngine.Random') "不得改动游戏随机数状态：$name"
   if($o.DeclaringType.FullName -eq 'System.Type' -and $o.Name -in @('GetMethod','GetField','GetNestedType','GetProperty','GetFields','GetMethods')){
    Assert (@($o.Parameters | Where-Object {$_.ParameterType.FullName -eq 'System.Reflection.BindingFlags'}).Count -eq 1) "反射查找必须显式指定 BindingFlags：$($o.FullName)"
   }
  }
 }
}

# 5. Vanilla behaviour the design depends on. Any change in a game update fails here first.
$seedCommand=TypeOf $game 'CommandSystem.Commands.Console.SeedCommand'
$handlers=@($seedCommand.CustomAttributes | Where-Object {$_.AttributeType.Name -eq 'CommandHandlerAttribute'} | ForEach-Object {$_.ConstructorArguments[0].Value.FullName})
Assert (($handlers -join ',') -eq 'CommandSystem.GameConsoleCommandHandler') "seed 指令的注册位置变化：$($handlers -join ',')"
Assert (@(Ops (Method $seedCommand 'Execute') | Where-Object {IsCall $_ 'MapGeneration.SeedSynchronizer' 'get_Seed'}).Count -eq 1) 'seed 指令不再读取 SeedSynchronizer.Seed'
$console=Ops (Method (TypeOf $game 'GameCore.Console') 'TypeCommand')
Assert (@($console | Where-Object {IsCall $_ 'GameConsoleTransmission' 'SendToServer'}).Count -ge 1) '控制台 . 指令不再发往服务器'
Assert (@($console | Where-Object {Field $_ 'Ldsfld' 'ConsoleCommandHandler'}).Count -ge 1 -and @($console | Where-Object {IsCall $_ 'CommandSystem.CommandHandler' 'TryGetCommand'}).Count -ge 1) '控制台普通指令不再在本地执行'
$sync=TypeOf $game 'MapGeneration.SeedSynchronizer'
$receive=Ops (Method $sync 'ClientReceiveSeed')
$guard=@($receive | Where-Object {(Field $_ 'Ldsfld' 'MapGenerated') -and ($_.Next.OpCode.Code -in @('Brtrue','Brtrue_S'))})
Assert ($guard.Count -eq 1) 'ClientReceiveSeed 的 MapGenerated 判断变化'
$branch=$guard[0].Next
$generation=@($receive | Where-Object {(IsCall $_ 'MapGeneration.SeedSynchronizer' 'GenerateLevel') -or (IsCall $_ 'MapGeneration.SeedSynchronizer' 'GenerateFacility')})
Assert ($generation.Count -ge 1 -and @($generation | Where-Object {$_.Offset -le $branch.Offset -or $_.Offset -ge $branch.Operand.Offset}).Count -eq 0) 'ClientReceiveSeed 在地图已生成时仍可能重建'
Assert (@($receive | Where-Object {IsCall $_ 'MapGeneration.SeedSynchronizer' 'set_Seed'}).Count -eq 1) 'ClientReceiveSeed 不再保存收到的种子'
$connect=Ops (Method $sync 'OnNewPlayerConnected')
$send=@($connect | Where-Object {(IsCall $_ 'Mirror.NetworkConnection' 'Send') -and ($_.Operand -is [Mono.Cecil.GenericInstanceMethod]) -and ($_.Operand.GenericArguments[0].Name -eq 'SeedMessage')})
Assert ($send.Count -eq 1 -and $send[0].Previous.OpCode.Code -eq 'Ldc_I4_0') '玩家连接时不再经可靠通道发送真实种子'
$awake=Ops (Method $sync 'Awake')
$broadcast=@($awake | Where-Object {(IsCall $_ 'Mirror.NetworkServer' 'SendToAll') -and ($_.Operand -is [Mono.Cecil.GenericInstanceMethod]) -and ($_.Operand.GenericArguments[0].Name -eq 'SeedMessage')})
$build=@($awake | Where-Object {IsCall $_ 'MapGeneration.SeedSynchronizer' 'GenerateLevel'})
Assert ($broadcast.Count -eq 1 -and $build.Count -ge 1 -and $broadcast[0].Offset -lt $build[-1].Offset) '服务器换图时不再先广播真实种子后生成地图'
Assert ($broadcast[0].Previous.Previous.OpCode.Code -eq 'Ldc_I4_0') '真实种子广播不再使用可靠通道'
$level=Ops (Method $sync 'GenerateLevel')
$set=@($level | Where-Object {Field $_ 'Stsfld' 'MapGenerated'})
$finished=@($level | Where-Object {Field $_ 'Ldsfld' 'OnGenerationFinished'})
Assert ($set.Count -eq 1 -and $finished.Count -eq 1 -and $set[0].Offset -lt $finished[0].Offset) 'OnGenerationFinished 不再在 MapGenerated 置位后触发'
$transport=Ops (Method (TypeOf $mirror 'Mirror.NetworkClient') 'OnTransportData')
$loading=@($transport | Where-Object {Field $_ 'Ldsfld' 'isLoadingScene'})
$next=@($transport | Where-Object {IsCall $_ 'Mirror.Unbatcher' 'GetNextMessage'})
Assert ($loading.Count -ge 1 -and $next.Count -ge 1 -and $loading[0].Offset -lt $next[0].Offset) 'Mirror 客户端加载场景期间不再暂停处理消息'
$manager=TypeOf $mirror 'Mirror.NetworkManager'
$clientConnect=Ops (Method $manager 'OnClientConnect')
Assert ((Field $clientConnect[1] 'Ldfld' 'clientLoadedScene') -and ($clientConnect[2].OpCode.Code -in @('Brtrue','Brtrue_S'))) '客户端连接后就绪的条件变化'
Assert (@(Ops (Method $manager 'OnClientAuthenticated') | Where-Object {IsCall $_ 'Mirror.Utils' 'IsSceneActive'}).Count -eq 1) '客户端判断是否需要加载在线场景的方式变化'
foreach($name in @('InitiateRoundRestart','ChangeLevel')){
 $restart=Ops (Method (TypeOf $game 'RoundRestarting.RoundRestart') $name)
 $messages=@($restart | Where-Object {IsCall $_ 'RoundRestarting.RoundRestartMessage' '.ctor'})
 Assert ($messages.Count -ge 1) "找不到回合重启消息：$name"
 foreach($message in $messages){Assert ($message.Previous.Previous.OpCode.Code -eq 'Ldc_I4_1') "回合重启消息不再要求客户端重连：$name"}
}
$changeScene=Ops (Method (TypeOf $game 'CustomNetworkManager') 'ServerChangeScene')
$notReady=@($changeScene | Where-Object {IsCall $_ 'Mirror.NetworkServer' 'SetAllClientsNotReady'})
$sceneLoading=@($changeScene | Where-Object {Field $_ 'Stsfld' 'isLoadingScene'})
Assert ($notReady.Count -eq 1 -and $sceneLoading.Count -eq 1 -and $notReady[0].Offset -lt $sceneLoading[0].Offset) '换图时不再先取消全部客户端就绪'

Write-Output "直接引用的游戏成员（均有 LabApi.dll 运行时引用证据）："
$direct | Sort-Object -Unique | ForEach-Object {Write-Output "  $_"}
Write-Output "外部框架成员（Mirror/EXILED/CommandSystem/UnityEngine）公开性核对：$externalCount 个"
Write-Output "EXILED 替身签名核对：$doubleCount 个"
Write-Output "审计通过：$($script:checks) 项检查。"
foreach($asm in @($plugin,$game,$labApi,$mirror,$doubles,$commandSystem,$unity)+$exiled){$asm.Dispose()}
