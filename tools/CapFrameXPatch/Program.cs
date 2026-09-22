using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: CapFrameXPatch <CapFrameX.exe>");
    return 2;
}

var path = Path.GetFullPath(args[0]);
if (!File.Exists(path))
{
    Console.Error.WriteLine("CapFrameX.exe not found: " + path);
    return 3;
}

static string Sha256(string file)
{
    using var sha = SHA256.Create();
    using var fs = File.OpenRead(file);
    return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
}

static bool IsStoreLocal(Instruction i) => i.OpCode.Code is
    Code.Stloc or Code.Stloc_S or Code.Stloc_0 or Code.Stloc_1 or Code.Stloc_2 or Code.Stloc_3;

static Instruction CloneStore(ILProcessor il, Instruction store) => store.OpCode.Code switch
{
    Code.Stloc_0 or Code.Stloc_1 or Code.Stloc_2 or Code.Stloc_3 => il.Create(store.OpCode),
    Code.Stloc or Code.Stloc_S => il.Create(store.OpCode, (VariableDefinition)store.Operand),
    _ => throw new InvalidOperationException("Unsupported stloc opcode: " + store.OpCode)
};

var originalHash = Sha256(path);
var temp = path + ".gctp-patched";

using (var asm = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { InMemory = true, ReadSymbols = false }))
{
    var type = asm.MainModule.Types.FirstOrDefault(t => t.FullName == "CapFrameX.CapFrameXViewRegion")
        ?? throw new InvalidOperationException("CapFrameX.CapFrameXViewRegion was not found.");
    var method = type.Methods.FirstOrDefault(m => m.Name == "RegisterDeferred" && m.IsStatic)
        ?? throw new InvalidOperationException("RegisterDeferred was not found.");

    var ins = method.Body.Instructions;
    Instruction? dispatcherStore = null;
    for (int i = 0; i < ins.Count; i++)
    {
        if (ins[i].Operand is MethodReference mr &&
            mr.Name == "get_Dispatcher" &&
            mr.DeclaringType.FullName.Contains("DispatcherObject", StringComparison.Ordinal))
        {
            for (int j = i + 1; j < Math.Min(ins.Count, i + 6); j++)
            {
                if (IsStoreLocal(ins[j]))
                {
                    dispatcherStore = ins[j];
                    break;
                }
            }
            if (dispatcherStore is not null) break;
        }
    }

    dispatcherStore ??= ins.FirstOrDefault(IsStoreLocal)
        ?? throw new InvalidOperationException("Could not locate Dispatcher local.");

    var il = method.Body.GetILProcessor();
    var forceNull = il.Create(OpCodes.Ldnull);
    var storeNull = CloneStore(il, dispatcherStore);
    il.InsertAfter(dispatcherStore, forceNull);
    il.InsertAfter(forceNull, storeNull);

    if (File.Exists(temp)) File.Delete(temp);
    asm.Write(temp);
}

File.Copy(temp, path, true);
File.Delete(temp);

var patchedHash = Sha256(path);
if (string.Equals(originalHash, patchedHash, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Patch did not change CapFrameX.exe.");

var reportPath = Path.Combine(Path.GetDirectoryName(path)!, "TOTALProfiler-CapFrameX-Patch.txt");
File.WriteAllText(reportPath,
    "G's Cyberpunk 2077 TOTAL Profiler - CapFrameX 1.9.0 hotfix" + Environment.NewLine +
    "Patch: force CapFrameXViewRegion.RegisterDeferred through its synchronous registration path" + Environment.NewLine +
    "Reason: eliminate intermittent tab-navigation race caused by deferred ApplicationIdle view registration" + Environment.NewLine +
    "Original CapFrameX.exe SHA256: " + originalHash + Environment.NewLine +
    "Patched  CapFrameX.exe SHA256: " + patchedHash + Environment.NewLine);

Console.WriteLine("CapFrameX deferred-tab hotfix applied.");
Console.WriteLine("Original SHA256: " + originalHash);
Console.WriteLine("Patched  SHA256: " + patchedHash);
return 0;
