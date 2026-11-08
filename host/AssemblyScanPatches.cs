using System.Reflection;
using System.Runtime.Loader;

namespace GmIde;

// Isola a descoberta de tipos no contexto da ferramenta, compartilhado por ProjectTool e GMAssetCompiler.
internal static class AssemblyScanPatches
{
    internal static int Rewrite(Mono.Cecil.ModuleDefinition module)
    {
        var getTypeFromHandle = module.ImportReference(typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle))!);
        var getAssembly = module.ImportReference(typeof(Type).GetProperty(nameof(Type.Assembly))!.GetGetMethod()!);
        var getLoadContext = module.ImportReference(typeof(AssemblyLoadContext).GetMethod(nameof(AssemblyLoadContext.GetLoadContext))!);
        var getAssemblies = module.ImportReference(typeof(AssemblyLoadContext).GetProperty(nameof(AssemblyLoadContext.Assemblies))!.GetGetMethod()!);
        var toArray = module.ImportReference(typeof(Enumerable).GetMethod(nameof(Enumerable.ToArray))!.MakeGenericMethod(typeof(Assembly)));
        int count = 0;
        foreach (var type in AllTypes(module.Types))
        foreach (var method in type.Methods)
        {
            if (!method.HasBody) continue;
            var il = method.Body.GetILProcessor();
            foreach (var ins in method.Body.Instructions.ToList())
            {
                if ((ins.OpCode != Mono.Cecil.Cil.OpCodes.Callvirt && ins.OpCode != Mono.Cecil.Cil.OpCodes.Call) ||
                    ins.Operand is not Mono.Cecil.MethodReference m || m.Name != "GetAssemblies" ||
                    m.DeclaringType.FullName != "System.AppDomain" || m.Parameters.Count != 0) continue;
                // Reaproveita a instrucao (alvos de branch/try continuam validos) virando pop.
                ins.OpCode = Mono.Cecil.Cil.OpCodes.Pop;
                ins.Operand = null;
                var seq = new[]
                {
                    il.Create(Mono.Cecil.Cil.OpCodes.Ldtoken, (Mono.Cecil.TypeReference)type),
                    il.Create(Mono.Cecil.Cil.OpCodes.Call, getTypeFromHandle),
                    il.Create(Mono.Cecil.Cil.OpCodes.Callvirt, getAssembly),
                    il.Create(Mono.Cecil.Cil.OpCodes.Call, getLoadContext),
                    il.Create(Mono.Cecil.Cil.OpCodes.Callvirt, getAssemblies),
                    il.Create(Mono.Cecil.Cil.OpCodes.Call, toArray),
                };
                var at = ins;
                foreach (var s in seq) { il.InsertAfter(at, s); at = s; }
                count++;
            }
        }
        return count;
    }

    static IEnumerable<Mono.Cecil.TypeDefinition> AllTypes(IEnumerable<Mono.Cecil.TypeDefinition> types)
    {
        foreach (var t in types)
        {
            yield return t;
            foreach (var n in AllTypes(t.NestedTypes)) yield return n;
        }
    }
}
