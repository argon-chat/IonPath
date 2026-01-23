namespace ion.syntax.test;

using Pidgin;

public class DebugParserTests
{
    [Test]
    public void Debug_Message_WithMultipleErrors()
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
        
        Console.WriteLine($"Success: {result.Success}");
        
        if (result.Success)
        {
            var message = (IonMessageSyntax)result.Value;
            Console.WriteLine($"Message name: {message.Name}");
            Console.WriteLine($"Field count: {message.Fields.Count}");
            
            foreach (var field in message.Fields.OfType<IonFieldSyntax>())
            {
                Console.WriteLine($"  - {field.Name}: {field.Type.Name}");
            }
            
            Console.WriteLine($"Has comments: {message.Comments != null}");
        }
        else
        {
            Console.WriteLine($"Error: {result.Error}");
        }
    }

    [Test]
    public void Debug_Service_WithInvalidMethod()
    {
        const string input = """
                             service TestService() {
                                validMethod(arg: i2): bool;
                                invalidMethod without parens;
                                anotherValidMethod(): u8;
                             }
                             """;

        var result = IonParser.Service.Parse(input);
        
        Console.WriteLine($"Success: {result.Success}");
        
        if (result.Success)
        {
            var service = result.Value;
            Console.WriteLine($"Service name: {service.serviceName}");
            Console.WriteLine($"Method count: {service.Methods.Count}");
            
            foreach (var method in service.Methods.OfType<IonMethodSyntax>())
            {
                Console.WriteLine($"  - {method.methodName}: ({method.arguments.Count} args) -> {method.returnType?.Name ?? "void"}");
            }
        }
        else
        {
            Console.WriteLine($"Error: {result.Error}");
        }
    }

    [Test]
    public void Debug_Service_WithInvalidArgument()
    {
        const string input = """
                             service TestService(validArg: i2, brokenArg, anotherValid: u8) {
                                method(): bool;
                             }
                             """;

        var result = IonParser.Service.Parse(input);
        
        Console.WriteLine($"Success: {result.Success}");
        
        if (result.Success)
        {
            var service = result.Value;
            Console.WriteLine($"Service name: {service.serviceName}");
            Console.WriteLine($"Base args count: {service.BaseArguments.Count}");
            
            foreach (var arg in service.BaseArguments)
            {
                Console.WriteLine($"  - {arg.argName}: {arg.type.Name}");
            }
        }
        else
        {
            Console.WriteLine($"Error: {result.Error}");
        }
    }

    [Test]
    public void Debug_Flags_WithInvalidEntry()
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
        
        Console.WriteLine($"Success: {result.Success}");
        
        if (result.Success)
        {
            var flags = (IonFlagsSyntax)result.Value;
            Console.WriteLine($"Flags name: {flags.Name}");
            Console.WriteLine($"Entry count: {flags.Entries.Count}");
            
            foreach (var entry in flags.Entries)
            {
                Console.WriteLine($"  - {entry.Name}");
            }
        }
        else
        {
            Console.WriteLine($"Error: {result.Error}");
        }
    }

    [Test]
    public void Debug_UseDirective()
    {
        const string input = """
                             #use "valid/path.ion"
                             #use invalid without quotes
                             #use "another/valid.ion"
                             
                             msg Test { field: i2; }
                             """;

        var syntax = IonParser.Parse("test", input);
        
        Console.WriteLine($"Use directives: {syntax.useSyntaxes.Count}");
        Console.WriteLine($"Messages: {syntax.messageSyntaxes.Count}");
        
        if (syntax.allTokens != null)
        {
            Console.WriteLine($"Total tokens: {syntax.allTokens.Count}");
            foreach (var token in syntax.allTokens)
            {
                Console.WriteLine($"  - {token.GetType().Name}");
            }
        }
    }
}
