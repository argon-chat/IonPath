namespace ion.syntax;

using Pidgin;
using static Pidgin.Parser;
using static Pidgin.Parser<char>;

public partial class IonParser
{
    /// <summary>
    /// The bare declaration forms, without their leading doc comment / attribute section.
    /// The leading section is hoisted into <see cref="Definition"/> so that it is parsed exactly
    /// once, before the <c>OneOf</c> dispatch. (Parsing it inside the first alternative
    /// made Pidgin commit to that alternative as soon as a comment was consumed, which turned
    /// every documented top level declaration into a parse error.)
    /// </summary>
    private static Parser<char, IonSyntaxMember> DefinitionCore =>
        OneOf(
            AttributeDefCore.OfType<IonSyntaxMember>(),
            ServiceCore.OfType<IonSyntaxMember>(),
            ImportDirectiveCore,
            UseDirectiveCore,
            FeatureDirectiveCore,
            MessageCore,
            // After MessageCore only for readability: `msg` and `mixin` share a first letter but
            // MsgKeyword is atomic, so neither can consume the other's input.
            MixinCore.OfType<IonSyntaxMember>(),
            FlagsCore,
            EnumsCore,
            TypedefCore.OfType<IonSyntaxMember>(),
            UnionCore.OfType<IonSyntaxMember>()
        );

    public static Parser<char, IonSyntaxMember> Definition =>
        WithLeading(DefinitionCore).Before(SkipTopLevelTrivia);

    /// <summary>
    /// One or more consecutive <c>//!</c> lines, materialised as a synthetic member so that
    /// <see cref="BuildFileSyntax"/> can lift them into <see cref="IonFileSyntax.ModuleDoc"/>.
    /// </summary>
    private static Parser<char, IonSyntaxMember> ModuleDocDeclaration =>
        Map(
            IonSyntaxMember (pos, docs) => new IonModuleDocSyntax(string.Join("\n", docs)).WithPos(pos),
            CurrentPos,
            ModuleDocLine.AtLeastOnce());

    /// <summary>A module doc block or a definition.</summary>
    private static Parser<char, IonSyntaxMember> TopLevelItem =>
        OneOf(
            Try(SkipTopLevelTrivia.Then(ModuleDocDeclaration)),
            Definition);

    /// <summary>
    /// Keywords that start a definition. Used for error recovery to skip
    /// past invalid input to the next recognizable definition.
    /// </summary>
    private static readonly string[] DefinitionKeywords =
    [
        "msg", "mixin", "service", "#import", "#use", "#feature", "flags", "enum", "typedef", "union",
        "attribute", "attr"
    ];

    /// <summary>
    /// Attempts to parse a Definition, and on failure skips to the next definition keyword
    /// producing an <see cref="InvalidIonBlock"/>.
    /// </summary>
    public static Parser<char, IonSyntaxMember> DefinitionOrRecover =>
        Try(TopLevelItem).Or(RecoverToNextDefinition);

    /// <summary>A definition keyword at the beginning of a line (leading indentation allowed).</summary>
    private static Parser<char, Unit> DefinitionKeywordAtLineStart =>
        CurrentPos.Assert(p => p.Col == 1)
            .Then(OneOf(Char(' '), Char('\t')).SkipMany())
            .Then(OneOf(DefinitionKeywords.Select(kw =>
                Try(String(kw).Then(OneOf(Whitespace.ThenReturn(Unit.Value), End))))))
            .ThenReturn(Unit.Value);

    /// <summary>
    /// A position at which error recovery re-synchronises: a definition keyword at the start
    /// of a line, or end of input.
    /// </summary>
    private static Parser<char, Unit> ResyncPoint =>
        Try(Lookahead(DefinitionKeywordAtLineStart)).ThenReturn(Unit.Value).Or(End);

    /// <summary>
    /// One unit of skipped-over source. Comments and string literals are consumed whole so that
    /// a definition keyword that only appears inside them can never be mistaken for a resync point.
    /// </summary>
    private static Parser<char, string> RecoverUnit =>
        OneOf(
            RawComment,
            RawStringLiteral,
            Any.Select(c => c.ToString()));

    /// <summary>
    /// Consumes characters until a definition keyword is found at the start of a line,
    /// and returns the consumed text as an <see cref="InvalidIonBlock"/>.
    /// Fails without consuming when only trivia is left, so that trailing comments are not
    /// reported as invalid blocks.
    /// </summary>
    private static Parser<char, IonSyntaxMember> RecoverToNextDefinition =>
        Try(Not(Try(SkipTriviaAll.Then(End))))
            .Then(RecoverUnit.AtLeastOnceUntil(ResyncPoint))
            .Select(chunks => (IonSyntaxMember)new InvalidIonBlock(string.Concat(chunks)));

    public static Parser<char, IEnumerable<IonSyntaxMember>> IonFile =>
        TopLevelItem.Many()
            .Before(SkipTriviaAll)
            .Before(End);

    /// <summary>
    /// Recovery variant of <see cref="IonFile"/>. Skips over invalid blocks
    /// between definitions, collecting them as <see cref="InvalidIonBlock"/>.
    /// </summary>
    public static Parser<char, IEnumerable<IonSyntaxMember>> IonFileRecovery =>
        DefinitionOrRecover.Many()
            .Before(SkipTriviaAll)
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
        var all = members.ToList();

        var moduleDocs = all.OfType<IonModuleDocSyntax>().Select(x => x.Text).ToList();
        var moduleDoc = moduleDocs.Count == 0 ? null : string.Join("\n", moduleDocs);

        var membersList = all.Where(x => x is not IonModuleDocSyntax).ToList();

        return new IonFileSyntax(name, fileInfo,
            membersList.OfType<IonUseSyntax>().ToList(),
            membersList.OfType<IonImportSyntax>().ToList(),
            membersList.OfType<IonFeatureSyntax>().ToList(),
            membersList.OfType<IonAttributeDefSyntax>().ToList(),
            membersList.OfType<IonEnumSyntax>().ToList(),
            membersList.OfType<IonFlagsSyntax>().ToList(),
            membersList.OfType<IonMessageSyntax>().ToList(),
            membersList.OfType<IonTypedefSyntax>().ToList(),
            membersList.OfType<IonServiceSyntax>().ToList(),
            membersList.OfType<IonUnionSyntax>().ToList(),
            membersList,
            moduleDoc
        )
        {
            mixinSyntaxes = membersList.OfType<IonMixinSyntax>().ToList()
        };
    }
}

public class ParseException(ParseError<char>? error) : Exception
{
    public ParseError<char>? Error { get; } = error;
}
