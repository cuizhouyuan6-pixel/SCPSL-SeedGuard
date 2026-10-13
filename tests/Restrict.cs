// Builds a test-only copy of the publicized game assembly with worst-case accessibility: every member of the
// types SeedGuard touches becomes internal unless the shipped LabApi.dll (compiled against the real game
// assembly) references it. Runs under Mono because this Mono.Cecil build writes through Mono-only APIs.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class Restrict
{
    private static readonly string[] Types = { "MapGeneration.SeedSynchronizer", "ReferenceHub", "CentralAuth.PlayerAuthenticationManager", "NicknameSync", "GameConsoleTransmission" };

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length < 3) throw new ArgumentException("用法：Restrict.exe <publicized> <LabApi.dll> <output> [--remove-client-guard]");
            string output = Path.GetFullPath(args[2]);
            if (Path.GetFileName(output) != "Assembly-CSharp.dll" || !output.Replace('\\', '/').Contains("/build/runtime/"))
                throw new ArgumentException("只允许在本插件 build/runtime 目录内生成测试用 Assembly-CSharp.dll。");
            bool removeGuard = args.Length > 3 && args[3] == "--remove-client-guard";
            var proven = new HashSet<string>();
            using (ModuleDefinition labApi = ModuleDefinition.ReadModule(args[1]))
            {
                foreach (MemberReference reference in labApi.GetMemberReferences())
                {
                    string owner = reference.DeclaringType.FullName;
                    proven.Add(owner + "::" + reference.Name);
                    // add/remove of one event always share the event's accessibility.
                    if (reference.Name.StartsWith("add_")) proven.Add(owner + "::remove_" + reference.Name.Substring(4));
                }
            }
            int restricted = 0;
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0])));
            using (var input = new MemoryStream(File.ReadAllBytes(args[0])))
            using (AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(input, new ReaderParameters { AssemblyResolver = resolver }))
            {
                ModuleDefinition module = assembly.MainModule;
                foreach (string name in Types)
                {
                    TypeDefinition type = module.GetType(name) ?? throw new InvalidOperationException("找不到类型 " + name);
                    foreach (MethodDefinition method in type.Methods)
                    {
                        if (method.IsConstructor || method.IsVirtual || method.IsAbstract || proven.Contains(name + "::" + method.Name)) continue;
                        method.IsAssembly = true;
                        restricted++;
                    }
                    foreach (FieldDefinition field in type.Fields)
                    {
                        if (proven.Contains(name + "::" + field.Name)) continue;
                        field.IsAssembly = true;
                        restricted++;
                    }
                }
                TypeDefinition sync = module.GetType("MapGeneration.SeedSynchronizer");
                TypeDefinition message = sync.NestedTypes.Single(t => t.Name == "SeedMessage");
                message.IsNestedAssembly = true;
                foreach (FieldDefinition field in message.Fields) { field.IsAssembly = true; restricted++; }
                // GenerateLevel needs Pooling.dll, which is not part of the reference set. Keep ClientReceiveSeed
                // untouched and let GenerateLevel go straight to GenerateFacility, which raises OnBeforeGenerated first.
                MethodDefinition level = sync.Methods.Single(m => m.Name == "GenerateLevel");
                MethodDefinition facility = sync.Methods.Single(m => m.Name == "GenerateFacility");
                level.Body.Instructions.Clear();
                level.Body.Variables.Clear();
                level.Body.ExceptionHandlers.Clear();
                ILProcessor probe = level.Body.GetILProcessor();
                probe.Append(probe.Create(OpCodes.Ldarg_0));
                probe.Append(probe.Create(OpCodes.Call, facility));
                probe.Append(probe.Create(OpCodes.Ret));
                // SendToClient needs BouncyCastle (encrypted channel), which is not part of the reference set either.
                // Keep its public signature and record the vanilla "color#text" payload instead.
                TypeDefinition transmission = module.GetType("GameConsoleTransmission");
                MethodDefinition sendToClient = transmission.Methods.Single(m => m.Name == "SendToClient" && m.Parameters.Count == 2);
                var sink = new FieldDefinition("TestLastMessage", FieldAttributes.Public | FieldAttributes.Static, module.TypeSystem.String);
                transmission.Fields.Add(sink);
                sendToClient.Body.Instructions.Clear();
                sendToClient.Body.Variables.Clear();
                sendToClient.Body.ExceptionHandlers.Clear();
                ILProcessor record = sendToClient.Body.GetILProcessor();
                record.Append(record.Create(OpCodes.Ldarg_2));
                record.Append(record.Create(OpCodes.Ldstr, "#"));
                record.Append(record.Create(OpCodes.Ldarg_1));
                record.Append(record.Create(OpCodes.Call, module.ImportReference(typeof(string).GetMethod("Concat", new[] { typeof(string), typeof(string), typeof(string) }))));
                record.Append(record.Create(OpCodes.Stsfld, sink));
                record.Append(record.Create(OpCodes.Ret));
                if (removeGuard)
                {
                    MethodDefinition receive = sync.Methods.Single(m => m.Name == "ClientReceiveSeed");
                    Instruction guard = receive.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldsfld && ((FieldReference)i.Operand).Name == "MapGenerated");
                    if (guard.Next.OpCode != OpCodes.Brtrue_S && guard.Next.OpCode != OpCodes.Brtrue) throw new InvalidOperationException("找不到 ClientReceiveSeed 的 MapGenerated 判断。");
                    ILProcessor il = receive.Body.GetILProcessor();
                    il.Replace(guard.Next, il.Create(OpCodes.Pop));
                }
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                using (var buffer = new MemoryStream())
                {
                    assembly.Write(buffer);
                    File.WriteAllBytes(output, buffer.ToArray());
                }
            }
            Console.WriteLine($"测试运行时：{output}；限制为 internal 的成员 {restricted} 个；GenerateLevel、SendToClient 已替换为探针；移除客户端保护={removeGuard}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("生成测试运行时失败：" + ex);
            return 1;
        }
    }
}
