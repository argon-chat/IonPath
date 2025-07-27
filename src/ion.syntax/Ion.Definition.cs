namespace ion.syntax;

using Pidgin;
using static Pidgin.Parser;
using static Pidgin.Parser<char>;

public partial class IonParser
{
    public static Parser<char, IonDefinition> Definition =>
        OneOf(
            UseDirective.OfType<IonDefinition>(),
            Message.OfType<IonDefinition>(),
            Flags.OfType<IonDefinition>(),
            Enums.OfType<IonDefinition>(),
            Typedef.OfType<IonDefinition>(),
            AttributeDef.OfType<IonDefinition>()
        ).Before(SkipWhitespaces);

    public static Parser<char, IEnumerable<IonDefinition>> IonFile =>
        Definition.Many().Before(End);
}