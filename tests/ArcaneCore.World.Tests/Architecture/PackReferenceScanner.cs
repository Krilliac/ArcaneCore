using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace ArcaneCore.World.Tests.Architecture;

/// <summary>
/// Reads the compiled world assembly (System.Reflection.Metadata, no loading) and lists every reference from a core type to a
/// pack type: base types, interfaces, field, method, property and event signatures, generic constraints, attributes, locals and
/// every token in every method body. A type belongs to the pack when its outermost declaring type's namespace is
/// <see cref="PackNamespace"/> or below it; compiler-generated closures are nested in their declaring type, so a lambda in a core
/// method counts as core. What the compiler emitted is the truth: a <c>var</c> or a member chain that names no pack type in the
/// source still shows up here.
/// </summary>
internal sealed class PackReferenceScanner
{
    private readonly MetadataReader _md;
    private readonly string _packNamespace;
    private readonly Dictionary<TypeDefinitionHandle, TypeDefinitionHandle> _outermost = [];
    private readonly Provider _provider;

    private PackReferenceScanner(MetadataReader md, string packNamespace)
    {
        _md = md;
        _packNamespace = packNamespace;
        _provider = new Provider();
    }

    /// <summary>The namespace of the Playerbots pack.</summary>
    public const string PackNamespace = "ArcaneCore.World.Playerbots";

    /// <summary>How many types were read (a scan that read nothing proves nothing).</summary>
    public int TypesScanned { get; private set; }

    /// <summary>How many method bodies were walked.</summary>
    public int BodiesScanned { get; private set; }

    /// <summary>
    /// Scan <paramref name="assemblyPath"/>: for every type whose <paramref name="isSubject"/> is true (by outermost full name),
    /// the pack types it references, keyed by that outermost type's full name.
    /// </summary>
    public static (PackReferenceScanner Scanner, SortedDictionary<string, SortedSet<string>> References) Scan(
        string assemblyPath, Func<bool, bool> isSubject, string packNamespace = PackNamespace)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        MetadataReader md = pe.GetMetadataReader();
        var scanner = new PackReferenceScanner(md, packNamespace);
        var found = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (TypeDefinitionHandle handle in md.TypeDefinitions)
        {
            TypeDefinitionHandle outer = scanner.Outermost(handle);
            string outerName = scanner.FullName(outer);
            if (outerName is "<Module>" || outerName.StartsWith("<PrivateImplementationDetails>", StringComparison.Ordinal))
            {
                continue;
            }

            if (!isSubject(scanner.IsPack(handle)))
            {
                continue;
            }

            scanner.TypesScanned++;
            var refs = new HashSet<TypeDefinitionHandle>();
            scanner.CollectType(pe, handle, refs);
            foreach (TypeDefinitionHandle r in refs)
            {
                if (!scanner.IsPack(r) || scanner.Outermost(r) == outer)
                {
                    continue;
                }

                if (!found.TryGetValue(outerName, out SortedSet<string>? set))
                {
                    found[outerName] = set = new SortedSet<string>(StringComparer.Ordinal);
                }

                set.Add(scanner.FullName(r));
            }
        }

        return (scanner, found);
    }

    private bool IsPack(TypeDefinitionHandle handle)
    {
        string ns = _md.GetString(_md.GetTypeDefinition(Outermost(handle)).Namespace);
        return ns == _packNamespace || ns.StartsWith(_packNamespace + ".", StringComparison.Ordinal);
    }

    private TypeDefinitionHandle Outermost(TypeDefinitionHandle handle)
    {
        if (_outermost.TryGetValue(handle, out TypeDefinitionHandle cached))
        {
            return cached;
        }

        TypeDefinitionHandle current = handle;
        while (true)
        {
            TypeDefinitionHandle parent = _md.GetTypeDefinition(current).GetDeclaringType();
            if (parent.IsNil)
            {
                break;
            }

            current = parent;
        }

        _outermost[handle] = current;
        return current;
    }

    private string FullName(TypeDefinitionHandle handle)
    {
        TypeDefinition type = _md.GetTypeDefinition(handle);
        string name = _md.GetString(type.Name);
        TypeDefinitionHandle parent = type.GetDeclaringType();
        if (!parent.IsNil)
        {
            return FullName(parent) + "+" + name;
        }

        string ns = _md.GetString(type.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    private void CollectType(PEReader pe, TypeDefinitionHandle handle, HashSet<TypeDefinitionHandle> refs)
    {
        TypeDefinition type = _md.GetTypeDefinition(handle);
        Entity(type.BaseType, refs);
        foreach (InterfaceImplementationHandle i in type.GetInterfaceImplementations())
        {
            Entity(_md.GetInterfaceImplementation(i).Interface, refs);
        }

        Attributes(type.GetCustomAttributes(), refs);
        GenericParameters(type.GetGenericParameters(), refs);
        foreach (FieldDefinitionHandle f in type.GetFields())
        {
            FieldDefinition field = _md.GetFieldDefinition(f);
            Add(field.DecodeSignature(_provider, null), refs);
            Attributes(field.GetCustomAttributes(), refs);
        }

        foreach (PropertyDefinitionHandle p in type.GetProperties())
        {
            PropertyDefinition property = _md.GetPropertyDefinition(p);
            MethodSignature<Refs> sig = property.DecodeSignature(_provider, null);
            Signature(sig, refs);
            Attributes(property.GetCustomAttributes(), refs);
        }

        foreach (EventDefinitionHandle e in type.GetEvents())
        {
            EventDefinition ev = _md.GetEventDefinition(e);
            Entity(ev.Type, refs);
            Attributes(ev.GetCustomAttributes(), refs);
        }

        foreach (MethodDefinitionHandle m in type.GetMethods())
        {
            MethodDefinition method = _md.GetMethodDefinition(m);
            Signature(method.DecodeSignature(_provider, null), refs);
            Attributes(method.GetCustomAttributes(), refs);
            GenericParameters(method.GetGenericParameters(), refs);
            foreach (ParameterHandle ph in method.GetParameters())
            {
                Attributes(_md.GetParameter(ph).GetCustomAttributes(), refs);
            }

            if (method.RelativeVirtualAddress == 0)
            {
                continue;
            }

            MethodBodyBlock body = pe.GetMethodBody(method.RelativeVirtualAddress);
            BodiesScanned++;
            if (!body.LocalSignature.IsNil)
            {
                StandaloneSignature locals = _md.GetStandaloneSignature(body.LocalSignature);
                foreach (Refs local in locals.DecodeLocalSignature(_provider, null))
                {
                    Add(local, refs);
                }
            }

            foreach (ExceptionRegion region in body.ExceptionRegions)
            {
                Entity(region.CatchType, refs);
            }

            Il(body.GetILReader(), refs);
        }
    }

    private void Il(BlobReader il, HashSet<TypeDefinitionHandle> refs)
    {
        while (il.RemainingBytes > 0)
        {
            ushort code = il.ReadByte();
            if (code == 0xFE)
            {
                code = (ushort)(0xFE00 | il.ReadByte());
            }

            switch (OperandOf((ILOpCode)code))
            {
                case Operand.None:
                    break;
                case Operand.Int8:
                    il.ReadByte();
                    break;
                case Operand.Int16:
                    il.ReadInt16();
                    break;
                case Operand.Int32:
                    il.ReadInt32();
                    break;
                case Operand.Int64:
                    il.ReadInt64();
                    break;
                case Operand.Token:
                    Token(il.ReadInt32(), refs);
                    break;
                case Operand.Switch:
                    int count = il.ReadInt32();
                    for (int i = 0; i < count; i++)
                    {
                        il.ReadInt32();
                    }

                    break;
            }
        }
    }

    private void Token(int token, HashSet<TypeDefinitionHandle> refs)
    {
        if ((token >>> 24) == 0x70)
        {
            return; // a user string (ldstr)
        }

        EntityHandle handle = MetadataTokens.EntityHandle(token);
        Entity(handle, refs);
    }

    private void Entity(EntityHandle handle, HashSet<TypeDefinitionHandle> refs)
    {
        if (handle.IsNil)
        {
            return;
        }

        switch (handle.Kind)
        {
            case HandleKind.TypeDefinition:
                refs.Add((TypeDefinitionHandle)handle);
                break;
            case HandleKind.TypeSpecification:
                Add(_md.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(_provider, null), refs);
                break;
            case HandleKind.MethodDefinition:
                refs.Add(_md.GetMethodDefinition((MethodDefinitionHandle)handle).GetDeclaringType());
                break;
            case HandleKind.FieldDefinition:
                refs.Add(_md.GetFieldDefinition((FieldDefinitionHandle)handle).GetDeclaringType());
                break;
            case HandleKind.MemberReference:
                MemberReference member = _md.GetMemberReference((MemberReferenceHandle)handle);
                Entity(member.Parent, refs);
                if (member.GetKind() == MemberReferenceKind.Method)
                {
                    Signature(member.DecodeMethodSignature(_provider, null), refs);
                }
                else
                {
                    Add(member.DecodeFieldSignature(_provider, null), refs);
                }

                break;
            case HandleKind.MethodSpecification:
                MethodSpecification spec = _md.GetMethodSpecification((MethodSpecificationHandle)handle);
                Entity(spec.Method, refs);
                foreach (Refs arg in spec.DecodeSignature(_provider, null))
                {
                    Add(arg, refs);
                }

                break;
            case HandleKind.StandaloneSignature:
                StandaloneSignature standalone = _md.GetStandaloneSignature((StandaloneSignatureHandle)handle);
                if (standalone.GetKind() == StandaloneSignatureKind.Method)
                {
                    Signature(standalone.DecodeMethodSignature(_provider, null), refs);
                }

                break;
        }
    }

    private void Attributes(CustomAttributeHandleCollection attributes, HashSet<TypeDefinitionHandle> refs)
    {
        foreach (CustomAttributeHandle a in attributes)
        {
            Entity(_md.GetCustomAttribute(a).Constructor, refs);
        }
    }

    private void GenericParameters(GenericParameterHandleCollection parameters, HashSet<TypeDefinitionHandle> refs)
    {
        foreach (GenericParameterHandle g in parameters)
        {
            foreach (GenericParameterConstraintHandle c in _md.GetGenericParameter(g).GetConstraints())
            {
                Entity(_md.GetGenericParameterConstraint(c).Type, refs);
            }
        }
    }

    private static void Signature(MethodSignature<Refs> sig, HashSet<TypeDefinitionHandle> refs)
    {
        Add(sig.ReturnType, refs);
        foreach (Refs p in sig.ParameterTypes)
        {
            Add(p, refs);
        }
    }

    private static void Add(Refs r, HashSet<TypeDefinitionHandle> refs) => refs.UnionWith(r.Types);

    private enum Operand
    {
        None,
        Int8,
        Int16,
        Int32,
        Int64,
        Token,
        Switch,
    }

    private static Operand OperandOf(ILOpCode op) => op switch
    {
        ILOpCode.Br_s or ILOpCode.Brfalse_s or ILOpCode.Brtrue_s or ILOpCode.Beq_s or ILOpCode.Bge_s or ILOpCode.Bgt_s
            or ILOpCode.Ble_s or ILOpCode.Blt_s or ILOpCode.Bne_un_s or ILOpCode.Bge_un_s or ILOpCode.Bgt_un_s
            or ILOpCode.Ble_un_s or ILOpCode.Blt_un_s or ILOpCode.Leave_s or ILOpCode.Ldarg_s or ILOpCode.Ldarga_s
            or ILOpCode.Starg_s or ILOpCode.Ldloc_s or ILOpCode.Ldloca_s or ILOpCode.Stloc_s or ILOpCode.Ldc_i4_s
            or ILOpCode.Unaligned => Operand.Int8,
        ILOpCode.Ldarg or ILOpCode.Ldarga or ILOpCode.Starg or ILOpCode.Ldloc or ILOpCode.Ldloca or ILOpCode.Stloc => Operand.Int16,
        ILOpCode.Br or ILOpCode.Brfalse or ILOpCode.Brtrue or ILOpCode.Beq or ILOpCode.Bge or ILOpCode.Bgt or ILOpCode.Ble
            or ILOpCode.Blt or ILOpCode.Bne_un or ILOpCode.Bge_un or ILOpCode.Bgt_un or ILOpCode.Ble_un or ILOpCode.Blt_un
            or ILOpCode.Leave or ILOpCode.Ldc_i4 or ILOpCode.Ldc_r4 => Operand.Int32,
        ILOpCode.Ldc_i8 or ILOpCode.Ldc_r8 => Operand.Int64,
        ILOpCode.Switch => Operand.Switch,
        ILOpCode.Call or ILOpCode.Callvirt or ILOpCode.Newobj or ILOpCode.Ldftn or ILOpCode.Ldvirtftn or ILOpCode.Jmp
            or ILOpCode.Ldfld or ILOpCode.Ldflda or ILOpCode.Stfld or ILOpCode.Ldsfld or ILOpCode.Ldsflda or ILOpCode.Stsfld
            or ILOpCode.Box or ILOpCode.Unbox or ILOpCode.Unbox_any or ILOpCode.Castclass or ILOpCode.Isinst or ILOpCode.Newarr
            or ILOpCode.Ldelema or ILOpCode.Ldelem or ILOpCode.Stelem or ILOpCode.Ldobj or ILOpCode.Stobj or ILOpCode.Cpobj
            or ILOpCode.Initobj or ILOpCode.Sizeof or ILOpCode.Mkrefany or ILOpCode.Refanyval or ILOpCode.Ldtoken
            or ILOpCode.Constrained or ILOpCode.Calli or ILOpCode.Ldstr => Operand.Token,
        _ => Operand.None,
    };

    /// <summary>The world-assembly types one signature mentions.</summary>
    private sealed class Refs
    {
        public static readonly Refs Empty = new([]);

        public Refs(ImmutableArray<TypeDefinitionHandle> types) => Types = types;

        public ImmutableArray<TypeDefinitionHandle> Types { get; }

        public static Refs Of(params Refs[] parts)
        {
            ImmutableArray<TypeDefinitionHandle>.Builder all = ImmutableArray.CreateBuilder<TypeDefinitionHandle>();
            foreach (Refs p in parts)
            {
                all.AddRange(p.Types);
            }

            return all.Count == 0 ? Empty : new Refs(all.ToImmutable());
        }
    }

    private sealed class Provider : ISignatureTypeProvider<Refs, object?>
    {
        public Refs GetArrayType(Refs elementType, ArrayShape shape) => elementType;

        public Refs GetByReferenceType(Refs elementType) => elementType;

        public Refs GetFunctionPointerType(MethodSignature<Refs> signature) => Refs.Of([signature.ReturnType, .. signature.ParameterTypes]);

        public Refs GetGenericInstantiation(Refs genericType, ImmutableArray<Refs> typeArguments) => Refs.Of([genericType, .. typeArguments]);

        public Refs GetGenericMethodParameter(object? genericContext, int index) => Refs.Empty;

        public Refs GetGenericTypeParameter(object? genericContext, int index) => Refs.Empty;

        public Refs GetModifiedType(Refs modifier, Refs unmodifiedType, bool isRequired) => Refs.Of(modifier, unmodifiedType);

        public Refs GetPinnedType(Refs elementType) => elementType;

        public Refs GetPointerType(Refs elementType) => elementType;

        public Refs GetPrimitiveType(PrimitiveTypeCode typeCode) => Refs.Empty;

        public Refs GetSZArrayType(Refs elementType) => elementType;

        public Refs GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => new([handle]);

        public Refs GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => Refs.Empty;

        public Refs GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
            => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    }
}
