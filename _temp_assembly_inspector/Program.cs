using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: AssemblyInspector <assembly.dll> [filter1,filter2,...]");
    return 2;
}

var path = Path.GetFullPath(args[0]);
var filters = args.Length > 1
    ? args[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    : Array.Empty<string>();

using var stream = File.OpenRead(path);
using var pe = new PEReader(stream);
if (!pe.HasMetadata)
{
    Console.Error.WriteLine($"No managed metadata: {path}");
    return 3;
}

var reader = pe.GetMetadataReader();
var provider = new TypeNameProvider();
Console.WriteLine($"ASSEMBLY|{Path.GetFileName(path)}");
if (reader.IsAssembly)
{
    var def = reader.GetAssemblyDefinition();
    Console.WriteLine($"IDENTITY|{reader.GetString(def.Name)}|{def.Version}");
}
foreach (var handle in reader.AssemblyReferences)
{
    var reference = reader.GetAssemblyReference(handle);
    Console.WriteLine($"REF|{reader.GetString(reference.Name)}|{reference.Version}");
}

foreach (var typeHandle in reader.TypeDefinitions)
{
    var type = reader.GetTypeDefinition(typeHandle);
    var typeName = MetadataNames.Definition(reader, typeHandle);
    var typeMatches = Matches(typeName, filters);
    var methods = new List<string>();

    foreach (var methodHandle in type.GetMethods())
    {
        var method = reader.GetMethodDefinition(methodHandle);
        var methodName = reader.GetString(method.Name);
        string sigText;
        try
        {
            var signature = method.DecodeSignature(provider, genericContext: null);
            sigText = $"{signature.ReturnType} {methodName}({string.Join(", ", signature.ParameterTypes)})";
        }
        catch (Exception ex)
        {
            sigText = $"<signature-error:{ex.GetType().Name}> {methodName}";
        }

        if (typeMatches || Matches(methodName, filters) || Matches(sigText, filters))
            methods.Add($"METHOD|{typeName}|{sigText}|RVA=0x{method.RelativeVirtualAddress:X8}|Impl={method.ImplAttributes}|Attrs={method.Attributes}");
    }

    if (typeMatches || methods.Count > 0)
    {
        Console.WriteLine($"TYPE|{typeName}|Attrs={type.Attributes}");
        foreach (var line in methods)
            Console.WriteLine(line);
    }
}

return 0;

static bool Matches(string value, string[] filters)
{
    if (filters.Length == 0) return true;
    return filters.Any(filter => value.Contains(filter, StringComparison.OrdinalIgnoreCase));
}

static class MetadataNames
{
    public static string Definition(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle);
        var name = reader.GetString(type.Name);
        var declaring = type.GetDeclaringType();
        if (!declaring.IsNil)
            return Definition(reader, declaring) + "+" + name;
        var ns = reader.GetString(type.Namespace);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    public static string Reference(MetadataReader reader, TypeReferenceHandle handle)
    {
        var type = reader.GetTypeReference(handle);
        var name = reader.GetString(type.Name);
        var ns = reader.GetString(type.Namespace);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }
}

sealed class TypeNameProvider : ISignatureTypeProvider<string, object?>
{
    public string GetArrayType(string elementType, ArrayShape shape) => $"{elementType}[{new string(',', Math.Max(0, shape.Rank - 1))}]";
    public string GetByReferenceType(string elementType) => elementType + "&";
    public string GetFunctionPointerType(MethodSignature<string> signature) => $"fnptr {signature.ReturnType}({string.Join(", ", signature.ParameterTypes)})";
    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => $"{genericType}<{string.Join(", ", typeArguments)}>";
    public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
    public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
    public string GetPinnedType(string elementType) => elementType + " pinned";
    public string GetPointerType(string elementType) => elementType + "*";
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
    {
        PrimitiveTypeCode.Boolean => "bool",
        PrimitiveTypeCode.Byte => "byte",
        PrimitiveTypeCode.Char => "char",
        PrimitiveTypeCode.Double => "double",
        PrimitiveTypeCode.Int16 => "short",
        PrimitiveTypeCode.Int32 => "int",
        PrimitiveTypeCode.Int64 => "long",
        PrimitiveTypeCode.IntPtr => "nint",
        PrimitiveTypeCode.Object => "object",
        PrimitiveTypeCode.SByte => "sbyte",
        PrimitiveTypeCode.Single => "float",
        PrimitiveTypeCode.String => "string",
        PrimitiveTypeCode.TypedReference => "TypedReference",
        PrimitiveTypeCode.UInt16 => "ushort",
        PrimitiveTypeCode.UInt32 => "uint",
        PrimitiveTypeCode.UInt64 => "ulong",
        PrimitiveTypeCode.UIntPtr => "nuint",
        PrimitiveTypeCode.Void => "void",
        _ => typeCode.ToString()
    };
    public string GetSZArrayType(string elementType) => elementType + "[]";
    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => MetadataNames.Definition(reader, handle);
    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => MetadataNames.Reference(reader, handle);
    public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
}
