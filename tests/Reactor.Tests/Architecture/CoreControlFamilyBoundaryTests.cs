using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Microsoft.UI.Reactor.Core;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.Architecture;

/// <summary>
/// Issue #498 — control-family isolation boundary guard.
///
/// The Reactor core (<c>Microsoft.UI.Reactor.Core</c>) and host
/// (<c>Microsoft.UI.Reactor.Hosting</c>) must know nothing about specific
/// control families. In particular they must carry <b>zero static type
/// references</b> into the Charting and Docking subsystems — otherwise the
/// AOT trimmer can't drop those subsystems from apps that never render a
/// chart / use docking, leaking their code (~7.8 KB for charting) into every
/// retail binary.
///
/// This test reads the compiled <c>Reactor.dll</c> metadata and decodes the IL
/// of every method defined on a Core/Hosting type, plus every field / method
/// signature, asserting none references a type whose namespace lives under the
/// forbidden subsystems. It is the regression pin for the decoupling: if a
/// future change reintroduces e.g. a <c>D3Color</c> allocation or a
/// <c>D3Charts</c> static write from core, this fails with the exact offending
/// member.
/// </summary>
public class CoreControlFamilyBoundaryTests
{
    private static readonly string[] SourceNamespacePrefixes =
    {
        "Microsoft.UI.Reactor.Core",
        "Microsoft.UI.Reactor.Hosting",
    };

    private static readonly string[] ForbiddenNamespacePrefixes =
    {
        "Microsoft.UI.Reactor.Charting",
        "Microsoft.UI.Reactor.Docking",
    };

    [Fact]
    public void CoreAndHosting_HaveNoStaticReferencesIntoChartingOrDocking()
    {
        var (violations, scannedTypes) = Scan(ForbiddenNamespacePrefixes);

        // Guard against a vacuous pass (wrong assembly / scanning nothing).
        Assert.True(scannedTypes > 50,
            $"Boundary scan only visited {scannedTypes} Core/Hosting types — expected the full set. " +
            "The scan is likely misconfigured and would pass vacuously.");

        Assert.True(
            violations.Count == 0,
            "Reactor Core/Hosting must not statically reference Charting/Docking types (issue #498). " +
            "Offending references:\n  " + string.Join("\n  ", violations));
    }

    /// <summary>
    /// Meta-test: proves the IL/signature scanner actually resolves references
    /// (i.e. the boundary test above is not passing vacuously). Reactor core
    /// provably constructs WinUI controls, so a scan for
    /// <c>Microsoft.UI.Xaml</c> references must find some.
    /// </summary>
    [Fact]
    public void Scanner_DetectsKnownReferences_NotVacuous()
    {
        var (violations, _) = Scan(new[] { "Microsoft.UI.Xaml" });
        Assert.NotEmpty(violations);
    }

    private (SortedSet<string> Violations, int ScannedTypes) Scan(string[] forbiddenPrefixes)
    {
        var assemblyPath = typeof(Element).Assembly.Location;
        Assert.False(string.IsNullOrEmpty(assemblyPath), "Could not locate Reactor.dll on disk.");

        var opcodes = BuildOpCodeTable();
        var violations = new SortedSet<string>(StringComparer.Ordinal);

        using var stream = global::System.IO.File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();

        var sigProvider = new NamespaceCollectingSignatureProvider(forbiddenPrefixes);
        int scannedTypes = 0;

        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var typeDef = reader.GetTypeDefinition(typeHandle);
            var rootNs = GetRootNamespace(reader, typeHandle);
            if (!StartsWithAny(rootNs, SourceNamespacePrefixes))
                continue;

            scannedTypes++;
            var typeName = GetReadableTypeName(reader, typeHandle);

            // ── Field signatures ──────────────────────────────────────────
            foreach (var field in typeDef.GetFields().Select(reader.GetFieldDefinition))
            {
                sigProvider.Hits.Clear();
                field.DecodeSignature(sigProvider, null);
                foreach (var ns in sigProvider.Hits)
                    violations.Add($"{typeName}.{reader.GetString(field.Name)} (field type) -> {ns}");
            }

            // ── Method signatures + IL bodies ─────────────────────────────
            foreach (var (method, methodName) in typeDef.GetMethods()
                .Select(reader.GetMethodDefinition)
                .Select(m => (Method: m, Name: reader.GetString(m.Name))))
            {
                sigProvider.Hits.Clear();
                method.DecodeSignature(sigProvider, null);
                foreach (var ns in sigProvider.Hits)
                    violations.Add($"{typeName}.{methodName} (signature) -> {ns}");

                if (method.RelativeVirtualAddress == 0)
                    continue;

                var body = pe.GetMethodBody(method.RelativeVirtualAddress);
                ScanMethodBody(reader, body.GetILBytes(), opcodes, forbiddenPrefixes, typeName, methodName, violations);
            }
        }

        return (violations, scannedTypes);
    }

    private void ScanMethodBody(
        MetadataReader reader, byte[]? il, Dictionary<int, OperandKind> opcodes,
        string[] forbiddenPrefixes, string typeName, string methodName, SortedSet<string> violations)
    {
        if (il is null || il.Length == 0)
            return;

        int pos = 0;
        while (pos < il.Length)
        {
            int opByte = il[pos++];
            int opKey = opByte;
            if (opByte == 0xFE && pos < il.Length)
                opKey = 0xFE00 | il[pos++];

            if (!opcodes.TryGetValue(opKey, out var kind))
                return; // Unknown opcode — stop decoding this method defensively.

            switch (kind)
            {
                case OperandKind.None:
                    break;
                case OperandKind.Int8:
                case OperandKind.ShortVar:
                case OperandKind.ShortBr:
                    pos += 1;
                    break;
                case OperandKind.Var:
                    pos += 2;
                    break;
                case OperandKind.Int32:
                case OperandKind.Float32:
                case OperandKind.Br:
                case OperandKind.StringToken:
                case OperandKind.SigToken:
                    pos += 4;
                    break;
                case OperandKind.Int64:
                case OperandKind.Float64:
                    pos += 8;
                    break;
                case OperandKind.Token:
                {
                    if (pos + 4 > il.Length) return;
                    int token = BitConverter.ToInt32(il, pos);
                    pos += 4;
                    var ns = ResolveTokenNamespace(reader, token);
                    if (ns is not null && StartsWithAny(ns, forbiddenPrefixes))
                        violations.Add($"{typeName}.{methodName} (IL) -> {ns}");
                    break;
                }
                case OperandKind.Switch:
                {
                    if (pos + 4 > il.Length) return;
                    int n = BitConverter.ToInt32(il, pos);
                    pos += 4 + (4 * n);
                    break;
                }
            }
        }
    }

    private static string? ResolveTokenNamespace(MetadataReader reader, int token)
    {
        EntityHandle handle;
        try
        {
            handle = MetadataTokens.EntityHandle(token);
        }
        catch (ArgumentException)
        {
            // Not a valid metadata token (e.g. user-string heap handle) — skip.
            return null;
        }

        return GetRootNamespaceForEntity(reader, handle);
    }

    private static string? GetRootNamespaceForEntity(MetadataReader reader, EntityHandle handle)
    {
        switch (handle.Kind)
        {
            case HandleKind.TypeDefinition:
                return GetRootNamespace(reader, (TypeDefinitionHandle)handle);
            case HandleKind.TypeReference:
                return GetRootNamespace(reader, (TypeReferenceHandle)handle);
            case HandleKind.MethodDefinition:
            {
                var m = reader.GetMethodDefinition((MethodDefinitionHandle)handle);
                return GetRootNamespace(reader, m.GetDeclaringType());
            }
            case HandleKind.FieldDefinition:
            {
                var f = reader.GetFieldDefinition((FieldDefinitionHandle)handle);
                return GetRootNamespace(reader, f.GetDeclaringType());
            }
            case HandleKind.MemberReference:
            {
                var mr = reader.GetMemberReference((MemberReferenceHandle)handle);
                return GetRootNamespaceForEntity(reader, mr.Parent);
            }
            case HandleKind.MethodSpecification:
            {
                var ms = reader.GetMethodSpecification((MethodSpecificationHandle)handle);
                return GetRootNamespaceForEntity(reader, ms.Method);
            }
            default:
                // TypeSpecification (generic instantiations / arrays) carry their
                // referenced types in a blob; those are covered by signature
                // decoding elsewhere. Skip here.
                return null;
        }
    }

    private static string GetRootNamespace(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var td = reader.GetTypeDefinition(handle);
        while (td.IsNested)
        {
            handle = td.GetDeclaringType();
            td = reader.GetTypeDefinition(handle);
        }
        return reader.GetString(td.Namespace);
    }

    private static string GetRootNamespace(MetadataReader reader, TypeReferenceHandle handle)
    {
        var tr = reader.GetTypeReference(handle);
        while (tr.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            tr = reader.GetTypeReference((TypeReferenceHandle)tr.ResolutionScope);
        }
        return reader.GetString(tr.Namespace);
    }

    private static string GetReadableTypeName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var td = reader.GetTypeDefinition(handle);
        var name = reader.GetString(td.Name);
        var ns = reader.GetString(td.Namespace);
        return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
    }

    private static bool StartsWithAny(string value, string[] prefixes)
    {
        return prefixes.Any(p =>
            value.Equals(p, StringComparison.Ordinal) ||
            value.StartsWith(p + ".", StringComparison.Ordinal));
    }

    // ════════════════════════════════════════════════════════════════════
    //  IL operand decoding — table sourced from the BCL's own OpCode data so
    //  we never hand-maintain operand sizes.
    // ════════════════════════════════════════════════════════════════════

    private enum OperandKind
    {
        None, Int8, Var, ShortVar, ShortBr, Br, Int32, Int64, Float32, Float64,
        Token, StringToken, SigToken, Switch,
    }

    private static Dictionary<int, OperandKind> BuildOpCodeTable()
    {
        var table = new Dictionary<int, OperandKind>();
        var opCodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => f.GetValue(null))
            .OfType<OpCode>();
        foreach (var op in opCodes)
        {
            ushort raw = unchecked((ushort)op.Value);
            int key = op.Size == 2 ? (0xFE00 | (raw & 0xFF)) : (raw & 0xFF);
            table[key] = MapOperand(op.OperandType);
        }
        return table;
    }

    private static OperandKind MapOperand(OperandType type) => type switch
    {
        OperandType.InlineNone => OperandKind.None,
        OperandType.ShortInlineI => OperandKind.Int8,
        OperandType.ShortInlineVar => OperandKind.ShortVar,
        OperandType.ShortInlineBrTarget => OperandKind.ShortBr,
        OperandType.InlineVar => OperandKind.Var,
        OperandType.InlineI => OperandKind.Int32,
        OperandType.ShortInlineR => OperandKind.Float32,
        OperandType.InlineBrTarget => OperandKind.Br,
        OperandType.InlineI8 => OperandKind.Int64,
        OperandType.InlineR => OperandKind.Float64,
        OperandType.InlineField => OperandKind.Token,
        OperandType.InlineMethod => OperandKind.Token,
        OperandType.InlineTok => OperandKind.Token,
        OperandType.InlineType => OperandKind.Token,
        OperandType.InlineString => OperandKind.StringToken,
        OperandType.InlineSig => OperandKind.SigToken,
        OperandType.InlineSwitch => OperandKind.Switch,
        _ => OperandKind.None,
    };

    /// <summary>
    /// Signature decoder that accumulates the root namespace of every
    /// type-def / type-ref it encounters whose namespace is forbidden. Returns
    /// empty strings for the actual decoded type (we only care about the
    /// side-effect set).
    /// </summary>
    private sealed class NamespaceCollectingSignatureProvider : ISignatureTypeProvider<string, object?>
    {
        public readonly HashSet<string> Hits = new(StringComparer.Ordinal);
        private readonly string[] _forbiddenPrefixes;

        public NamespaceCollectingSignatureProvider(string[] forbiddenPrefixes)
            => _forbiddenPrefixes = forbiddenPrefixes;

        private string Check(MetadataReader reader, EntityHandle handle)
        {
            var ns = GetRootNamespaceForEntity(reader, handle);
            if (ns is not null && StartsWithAny(ns, _forbiddenPrefixes))
                Hits.Add(ns);
            return string.Empty;
        }

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
            => Check(reader, handle);

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
            => Check(reader, handle);

        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        {
            var spec = reader.GetTypeSpecification(handle);
            return spec.DecodeSignature(this, genericContext);
        }

        public string GetSZArrayType(string elementType) => string.Empty;
        public string GetArrayType(string elementType, ArrayShape shape) => string.Empty;
        public string GetByReferenceType(string elementType) => string.Empty;
        public string GetPointerType(string elementType) => string.Empty;
        public string GetPinnedType(string elementType) => string.Empty;
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => string.Empty;
        public string GetGenericMethodParameter(object? genericContext, int index) => string.Empty;
        public string GetGenericTypeParameter(object? genericContext, int index) => string.Empty;
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => string.Empty;
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => string.Empty;
        public string GetFunctionPointerType(MethodSignature<string> signature) => string.Empty;
    }
}
