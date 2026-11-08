using Mono.Cecil;
using Mono.Cecil.Cil;

namespace GmIde;

static partial class IdePatches
{
    internal static bool PatchLocalAndroidOptionLoading(ModuleDefinition module)
    {
        var method = module.GetType("YoYoStudio.Resources.GMProject")?.Methods
            .SingleOrDefault(m => m.Name == "GetOptionTypeNamesList");
        var anchor = method?.Body?.Instructions.FirstOrDefault(i => i.OpCode == OpCodes.Ldstr && Equals(i.Operand, "GMMainOptions"));
        if (anchor?.Next?.Operand is not MethodReference contains || contains.Name != "Contains" ||
            contains.DeclaringType is not GenericInstanceType list ||
            list.ElementType.FullName != "System.Collections.Generic.List`1" ||
            list.GenericArguments.Single().FullName != "System.String") return false;
        var add = new MethodReference("Add", module.TypeSystem.Void, contains.DeclaringType) { HasThis = true };
        add.Parameters.Add(new ParameterDefinition(contains.Parameters[0].ParameterType));
        var il = method!.Body.GetILProcessor();
        // Antes de perguntar por Main, inclui Android uma unica vez na lista de
        // tipos a carregar do disco. A restricao de versao do ProjectTool permanece.
        foreach (var ins in new[] {
            il.Create(OpCodes.Dup), il.Create(OpCodes.Ldstr, "GMAndroidOptions"),
            il.Create(OpCodes.Callvirt, contains), il.Create(OpCodes.Brtrue, anchor),
            il.Create(OpCodes.Dup), il.Create(OpCodes.Ldstr, "GMAndroidOptions"), il.Create(OpCodes.Callvirt, add),
        }) il.InsertBefore(anchor, ins);
        method.Body.MaxStackSize += 2;
        return true;
    }

    // Inclui o recurso real de opcoes Android no editor mobile. O restante da lista
    // e preservado; nao cria identidade de usuario nem altera as permissoes de outros alvos.
    internal static bool PatchLocalAndroidOptions(ModuleDefinition module)
    {
        var type = module.GetType("YoYoStudio.Resources.GMProjectAdapter");
        foreach (string name in new[] { "GetLicensedOptions", "VerifyLicensedOptions" })
        {
            var method = type?.Methods.SingleOrDefault(m => m.Name == name);
            if (method?.Body == null) return false;
            var anchor = method.Body.Instructions.SingleOrDefault(i =>
                i.Operand is MethodReference m && m.Name == "get_TestPlatform_Actual");
            // A lista ja esta na pilha: licensed.Add(TestPlatform_Actual.ToLower()).
            var add = anchor?.Next?.Next;
            if (anchor == null || anchor.Next?.Operand is not MethodReference lower || lower.Name != "ToLower" ||
                add?.Operand is not MethodReference append || append.Name != "Add" ||
                append.DeclaringType is not GenericInstanceType list ||
                list.ElementType.FullName != "System.Collections.Generic.List`1" ||
                list.GenericArguments.Single().FullName != "System.String") return false;
            var il = method.Body.GetILProcessor();
            il.InsertBefore(anchor, il.Create(OpCodes.Dup));
            il.InsertBefore(anchor, il.Create(OpCodes.Ldstr, "android"));
            il.InsertBefore(anchor, il.Create(OpCodes.Callvirt, append));
            method.Body.MaxStackSize += 2;
        }
        return true;
    }
}
