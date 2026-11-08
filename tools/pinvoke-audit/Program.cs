// Varredura estatica dos assemblies da IDE (System.Reflection.Metadata).
// Uso: dotnet run -c Release -- <pasta-da-IDE> <pasta-lib-do-runtime-android> <pasta-saida>
// Saidas (TSV, uma linha por fato):
//   assemblies.tsv  asm  kind(core|blocked-by-launcher|ide)  path
//   pinvoke.tsv     asm  type  method  module  entrypoint
//   memberrefs.tsv  asm  callerType  callerMethod  targetAsm  targetType  member  opcode  (refs a outros assemblies, em IL)
//   calls.tsv       asm  callerType::callerMethod  calleeAsm  calleeType::callee  opcode (inclui MethodDefs locais)
//   strings.tsv     asm  type  method  string   (ldstr)
//   asmrefs.tsv     asm  referencedAsm
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

var ideDir = args[0]; var rtDir = args[1]; var outDir = args[2];
Directory.CreateDirectory(outDir);
var rt = new HashSet<string>(Directory.GetFiles(rtDir, "*.dll").Select(Path.GetFileNameWithoutExtension), StringComparer.OrdinalIgnoreCase);
rt.Add("System.Private.CoreLib");
// Launcher.ResolveFromIdeDir recusa estes nomes (devolve null).
bool Blocked(string n) => n.StartsWith("System.") || n.StartsWith("Microsoft.") || n is "netstandard" or "mscorlib" or "WindowsBase";

using var wAsm = new StreamWriter(Path.Combine(outDir, "assemblies.tsv"));
using var wPi = new StreamWriter(Path.Combine(outDir, "pinvoke.tsv"));
using var wMr = new StreamWriter(Path.Combine(outDir, "memberrefs.tsv"));
using var wCall = new StreamWriter(Path.Combine(outDir, "calls.tsv"));
using var wStr = new StreamWriter(Path.Combine(outDir, "strings.tsv"));
using var wRef = new StreamWriter(Path.Combine(outDir, "asmrefs.tsv"));

var files = Directory.GetFiles(ideDir, "*.dll").Concat(Directory.GetFiles(ideDir, "*.exe"))
    .Concat(Directory.GetFiles(Path.Combine(ideDir, "IDENativeLib"), "*.dll"));
foreach (var f in files)
{
    PEReader pe; MetadataReader md;
    try { pe = new PEReader(File.OpenRead(f)); if (!pe.HasMetadata) continue; md = pe.GetMetadataReader(); if (!md.IsAssembly) continue; }
    catch { continue; }
    var asm = md.GetString(md.GetAssemblyDefinition().Name);
    string kind = rt.Contains(asm) ? "core" : Blocked(asm) ? "blocked-by-launcher" : "ide";
    wAsm.WriteLine($"{asm}\t{kind}\t{Path.GetRelativePath(ideDir, f)}");
    if (kind == "core") { pe.Dispose(); continue; }
    foreach (var h in md.AssemblyReferences) wRef.WriteLine($"{asm}\t{md.GetString(md.GetAssemblyReference(h).Name)}");

    string TypeName(EntityHandle h)
    {
        switch (h.Kind)
        {
            case HandleKind.TypeDefinition:
            {
                var td = md.GetTypeDefinition((TypeDefinitionHandle)h);
                var n = md.GetString(td.Name); var ns = md.GetString(td.Namespace);
                var decl = td.GetDeclaringType();
                if (!decl.IsNil) return TypeName(decl) + "/" + n;
                return ns.Length > 0 ? ns + "." + n : n;
            }
            case HandleKind.TypeReference:
            {
                var tr = md.GetTypeReference((TypeReferenceHandle)h);
                var n = md.GetString(tr.Name); var ns = md.GetString(tr.Namespace);
                if (tr.ResolutionScope.Kind == HandleKind.TypeReference) return TypeName((TypeReferenceHandle)tr.ResolutionScope) + "/" + n;
                return ns.Length > 0 ? ns + "." + n : n;
            }
            case HandleKind.TypeSpecification:
            {
                var br = md.GetBlobReader(md.GetTypeSpecification((TypeSpecificationHandle)h).Signature);
                if (br.ReadSignatureTypeCode() == SignatureTypeCode.GenericTypeInstance) { br.ReadByte(); return TypeName(br.ReadTypeHandle()); }
                return "<spec>";
            }
        }
        return "?";
    }
    string ScopeAsm(EntityHandle h)
    {
        if (h.Kind == HandleKind.TypeSpecification)
        {
            var br = md.GetBlobReader(md.GetTypeSpecification((TypeSpecificationHandle)h).Signature);
            if (br.ReadSignatureTypeCode() == SignatureTypeCode.GenericTypeInstance) { br.ReadByte(); return ScopeAsm(br.ReadTypeHandle()); }
            return asm;
        }
        if (h.Kind != HandleKind.TypeReference) return asm;
        var s = md.GetTypeReference((TypeReferenceHandle)h).ResolutionScope;
        if (s.Kind == HandleKind.AssemblyReference) return md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)s).Name);
        if (s.Kind == HandleKind.TypeReference) return ScopeAsm((TypeReferenceHandle)s);
        return asm;
    }
    (string a, string t, string m) Target(EntityHandle tok)
    {
        switch (tok.Kind)
        {
            case HandleKind.MethodDefinition:
            {
                var m = md.GetMethodDefinition((MethodDefinitionHandle)tok);
                return (asm, TypeName(m.GetDeclaringType()), md.GetString(m.Name));
            }
            case HandleKind.FieldDefinition:
            {
                var fd = md.GetFieldDefinition((FieldDefinitionHandle)tok);
                return (asm, TypeName(fd.GetDeclaringType()), md.GetString(fd.Name));
            }
            case HandleKind.MemberReference:
            {
                var mr = md.GetMemberReference((MemberReferenceHandle)tok);
                var p = mr.Parent;
                if (p.Kind == HandleKind.MethodDefinition) return Target(p);
                if (p.Kind == HandleKind.ModuleReference) return (asm, "<module>", md.GetString(mr.Name));
                return (ScopeAsm(p), TypeName(p), md.GetString(mr.Name));
            }
            case HandleKind.MethodSpecification:
                return Target(md.GetMethodSpecification((MethodSpecificationHandle)tok).Method);
            case HandleKind.TypeReference: case HandleKind.TypeDefinition: case HandleKind.TypeSpecification:
                return (ScopeAsm(tok), TypeName(tok), "<type>");
        }
        return (asm, "?", "?");
    }

    foreach (var th in md.TypeDefinitions)
    {
        var td = md.GetTypeDefinition(th);
        var tn = TypeName(th);
        foreach (var mh in td.GetMethods())
        {
            var m = md.GetMethodDefinition(mh);
            var mn = md.GetString(m.Name);
            var imp = m.GetImport();
            if (!imp.Module.IsNil)
            {
                var mod = md.GetString(md.GetModuleReference(imp.Module).Name);
                var ep = imp.Name.IsNil ? mn : md.GetString(imp.Name);
                wPi.WriteLine($"{asm}\t{tn}\t{mn}\t{mod}\t{ep}");
            }
            if (m.RelativeVirtualAddress == 0) continue;
            MethodBodyBlock body;
            try { body = pe.GetMethodBody(m.RelativeVirtualAddress); } catch { continue; }
            var il = body.GetILReader();
            try
            {
                while (il.RemainingBytes > 0)
                {
                    int b = il.ReadByte();
                    ILOpCode op = b == 0xFE ? (ILOpCode)(0xFE00 | il.ReadByte()) : (ILOpCode)b;
                    switch (OperandSize(op))
                    {
                        case 0: break;
                        case 1: il.ReadByte(); break;
                        case 2: il.ReadInt16(); break;
                        case 8: il.ReadInt64(); break;
                        case -1: { int n = il.ReadInt32(); for (int i = 0; i < n; i++) il.ReadInt32(); break; }
                        case 4:
                        {
                            int tok = il.ReadInt32();
                            if (op == ILOpCode.Ldstr)
                            {
                                var s = md.GetUserString(MetadataTokens.UserStringHandle(tok & 0xFFFFFF));
                                wStr.WriteLine($"{asm}\t{tn}\t{mn}\t{Esc(s)}");
                            }
                            else if (IsTokenOp(op))
                            {
                                var h = MetadataTokens.EntityHandle(tok);
                                var (ta, tt, tm) = Target(h);
                                if (h.Kind is HandleKind.MethodDefinition or HandleKind.MemberReference or HandleKind.MethodSpecification)
                                    wCall.WriteLine($"{asm}\t{tn}::{mn}\t{ta}\t{tt}::{tm}\t{op}");
                                if (ta != asm)
                                    wMr.WriteLine($"{asm}\t{tn}\t{mn}\t{ta}\t{tt}\t{tm}\t{op}");
                            }
                            break;
                        }
                    }
                }
            }
            catch { }
        }
    }
    pe.Dispose();
}

static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");
static bool IsTokenOp(ILOpCode op) => op is ILOpCode.Call or ILOpCode.Callvirt or ILOpCode.Newobj or ILOpCode.Ldftn or ILOpCode.Ldvirtftn
    or ILOpCode.Ldsfld or ILOpCode.Ldfld or ILOpCode.Stsfld or ILOpCode.Stfld or ILOpCode.Ldsflda or ILOpCode.Ldflda
    or ILOpCode.Newarr or ILOpCode.Castclass or ILOpCode.Isinst or ILOpCode.Ldtoken or ILOpCode.Box or ILOpCode.Initobj or ILOpCode.Unbox_any;
static int OperandSize(ILOpCode op)
{
    switch (op)
    {
        case ILOpCode.Switch: return -1;
        case ILOpCode.Ldarg_s: case ILOpCode.Ldarga_s: case ILOpCode.Starg_s: case ILOpCode.Ldloc_s: case ILOpCode.Ldloca_s: case ILOpCode.Stloc_s:
        case ILOpCode.Ldc_i4_s: case ILOpCode.Br_s: case ILOpCode.Brfalse_s: case ILOpCode.Brtrue_s: case ILOpCode.Beq_s: case ILOpCode.Bge_s:
        case ILOpCode.Bgt_s: case ILOpCode.Ble_s: case ILOpCode.Blt_s: case ILOpCode.Bne_un_s: case ILOpCode.Bge_un_s: case ILOpCode.Bgt_un_s:
        case ILOpCode.Ble_un_s: case ILOpCode.Blt_un_s: case ILOpCode.Leave_s: case ILOpCode.Unaligned:
            return 1;
        case ILOpCode.Ldarg: case ILOpCode.Ldarga: case ILOpCode.Starg: case ILOpCode.Ldloc: case ILOpCode.Ldloca: case ILOpCode.Stloc:
            return 2;
        case ILOpCode.Ldc_i8: case ILOpCode.Ldc_r8: return 8;
        case ILOpCode.Ldc_i4: case ILOpCode.Ldc_r4: case ILOpCode.Br: case ILOpCode.Brfalse: case ILOpCode.Brtrue: case ILOpCode.Beq: case ILOpCode.Bge:
        case ILOpCode.Bgt: case ILOpCode.Ble: case ILOpCode.Blt: case ILOpCode.Bne_un: case ILOpCode.Bge_un: case ILOpCode.Bgt_un: case ILOpCode.Ble_un:
        case ILOpCode.Blt_un: case ILOpCode.Leave: case ILOpCode.Jmp: case ILOpCode.Call: case ILOpCode.Calli: case ILOpCode.Callvirt:
        case ILOpCode.Cpobj: case ILOpCode.Ldobj: case ILOpCode.Ldstr: case ILOpCode.Newobj: case ILOpCode.Castclass: case ILOpCode.Isinst:
        case ILOpCode.Unbox: case ILOpCode.Ldfld: case ILOpCode.Ldflda: case ILOpCode.Stfld: case ILOpCode.Ldsfld: case ILOpCode.Ldsflda:
        case ILOpCode.Stsfld: case ILOpCode.Stobj: case ILOpCode.Box: case ILOpCode.Newarr: case ILOpCode.Ldelema: case ILOpCode.Ldelem:
        case ILOpCode.Stelem: case ILOpCode.Unbox_any: case ILOpCode.Refanyval: case ILOpCode.Mkrefany: case ILOpCode.Ldtoken:
        case ILOpCode.Ldftn: case ILOpCode.Ldvirtftn: case ILOpCode.Initobj: case ILOpCode.Constrained: case ILOpCode.Sizeof:
            return 4;
    }
    return 0;
}
