namespace ion.compiler.CodeGen;

using ion.runtime;
using System.Text;
using syntax;

public class IonTypeScriptGenerator
{
    public static string GenerateModule(IonModule module)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"// Auto-generated from module {module.Name}");
        sb.AppendLine();

        foreach (var type in module.Definitions)
        {
            sb.AppendLine(GenerateType(type));
            sb.AppendLine();
        }

        foreach (var service in module.Services)
        {
            sb.AppendLine(GenerateService(service));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string GenerateType(IonType type)
    {
        if (type is IonEnum e) return GenerateEnum(e);
        if (type is IonFlags f) return GenerateFlags(f);
        if (type.isTypedef) return GenerateTypedef(type);
        return GenerateMsg(type);
    }

    private static string GenerateEnum(IonEnum e)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"enum {e.name.Identifier} {{");
        foreach (var m in e.members)
            sb.AppendLine($"  {m.name.Identifier} = {m.constantValue},");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string GenerateFlags(IonFlags f)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"enum {f.name.Identifier} {{");
        foreach (var m in f.members)
            sb.AppendLine($"  {m.name.Identifier} = {m.constantValue},");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string GenerateTypedef(IonType type)
    {
        var underlying = ResolveTypeScriptType(type.fields.FirstOrDefault()?.type!);
        return $"type {type.name.Identifier} = {underlying};";
    }

    private static string GenerateMsg(IonType type)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"interface {type.name.Identifier} {{");

        foreach (var field in type.fields)
        {
            var fieldType = ResolveTypeScriptType(field.type);
            var fieldName = field.name.Identifier;
            sb.AppendLine($"  {fieldName}: {fieldType};");
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string GenerateService(IonService service)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"interface I{service.name.Identifier} {{");

        foreach (var method in service.methods)
        {
            var returnType = method.modifiers.Any(x => x is IonMethodModifiers.Stream)
                ? $"AsyncIterable<{ResolveTypeScriptType(method.returnType)}>"
                : $"Promise<{ResolveTypeScriptType(method.returnType)}>";

            var args = string.Join(", ", method.arguments.Select(arg =>
                $"{arg.name.Identifier}: {ResolveTypeScriptType(arg.type)}"));

            sb.AppendLine($"  {method.name.Identifier}({args}): {returnType};");
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string ResolveTypeScriptType(IonType type)
    {
        string baseType;

        if (type.IsBuiltin)
        {
            baseType = type.name.Identifier switch
            {
                "bool" => "boolean",
                "int8" or "int16" or "int32" or "int64"
                    or "uint8" or "uint16" or "uint32" or "uint64"
                    or "float32" or "float64" => "number",
                "string" => "string",
                "guid" => "string", // could also be `"uuid"` or branded type
                _ => type.name.Identifier
            };
        }
        else
        {
            baseType = type.name.Identifier;
        }

        //if (type.IsArray)
        //    baseType += "[]";

        //if (type.IsOptional)
        //    baseType += " | null";

        return baseType;
    }
}


//public class TypeScriptGenerator : ILanguageGenerator
//{
//}

//public interface ILanguageGenerator
//{
//    void GenerateStandardTypedefs();
//}