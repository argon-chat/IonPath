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

    /// <summary>
    /// Keywords that start a definition. Used for error recovery to skip
    /// past invalid input to the next recognizable definition.
    /// </summary>
    private static readonly string[] DefinitionKeywords =
        ["msg", "service", "use", "feature", "flags", "enum", "typedef", "union", "attr"];

    /// <summary>
    /// Attempts to parse a Definition, and on failure skips to the next definition keyword
    /// producing an <see cref="InvalidIonBlock"/>.
    /// </summary>
    public static Parser<char, IonSyntaxMember> DefinitionOrRecover =>
        Try(Definition).Or(RecoverToNextDefinition);

    /// <summary>
    /// Consumes characters until a definition keyword is found at the start of a line (or at current position),
    /// and returns the consumed text as an <see cref="InvalidIonBlock"/>.
    /// </summary>
    private static Parser<char, IonSyntaxMember> RecoverToNextDefinition =>
        Any.AtLeastOnceUntil(
            Try(Lookahead(OneOf(DefinitionKeywords.Select(kw => Try(String(kw))))))
                .ThenReturn(Unit.Value)
            .Or(End)
        ).Select(chars => (IonSyntaxMember)new InvalidIonBlock(new string(chars.ToArray())));

    public static Parser<char, IEnumerable<IonSyntaxMember>> IonFile =>
        SkipWhitespaces
            .Then(Definition.Many(), (_, defs) => defs)
            .Before(End);

    /// <summary>
    /// Recovery variant of <see cref="IonFile"/>. Skips over invalid blocks
    /// between definitions, collecting them as <see cref="InvalidIonBlock"/>.
    /// </summary>
    public static Parser<char, IEnumerable<IonSyntaxMember>> IonFileRecovery =>
        SkipWhitespaces
            .Then(DefinitionOrRecover.Many(), (_, defs) => defs)
            .Before(End);


    public static IonFileSyntax Parse(string name, string content)
    {
        var result = IonFile.Parse(content);

        if (!result.Success)
        {
            // Try recovery: skip invalid blocks and continue parsing
            var recovery = IonFileRecovery.Parse(content);
            if (recovery.Success)
                return BuildFileSyntax(name, new FileInfo($"{name}.ion"), recovery.Value);
            throw new ParseException(result.Error);
        }

        return BuildFileSyntax(name, new FileInfo($"{name}.ion"), result.Value);
    }

    public static IonFileSyntax Parse(FileInfo file)
    {
        var content = File.ReadAllText(file.FullName);
        var result = IonFile.Parse(content);

        if (!result.Success)
        {
            var recovery = IonFileRecovery.Parse(content);
            if (recovery.Success)
                return BuildFileSyntax(file.Name, file, recovery.Value);
            throw new ParseException(result.Error);
        }

        return BuildFileSyntax(file.Name, file, result.Value);
    }

    private static IonFileSyntax BuildFileSyntax(string name, FileInfo fileInfo, IEnumerable<IonSyntaxMember> members)
    {
        var membersList = members.ToList();
        return new IonFileSyntax(name, fileInfo,
            membersList.OfType<IonUseSyntax>().ToList(),
            membersList.OfType<IonFeatureSyntax>().ToList(),
            membersList.OfType<IonAttributeDefSyntax>().ToList(),
            membersList.OfType<IonEnumSyntax>().ToList(),
            membersList.OfType<IonFlagsSyntax>().ToList(),
            membersList.OfType<IonMessageSyntax>().ToList(),
            membersList.OfType<IonTypedefSyntax>().ToList(),
            membersList.OfType<IonServiceSyntax>().ToList(),
            membersList.OfType<IonUnionSyntax>().ToList(),
            membersList
        );
    }
}

public class ParseException(ParseError<char>? error) : Exception
{
    public ParseError<char>? Error { get; } = error;
}