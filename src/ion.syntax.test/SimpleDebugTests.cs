namespace ion.syntax.test;

using Pidgin;

public class SimpleDebugTests
{
    [Test]
    public void SimpleUse_SingleInvalid()
    {
        const string input = "#use invalid without quotes\n";

        // Should throw ParseException
        var ex = Assert.Throws<syntax.ParseException>(() => IonParser.Parse("test", input));
        Console.WriteLine($"Parse error: {ex?.Message}");
    }

    [Test]
    public void SimpleUse_ValidThenInvalid()
    {
        var input = """
            #use "valid.ion"
            #use invalid
            """;

        // Should throw ParseException
        var ex = Assert.Throws<syntax.ParseException>(() => IonParser.Parse("test", input));
        Console.WriteLine($"Parse error: {ex?.Message}");
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

        // Should throw ParseException
        var ex = Assert.Throws<syntax.ParseException>(() => IonParser.Parse("test", input));
        Console.WriteLine($"Parse error: {ex?.Message}");
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

        // Should throw ParseException
        var ex = Assert.Throws<syntax.ParseException>(() => IonParser.Parse("test", input));
        Console.WriteLine($"Parse error: {ex?.Message}");
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
