namespace ion.syntax.test;

using Pidgin;
using static Assert;

public class ErrorRecoveryTests
{
    #region Message Error Recovery

    [Test]
    public void Message_WithInvalidField_ContinuesParsing()
    {
        const string input = """
                             msg FooBar {
                                validField: i2;
                                invalidField without colon;
                                anotherValidField: u8;
                             }
                             """;

        var result = IonParser.Message.Parse(input);

        That(result.Success);
        var message = (IonMessageSyntax)result.Value;
        That(message.Fields.Count >= 2, "Should recover and parse valid fields");
    }

    [Test]
    public void Message_WithMultipleErrors_ParsesValidParts()
    {
        const string input = """
                             msg FooBar {
                                field1: i2;
                                broken field syntax
                                field2: u8;
                                another broken;
                                field3: bool;
                             }
                             """;

        var result = IonParser.Message.Parse(input);

        That(result.Success);
        var message = (IonMessageSyntax)result.Value;
        That(message.Fields.Count >= 3, "Should recover and parse all valid fields");
    }

    [Test]
    public void File_WithInvalidDefinition_ContinuesParsing()
    {
        const string input = """
                             msg ValidMessage {
                                field: i2;
                             }
                             
                             invalid syntax here without msg keyword
                             
                             msg AnotherValidMessage {
                                field: u8;
                             }
                             """;

        var syntax = IonParser.Parse("test", input);
        That(syntax.messageSyntaxes.Count >= 2, "Should parse both valid messages despite error");
    }

    #endregion

    #region Service Error Recovery

    [Test]
    public void Service_WithInvalidMethod_ContinuesParsing()
    {
        const string input = """
                             service TestService() {
                                validMethod(arg: i2): bool;
                                invalidMethod without parens;
                                anotherValidMethod(): u8;
                             }
                             """;

        var result = IonParser.Service.Parse(input);

        That(result.Success);
        That(result.Value.Methods.Count >= 2, "Should recover and parse valid methods");
    }

    [Test]
    public void Service_WithInvalidArgument_FailsWithError()
    {
        const string input = """
                             service TestService(validArg: i2, brokenArg, anotherValid: u8) {
                                method(): bool;
                             }
                             """;

        var result = IonParser.Service.Parse(input);

        // Invalid argument syntax should cause parser to FAIL, not silently eat garbage
        That(result.Success, Is.False, "Invalid argument should cause parse failure");
        That(result.Error, Is.Not.Null, "Should have error details");
    }

    #endregion

    #region Flags/Enum Error Recovery

    [Test]
    public void Flags_WithInvalidEntry_ContinuesParsing()
    {
        const string input = """
                             flags MyFlags {
                                ValidEntry1,
                                Invalid Entry With Spaces,
                                ValidEntry2 = 1 << 1,
                                ValidEntry3
                             }
                             """;

        var result = IonParser.Flags.Parse(input);

        That(result.Success);
        var flags = (IonFlagsSyntax)result.Value;
        That(flags.Entries.Count >= 3, "Should recover and parse valid entries");
    }

    #endregion

    #region Union Error Recovery

    [Test]
    public void Union_WithInvalidCase_ContinuesParsing()
    {
        const string input = """
                             union MyUnion {
                                ValidCase(field: i2),
                                InvalidCase without args or parens,
                                AnotherValidCase(x: u8)
                             }
                             """;

        var result = IonParser.Union.Parse(input);

        That(result.Success);
        That(result.Value.cases.Count >= 2, "Should recover and parse valid cases");
    }

    #endregion

    #region File-Level Error Recovery

    [Test]
    public void File_WithMixedValidAndInvalid_ParsesAll()
    {
        const string input = """
                             msg Message1 {
                                field: i2;
                             }
                             
                             broken syntax here
                             
                             flags MyFlags {
                                Entry1,
                                Entry2
                             }
                             
                             more garbage
                             
                             service MyService() {
                                method(): bool;
                             }
                             """;

        var syntax = IonParser.Parse("test", input);

        That(syntax.messageSyntaxes.Count >= 1, "Should parse message");
        That(syntax.flagsSyntaxes.Count >= 1, "Should parse flags");
        That(syntax.serviceSyntaxes.Count >= 1, "Should parse service");
    }

    [Test]
    public void File_WithSyntaxErrors_ReportsErrors()
    {
        const string input = """
                             msg ValidMessage {
                                validField: i2;
                             }
                             
                             totally invalid syntax
                             """;

        var syntax = IonParser.Parse("test", input);

        That(syntax.allTokens != null && syntax.allTokens.OfType<InvalidIonBlock>().Any(), 
            "Should capture invalid blocks");
    }

    [Test]
    public void File_CompletelyInvalid_DoesNotCrash()
    {
        const string input = """
                             this is completely invalid
                             no valid ion syntax at all
                             just random text
                             """;

        var syntax = IonParser.Parse("test", input);

        That(syntax, Is.Not.Null, "Should return IonFileSyntax even for invalid input");
    }

    #endregion

    #region Attribute Error Recovery

    [Test]
    public void Attributes_WithInvalidSyntax_FailsWithError()
    {
        const string input = """
                             @Valid
                             @Invalid without closing paren(
                             @AnotherValid()
                             msg TestMessage {
                                field: i2;
                             }
                             """;

        var result = IonParser.Message.Parse(input);

        // Invalid attribute syntax should cause parser to FAIL, not silently eat garbage
        That(result.Success, Is.False, "Invalid attribute should cause parse failure");
    }

    #endregion

    #region Directive Error Recovery

    [Test]
    public void UseDirective_WithMissingQuotes_RecoverGracefully()
    {
        const string input = """
                             #use "valid/path.ion"
                             #use invalid without quotes
                             #use "another/valid.ion"
                             
                             msg Test { field: i2; }
                             """;

        var syntax = IonParser.Parse("test", input);

        That(syntax.useSyntaxes.Count >= 2, "Should parse valid use directives");
        That(syntax.messageSyntaxes.Count >= 1, "Should continue to parse definitions");
    }

    #endregion
}
