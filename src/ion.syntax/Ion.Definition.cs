namespace ion.syntax;

using Pidgin;
using static Pidgin.Parser;
using static Pidgin.Parser<char>;

public partial class IonParser
{
    public static Parser<char, IonSyntaxMember> Definition =>
        OneOf(
            AttributeDef.OfType<IonSyntaxMember>(),
            Service.OfType<IonSyntaxMember>(),
            UseDirective.OfType<IonSyntaxMember>(),
            FeatureDirective.OfType<IonSyntaxMember>(),
            Message.OfType<IonSyntaxMember>(),
            Flags.OfType<IonSyntaxMember>(),
            Enums.OfType<IonSyntaxMember>(),
            Typedef.OfType<IonSyntaxMember>(),
            Union.OfType<IonSyntaxMember>()
        ).Before(SkipWhitespaces);

    public static Parser<char, IEnumerable<IonSyntaxMember>> IonFile =>
        SkipWhitespaces
            .Then(Definition.Many(), (_, defs) => defs)
            .Before(End);


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
            result.Value.OfType<IonServiceSyntax>().ToList(),
            result.Value.OfType<IonUnionSyntax>().ToList(),
            result.Value.ToList()
        );
    }

    public static IonFileSyntax Parse(FileInfo file)
    {
        var result = IonFile.Parse(File.ReadAllText(file.FullName));

        if (!result.Success)
            throw new ParseException(result.Error);

        return new IonFileSyntax(file.Name, file,
            result.Value.OfType<IonUseSyntax>().ToList(),
            result.Value.OfType<IonFeatureSyntax>().ToList(),
            result.Value.OfType<IonAttributeDefSyntax>().ToList(),
            result.Value.OfType<IonEnumSyntax>().ToList(),
            result.Value.OfType<IonFlagsSyntax>().ToList(),
            result.Value.OfType<IonMessageSyntax>().ToList(),
            result.Value.OfType<IonTypedefSyntax>().ToList(),
            result.Value.OfType<IonServiceSyntax>().ToList(),
            result.Value.OfType<IonUnionSyntax>().ToList(),
            result.Value.ToList()
        );
    }
}

public class ParseException(ParseError<char>? error) : Exception
{
    public ParseError<char>? Error { get; } = error;
}