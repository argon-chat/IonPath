namespace ion.syntax.test;

using Pidgin;

public class SimpleDebugTests
{
    [Test]
    public void SimpleUse_SingleInvalid()
    {
        const string input = "#use invalid without quotes\n";

        // Recovery: invalid input is captured as InvalidIonBlock
        var syntax = IonParser.Parse("test", input);
        Assert.That(syntax.allTokens!.OfType<InvalidIonBlock>().Any(), "Should recover with InvalidIonBlock");
    }

    [Test]
    public void SimpleUse_ValidThenInvalid()
    {
        var input = """
            #use "valid.ion"
            #use invalid
            """;

        // Recovery: valid use is parsed, invalid one becomes InvalidIonBlock
        var syntax = IonParser.Parse("test", input);
        Assert.That(syntax.useSyntaxes.Count, Is.GreaterThanOrEqualTo(1));
        Assert.That(syntax.allTokens!.OfType<InvalidIonBlock>().Any(), "Should recover with InvalidIonBlock");
    }

    [Test]
    public void SimpleFile_InvalidBetweenMessages()
    {
        var input = """
            msg ValidMessage {
               field: i2;
            }
            
            invalid syntax here
            
            msg AnotherValidMessage {
               field: u8;
            }
            """;

        // Recovery: both messages parsed, invalid text captured as InvalidIonBlock
        var syntax = IonParser.Parse("test", input);
        Assert.That(syntax.messageSyntaxes.Count, Is.GreaterThanOrEqualTo(2));
        Assert.That(syntax.allTokens!.OfType<InvalidIonBlock>().Any(), "Should recover with InvalidIonBlock");
    }

    [Test]
    public void SimpleAttributes_Invalid()
    {
        var input = """
            @Valid
            @Invalid without closing paren(
            @AnotherValid()
            msg TestMessage {
               field: i2;
            }
            """;

        var result = IonParser.Message.Parse(input);
        
        Console.WriteLine($"Success: {result.Success}");
        if (result.Success)
        {
            var message = (IonMessageSyntax)result.Value;
            Console.WriteLine($"Message: {message.Name}");
            Console.WriteLine($"Attributes: {message.Attributes.Count}");
            foreach (var attr in message.Attributes)
            {
                Console.WriteLine($"  - @{attr.Name}");
            }
        }
        else
        {
            Console.WriteLine($"Error: {result.Error}");
        }
    }

    [Test]
    public void SimpleService_MissingSemicolon()
    {
        var input = """
            service TestFoo {
                Dog(data: bytes): bytes
                DogWww(): bytes;
            }
            """;

        var result = IonParser.Service.Parse(input);
        
        Console.WriteLine($"Success: {result.Success}");
        if (result.Success)
        {
            var service = result.Value;
            Console.WriteLine($"Service: {service.serviceName}");
            Console.WriteLine($"Methods: {service.Methods.Count}");
            
            foreach (var method in service.Methods)
            {
                var detail = method switch
                {
                    IonMethodSyntax m => $"Method: {m.methodName}",
                    _ => method.GetType().Name
                };
                Console.WriteLine($"  - {detail}");
            }
        }
        else
        {
            Console.WriteLine($"Error: {result.Error}");
        }
    }

    [Test]
    public void SimpleService_DoubleColon()
    {
        var input = """
            service TestBlobs() {
                Do(data: bytes);
                DoIt(data: bytes: bytes;
                DoIt2(data: bytes): bytes;
                DoIt3(data: bytes): bytes;
            }
            """;

        // Recovery: valid methods parsed, invalid one captured as InvalidIonBlock
        var syntax = IonParser.Parse("test", input);
        Assert.That(syntax.serviceSyntaxes.Count, Is.GreaterThanOrEqualTo(0));
        Assert.That(syntax.allTokens!.OfType<InvalidIonBlock>().Any(), "Should recover with InvalidIonBlock");
    }

    [Test]
    public void SimpleService_DoubleColon_DirectParse()
    {
        var input = """
            service TestBlobs() {
                DoIt(data: bytes: bytes;
            }
            """;
        var result = IonParser.Service.Parse(input);
        
        Console.WriteLine($"Success: {result.Success}");
        if (!result.Success)
        {
            Console.WriteLine($"Error: {result.Error}");
        }
    }
}
