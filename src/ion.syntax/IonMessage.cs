namespace ion.syntax;

using Pidgin;


public record IonSyntaxEntity
{
    public SourcePos Position { get; set; }
}
public record IonDefinition : IonSyntaxEntity
{
    public string? Comments { get; set; }
    public List<IonAttribute> Attributes { get; set; } = [];
} 

public record IonUnderlyingType(string Name, bool IsOptional, bool IsArray) : IonSyntaxEntity;
public record IonField(string Name, IonUnderlyingType Type) : IonDefinition;
public record IonMessage(string Name, List<IonField> Fields) : IonDefinition;
public record IonFlagEntry(string Name, string ValueExpression) : IonSyntaxEntity;
public record IonFlags(string Name, IonUnderlyingType Type, List<IonFlagEntry> Entries) : IonDefinition;
public record IonEnum(string Name, IonUnderlyingType Type, List<IonFlagEntry> Entries) : IonDefinition;
public record IonAttribute(string Name, List<string> Args) : IonSyntaxEntity;
public record IonUse(string Path) : IonDefinition;
public record IonTypedef(IonUnderlyingType TypeName, IonUnderlyingType? BaseType) : IonDefinition;
public record IonArgument(string argName, IonUnderlyingType type) : IonDefinition;
public record IonAttributeDef(string Name, List<IonArgument> Args) : IonDefinition;


public static class IonDefinitionEx
{
    public static T WithComments<T>(this T t, string? comments) where T : IonDefinition
    {
        t.Comments = comments; return t;
    }

    public static T WithAttributes<T>(this T t, IEnumerable<IonAttribute> attributes) where T : IonDefinition
    {
        t.Attributes.AddRange(attributes); return t;
    }

    public static T WithPos<T>(this T t, SourcePos pos) where T : IonSyntaxEntity
    {
        t.Position = pos; return t;
    }
}