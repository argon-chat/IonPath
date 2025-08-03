namespace ion.syntax;

using Pidgin;

public record IonSyntaxBase
{
    public SourcePos StartPosition { get; set; }
    public SourcePos? EndPosition { get; set; }
    public FileInfo? SourceFile { get; set; }
}

public record IonSyntaxMember : IonSyntaxBase
{
    public string? Comments { get; set; }
    public List<IonAttributeSyntax> Attributes { get; set; } = [];
}

public record InvalidIonBlock(string block) : IonSyntaxMember
{

}

public record IonIdentifier(string Identifier) : IonSyntaxBase()
{
    public static implicit operator IonIdentifier(string val) => new(val) { StartPosition = new SourcePos(0, 0) };
}
public record IonUnderlyingTypeSyntax(IonIdentifier Name, bool IsOptional, bool IsArray) : IonSyntaxBase;

public record IonFieldSyntax(IonIdentifier Name, IonUnderlyingTypeSyntax Type) : IonSyntaxMember;

public record IonMessageSyntax(IonIdentifier Name, List<IonFieldSyntax> Fields) : IonSyntaxMember;

public record IonFlagEntrySyntax(IonIdentifier Name, string ValueExpression) : IonSyntaxBase;

public record IonFlagsSyntax(IonIdentifier Name, IonUnderlyingTypeSyntax Type, List<IonFlagEntrySyntax> Entries)
    : IonSyntaxMember;

public record IonEnumSyntax(IonIdentifier Name, IonUnderlyingTypeSyntax Type, List<IonFlagEntrySyntax> Entries)
    : IonSyntaxMember;

public record IonAttributeSyntax(IonIdentifier Name, List<string> Args) : IonSyntaxBase;

public record IonUseSyntax(string Path) : IonSyntaxMember;

public record IonTypedefSyntax(IonUnderlyingTypeSyntax TypeName, IonUnderlyingTypeSyntax? BaseType) : IonSyntaxMember;

public record IonArgumentSyntax(IonIdentifier argName, IonUnderlyingTypeSyntax type) : IonSyntaxMember;

public record IonAttributeDefSyntax(IonIdentifier Name, List<IonArgumentSyntax> Args) : IonSyntaxMember;

public record IonFeatureSyntax(string featureName) : IonSyntaxMember;

public record IonMethodSyntax(
    IonIdentifier methodName,
    List<IonMethodModifiers> modifiers,
    List<IonArgumentSyntax> arguments,
    IonUnderlyingTypeSyntax? returnType) : IonSyntaxMember;

public record IonServiceSyntax(IonIdentifier serviceName, List<IonArgumentSyntax> BaseArguments, List<IonMethodSyntax> Methods)
    : IonSyntaxMember;

public enum IonMethodModifiers
{
    Unary,
    Stream,
    Internal
}

public record IonFileSyntax(
    string Name,
    FileInfo file,
    List<IonUseSyntax> useSyntaxes,
    List<IonFeatureSyntax> featureSyntaxes,
    List<IonAttributeDefSyntax> attributeDefSyntaxes,
    List<IonEnumSyntax> enumSyntaxes,
    List<IonFlagsSyntax> flagsSyntaxes,
    List<IonMessageSyntax> messageSyntaxes,
    List<IonTypedefSyntax> typedefSyntaxes,
    List<IonServiceSyntax> serviceSyntaxes)
{
    public List<IonSyntaxMember> Definitions => attributeDefSyntaxes
        .OfType<IonSyntaxMember>()
        .Concat(flagsSyntaxes)
        .Concat(enumSyntaxes)
        .Concat(messageSyntaxes)
        .Concat(serviceSyntaxes)
        .Concat(typedefSyntaxes).ToList();
}

public static class IonFileProcessingScope
{
    private static readonly ThreadLocal<FileInfo?> currentFile = new(true);

    public static IDisposable Begin(FileInfo file)
    {
        if (currentFile.Value is not null) throw new InvalidOperationException($"Current thread already has locked file");
        currentFile.Value = file;
        return new Disposer();
    }


    private class Disposer : IDisposable
    {
        public void Dispose()
        {
            currentFile.Value = null;
        }
    }

    internal static FileInfo? Take() => currentFile.Value;
}


public static class IonSyntaxEx
{
    public static T WithComments<T>(this T t, string? comments) where T : IonSyntaxMember
    {
        t.Comments = comments;
        return t;
    }

    public static T WithComments<T>(this T t, Maybe<string> comments) where T : IonSyntaxMember
    {
        t.Comments = comments.GetValueOrDefault();
        return t;
    }

    public static T WithAttributes<T>(this T t, IEnumerable<IonAttributeSyntax> attributes) where T : IonSyntaxMember
    {
        t.Attributes.AddRange(attributes);
        return t;
    }

    public static T WithPos<T>(this T t, SourcePos pos) where T : IonSyntaxBase
    {
        t.StartPosition = pos;
        t.SourceFile = IonFileProcessingScope.Take();
        return t;
    }

    public static T WithPos<T>(this T t, SourcePos start, SourcePos end) where T : IonSyntaxBase
    {
        t.StartPosition = start;
        t.EndPosition = end;
        t.SourceFile = IonFileProcessingScope.Take();
        return t;
    }
}