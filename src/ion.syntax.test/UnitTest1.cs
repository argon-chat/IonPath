namespace ion.syntax.test;

using Pidgin;
using System;
using static Assert;

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

        That(result.Success);
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

        That(result.Success);
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

        That(result.Success);
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

        That(result.Success);
    }

    [Test]
    public void Test5()
    {
        const string input = """
                             #use "foobar"
                             
                             
                             """;

        var result = IonParser.IonFile.Parse(input);

        That(result.Success);
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

        That(result.Success);
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

        That(result.Success);
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

        That(result.Success);
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

        That(result.Success);
    }

    [Test]
    public void Test10()
    {
        const string input = """
                             unary CreateChannel(request: CreateChannelRequest);
                             """;

        var result = IonParser.ServiceMethod.Parse(input);

        That(result.Success);
    }


    [Test]
    public void Test11()
    {
        const string input = """
                             enum ChannelType: u2
                             {
                                 Text,
                                 Voice,
                                 Announcement
                             }
                             """;

        var result = IonParser.Enums.Parse(input);

        That(result.Success);
    }

    [Test]
    public void Test12()
    {
        const string input = """
                             enum ChannelType: u2
                             {
                                 Text,
                                 Voice,
                                 Announcement
                             }
                             """;

        var result = IonParser.IonFile.Parse(input);

        That(result.Success);
        That(!result.Value.OfType<InvalidIonBlock>().Any());
    }


    [Test]
    public void Test13()
    {
        const string input = """
                             enum JoinToChannelError: u2 {
                                 NONE,
                                 CHANNEL_IS_NOT_VOICE
                             }
                             """;

        var result = IonParser.Enums.Parse(input);

        That(result.Success);
    }

    [Test]
    public void Test14()
    {
        const string input = """
                             flags ChannelMemberState
                             {
                                 NONE                       = 0,
                                 MUTED                      = 1 << 1,
                                 MUTED_BY_SERVER            = 1 << 2,
                                 MUTED_HEADPHONES           = 1 << 3,
                                 MUTED_HEADPHONES_BY_SERVER = 1 << 4,
                                 STREAMING                  = 1 << 5
                             }
                             
                             """;

        var result = IonParser.Flags.Parse(input);

        That(result.Success);
    }

    [Test]
    public void Test15()
    {
        const string input = """
                             union FooUnion {
                                Case1(id: i4),
                                Case2(name: string)
                             }
                             """;

        var result = IonParser.Union.Parse(input);

        That(result.Success);
    }

    [Test]
    public void Test16()
    {
        const string input = """
                             union FooUnion(token: string) {
                                Case1(id: i4),
                                Case2(name: string)
                             }
                             """;

        var result = IonParser.Union.Parse(input);

        That(result.Success);
    }

    [Test]
    public void Test17()
    {
        const string input = """
                             union FooUnion(token: string) {
                                Case1,
                                Case2(name: string)
                             }
                             """;

        var result = IonParser.Union.Parse(input);

        That(result.Success);
    }

    [Test]
    public void Test18()
    {
        const string input = """
                             attribute @AllowAnonymous();
                             """;

        var result = IonParser.AttributeDef.Parse(input);

        That(result.Success);
    }

    [Test]
    public void Test19()
    {
        const string input = """
                             attribute @AllowAnonymous();
                             """;

        var result = IonParser.Definition.Parse(input);

        That(result.Success);
    }


    [Test]
    public void Test20()
    {
        const string input = """
                             
                             
                             attribute @AllowAnonymous();
                             attribute @MachineIdOptional();
                             """;

        var result = IonParser.IonFile.Parse(input);

        That(result.Success);
    }

    
}