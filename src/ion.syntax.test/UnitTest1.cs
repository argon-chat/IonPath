namespace ion.syntax.test;

using Pidgin;

public class Tests
{
    [Test]
    public void Test1()
    {
        const string input = """
                             msg FooBar {
                                testField: i2;
                                foobarField: u8;
                                delta: f4;

                                foobarOptional: bool?; 
                                theArray: i2[];
                             }
                             """;

        var result = IonParser.Message.Parse(input);

        Assert.That(result.Success);
    }

    [Test]
    public void Test2()
    {
        const string input = """
                             msg FooBar {
                                testField : i2 ;
                                foobarField : u8 ;
                                delta : f4 ;

                                foobarOptional : bool ?; 
                                theArray : i2 [] ;
                             }
                             """;

        var result = IonParser.Message.Parse(input);

        Assert.That(result.Success);
    }

    [Test]
    public void Test3()
    {
        const string input = """
                             flags Permissions : u32 {
                               READ = 1,
                               WRITE = 1 << 1,
                               ADMIN = 1 << 2
                             }
                             """;

        var result = IonParser.Flags.Parse(input);

        Assert.That(result.Success);
    }

    [Test]
    public void Test4()
    {
        const string input = """
                             @TestAttribute()
                             flags Permissions : u32 {
                               READ = 1,
                               WRITE = 1 << 1,
                               ADMIN = 1 << 2
                             }
                             """;

        var result = IonParser.Flags.Parse(input);

        Assert.That(result.Success);
    }

    [Test]
    public void Test5()
    {
        const string input = """
                             #use "foobar"
                             
                             
                             """;

        var result = IonParser.IonFile.Parse(input);

        Assert.That(result.Success);
    }

    [Test]
    public void Test6()
    {
        const string input = """
                             @Grain()
                             service IServerInteraction(@GrainId() id: guid)
                             {
                                GetServers(): Server[];
                                FooBar(i: i4): Server;
                                stream FooBar(i: i4): Server;
                             }
                             """;

        var result = IonParser.Service.Parse(input);

        Assert.That(result.Success);
    }

    [Test]
    public void Test7()
    {
        const string input = """
                             service IServerInteraction()
                             {
                                GetServers(): Server[];
                             }
                             """;

        var result = IonParser.Service.Parse(input);

        Assert.That(result.Success);
    }

    [Test]
    public void Test8()
    {
        const string input = """
                             service IServerInteraction(){
                                 unary CreateChannel(request: CreateChannelRequest);
                                 DeleteChannel(channelId: guid);
                                 GetChannels(): RealtimeChannel[];
                             }
                             """;

        var result = IonParser.Service.Parse(input);

        Assert.That(result.Success);
    }

    [Test]
    public void Test9()
    {
        const string input = """
                             service IServerInteraction()
                             {
                                unary foobar();
                             }
                             """;

        var result = IonParser.Service.Parse(input);

        Assert.That(result.Success);
    }

    [Test]
    public void Test10()
    {
        const string input = """
                             unary CreateChannel(request: CreateChannelRequest);
                             """;

        var result = IonParser.ServiceMethod.Parse(input);

        Assert.That(result.Success);
    }
}