namespace ion.syntax;

using Pidgin;

public record IonSyntaxBase
{
    public SourcePos Position { get; set; }
}

public record IonSyntaxMember : IonSyntaxBase
{
    public string? Comments { get; set; }
    public List<IonAttributeSyntax> Attributes { get; set; } = [];
}

public record IonUnderlyingTypeSyntax(string Name, bool IsOptional, bool IsArray) : IonSyntaxBase;

public record IonFieldSyntax(string Name, IonUnderlyingTypeSyntax Type) : IonSyntaxMember;

public record IonMessageSyntax(string Name, List<IonFieldSyntax> Fields) : IonSyntaxMember;

public record IonFlagEntrySyntax(string Name, string ValueExpression) : IonSyntaxBase;

public record IonFlagsSyntax(string Name, IonUnderlyingTypeSyntax Type, List<IonFlagEntrySyntax> Entries)
    : IonSyntaxMember;

public record IonEnumSyntax(string Name, IonUnderlyingTypeSyntax Type, List<IonFlagEntrySyntax> Entries)
    : IonSyntaxMember;

public record IonAttributeSyntax(string Name, List<string> Args) : IonSyntaxBase;

public record IonUseSyntax(string Path) : IonSyntaxMember;

public record IonTypedefSyntax(IonUnderlyingTypeSyntax TypeName, IonUnderlyingTypeSyntax? BaseType) : IonSyntaxMember;

public record IonArgumentSyntax(string argName, IonUnderlyingTypeSyntax type) : IonSyntaxMember;

public record IonAttributeDefSyntax(string Name, List<IonArgumentSyntax> Args) : IonSyntaxMember;

public record IonFeatureSyntax(string featureName) : IonSyntaxMember;

public record IonFileSyntax(
    string Name,
    FileInfo file,
    List<IonUseSyntax> useSyntaxes,
    List<IonFeatureSyntax> featureSyntaxes,
    List<IonAttributeDefSyntax> attributeDefSyntaxes,
    List<IonEnumSyntax> enumSyntaxes,
    List<IonFlagsSyntax> flagsSyntaxes,
    List<IonMessageSyntax> messageSyntaxes,
    List<IonTypedefSyntax> typedefSyntaxes)
{
    public List<IonSyntaxMember> Definitions => attributeDefSyntaxes
        .OfType<IonSyntaxMember>()
        .Concat(flagsSyntaxes)
        .Concat(enumSyntaxes)
        .Concat(messageSyntaxes).Concat(typedefSyntaxes).ToList();
}

public static class IonSyntaxEx
{
    public static T WithComments<T>(this T t, string? comments) where T : IonSyntaxMember
    {
        t.Comments = comments;
        return t;
    }

    public static T WithAttributes<T>(this T t, IEnumerable<IonAttributeSyntax> attributes) where T : IonSyntaxMember
    {
        t.Attributes.AddRange(attributes);
        return t;
    }

    public static T WithPos<T>(this T t, SourcePos pos) where T : IonSyntaxBase
    {
        t.Position = pos;
        return t;
    }
}