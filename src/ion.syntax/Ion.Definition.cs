namespace ion.syntax;

using Pidgin;
using static Pidgin.Parser;
using static Pidgin.Parser<char>;

public partial class IonParser
{
    public static Parser<char, IonSyntaxMember> Definition =>
        OneOf(
            Try(Service.OfType<IonSyntaxMember>()),
            Try(UseDirective.OfType<IonSyntaxMember>()),
            Try(FeatureDirective.OfType<IonSyntaxMember>()),
            Try(Message.OfType<IonSyntaxMember>()),
            Try(Flags.OfType<IonSyntaxMember>()),
            Try(Enums.OfType<IonSyntaxMember>()),
            Try(Typedef.OfType<IonSyntaxMember>()),
            Try(AttributeDef.OfType<IonSyntaxMember>()),
            InvalidBlockFallback
        ).Before(SkipWhitespaces);

    private static Parser<char, IonSyntaxMember> InvalidBlockFallback =>
        from first in Any
        from rest in Any.Until(Lookahead(Char('}').Or(Char(';')))).Optional()
        let content = first + string.Concat(rest.GetValueOrDefault() ?? [])
        select (IonSyntaxMember)new InvalidIonBlock(content.Trim());

    public static Parser<char, IEnumerable<IonSyntaxMember>> IonFile =>
        Definition.Many().Before(End);


    public static IonFileSyntax Parse(string name, string content)
    {
        var result = IonFile.Parse(content);

        if (!result.Success)
            throw new ParseException(result.Error);

        return new IonFileSyntax(name, new FileInfo($"{name}.ion"),
            result.Value.OfType<IonUseSyntax>().ToList(),
            result.Value.OfType<IonFeatureSyntax>().ToList(),
            result.Value.OfType<IonAttributeDefSyntax>().ToList(),
            result.Value.OfType<IonEnumSyntax>().ToList(),
            result.Value.OfType<IonFlagsSyntax>().ToList(),
            result.Value.OfType<IonMessageSyntax>().ToList(),
            result.Value.OfType<IonTypedefSyntax>().ToList(),
            result.Value.OfType<IonServiceSyntax>().ToList()
        );
    }

    public static IonFileSyntax Parse(FileInfo file)
    {
        var result = IonFile.Parse(File.ReadAllText(file.FullName));

        if (!result.Success)
            throw new Exception();

        return new IonFileSyntax(Path.GetFileNameWithoutExtension(file.Name), file,
            result.Value.OfType<IonUseSyntax>().ToList(),
            result.Value.OfType<IonFeatureSyntax>().ToList(),
            result.Value.OfType<IonAttributeDefSyntax>().ToList(),
            result.Value.OfType<IonEnumSyntax>().ToList(),
            result.Value.OfType<IonFlagsSyntax>().ToList(),
            result.Value.OfType<IonMessageSyntax>().ToList(),
            result.Value.OfType<IonTypedefSyntax>().ToList(),
            result.Value.OfType<IonServiceSyntax>().ToList()
        );
    }
}

public class ParseException(ParseError<char>? error) : Exception
{
    public ParseError<char>? Error { get; } = error;
}