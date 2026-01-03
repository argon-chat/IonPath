namespace ion.syntax.test;

using Pidgin;
using System;
using static Assert;

public class Tests
{
    #region Message Tests

    [Test]
    public void Message_BasicStructure_Success()
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
    public void Message_WithExtraSpacing_Success()
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
    public void Message_WithAttributes_Success()
    {
        const string input = """
                             @Serializable()
                             @Version(1)
                             msg Person {
                                name: string;
                                age: u8;
                             }
                             """;

        var result = IonParser.Message.Parse(input);

        That(result.Success);
    }

    //[Test]
    //public void Message_WithPartialType_Success()
    //{
    //    const string input = """
    //                         msg Request {
    //                            data~: Data;
    //                         }
    //                         """;

    //    var result = IonParser.Message.Parse(input);

    //    That(result.Success);
    //}

    [Test]
    public void Message_EmptyBody_Success()
    {
        const string input = """
                             msg Empty {
                             }
                             """;

        var result = IonParser.Message.Parse(input);

        That(result.Success);
    }

    #endregion

    #region Flags Tests

    [Test]
    public void Flags_BasicStructure_Success()
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
    public void Flags_WithAttribute_Success()
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
    public void Flags_WithoutExplicitType_Success()
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

    #endregion

    #region Enum Tests

    [Test]
    public void Enum_BasicStructure_Success()
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
    public void Enum_UppercaseValues_Success()
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
    public void Enum_InFile_Success()
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

    #endregion

    #region Service Tests

    [Test]
    public void Service_WithAttributes_Success()
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
    public void Service_EmptyParameters_Success()
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
    public void Service_MixedMethods_Success()
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
    public void Service_UnaryMethod_Success()
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
    public void Service_WithStreamParameters_Success()
    {
        const string input = """
                             service InventoryInteraction() {
                                 stream GetMyInventoryItems(stream e: InventoryItem): InventoryItem;
                             }
                             """;

        var result = IonParser.Service.Parse(input);

        That(result.Success);
    }

    [Test]
    public void Service_StreamReturnType_Success()
    {
        const string input = """
                             service RandomStreamInteraction() {
                                 stream Integer(): i4;
                             }
                             """;

        var result = IonParser.Service.Parse(input);

        That(result.Success);
    }

    [Test]
    public void Service_WithConstructorParameter_Success()
    {
        const string input = """
                             service RandomStreamInteraction(seed: i4) {
                                 stream Integer(): i4;
                             }
                             """;

        var result = IonParser.Service.Parse(input);

        That(result.Success);
    }

    [Test]
    public void Service_MethodWithoutParameters_Success()
    {
        const string input = """
                             service SA() {
                                 upCurrentChannel();
                             }
                             """;

        var result = IonParser.Service.Parse(input);

        That(result.Success);
    }

    #endregion

    #region ServiceMethod Tests

    [Test]
    public void ServiceMethod_UnaryWithParameters_Success()
    {
        const string input = """
                             unary CreateChannel(request: CreateChannelRequest);
                             """;

        var result = IonParser.ServiceMethod.Parse(input);

        That(result.Success);
    }

    [Test]
    public void ServiceMethod_WithoutParameters_Success()
    {
        const string input = """
                             upCurrentChannel();
                             """;

        var result = IonParser.ServiceMethod.Parse(input);

        That(result.Success);
    }

    [Test]
    public void ServiceMethod_WithSingleAttribute_Success()
    {
        const string input = """
                             @FooBar()
                             upCurrentChannel();
                             """;

        var result = IonParser.ServiceMethod.Parse(input);

        That(result.Success);
    }

    [Test]
    public void ServiceMethod_WithMultipleAttributes_Success()
    {
        const string input = """
                             @FooBar()
                             @SecondFoo()
                             upCurrentChannel();
                             """;

        var result = IonParser.ServiceMethod.Parse(input);

        That(result.Success);
    }

    [Test]
    public void ServiceMethod_WithThreeAttributes_Success()
    {
        const string input = """
                             @Auth()
                             @Log()
                             @Retry(3)
                             ProcessRequest(data: string): bool;
                             """;

        var result = IonParser.ServiceMethod.Parse(input);

        That(result.Success);
    }

    [Test]
    public void ServiceMethod_InternalModifier_Success()
    {
        const string input = """
                             internal DeleteUser(id: guid);
                             """;

        var result = IonParser.ServiceMethod.Parse(input);

        That(result.Success);
    }

    #endregion

    #region Union Tests

    [Test]
    public void Union_BasicStructure_Success()
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
    public void Union_WithBaseParameters_Success()
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
    public void Union_WithEmptyCase_Success()
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
    public void Union_WithAttributes_Success()
    {
        const string input = """
                             @Sealed()
                             union Result {
                                Success(value: i4),
                                @Error()
                                Failure(message: string)
                             }
                             """;

        var result = IonParser.Union.Parse(input);

        That(result.Success);
    }

    #endregion

    #region Attribute Tests

    [Test]
    public void AttributeDef_BasicStructure_Success()
    {
        const string input = """
                             attribute @AllowAnonymous();
                             """;

        var result = IonParser.AttributeDef.Parse(input);

        That(result.Success);
    }

    [Test]
    public void AttributeDef_AsDefinition_Success()
    {
        const string input = """
                             attribute @AllowAnonymous();
                             """;

        var result = IonParser.Definition.Parse(input);

        That(result.Success);
    }

    [Test]
    public void AttributeDef_WithParameters_Success()
    {
        const string input = """
                             attribute @Cache(duration: i4, key: string);
                             """;

        var result = IonParser.AttributeDef.Parse(input);

        That(result.Success);
    }

    #endregion

    #region File Tests

    [Test]
    public void IonFile_UseDirective_Success()
    {
        const string input = """
                             #use "foobar"
                             
                             
                             """;

        var result = IonParser.IonFile.Parse(input);

        That(result.Success);
    }

    [Test]
    public void IonFile_MultipleAttributeDefs_Success()
    {
        const string input = """
                             
                             
                             attribute @AllowAnonymous();
                             attribute @MachineIdOptional();
                             """;

        var result = IonParser.IonFile.Parse(input);

        That(result.Success);
    }

    [Test]
    public void IonFile_InvalidOptionalSyntax_Fails()
    {
        const string input = """
                             service InventoryInteraction() {
                                 GetMyInventoryItems(): InventoryItem[];
                             }
                             
                             msg InventoryItem {
                                 id: string;
                                 instanceId: guid;
                                 grantedDate: datetime;
                                 rarity: ItemRarity;
                                 tags: string[];
                                 useable: bool;
                                 giftable: bool;
                                 iconId: string;
                                 usableVector?: ItemUseVector;
                             }
                             
                             enum ItemRarity {
                                 Common,
                                 Rare,
                                 Legendary,
                                 Relic
                             }
                             
                             enum ItemUseVector {
                                 RedeemCode,
                                 SpacePremium,
                                 UserPremium
                             }
                             """;

        var result = IonParser.IonFile.Parse(input);

        That(!result.Success);
        That(result.Error!.Message, Is.EqualTo("'?' is not allowed after field name"));
    }

    [Test]
    public void IonFile_CompleteContract_Success()
    {
        const string input = """
                             #use "common"
                             
                             @Grain()
                             service UserService(userId: guid) {
                                 GetUser(): User;
                                 UpdateUser(user: User);
                                 DeleteUser();
                             }
                             
                             msg User {
                                 id: guid;
                                 name: string;
                                 email: string;
                                 createdAt: datetime;
                             }
                             """;

        var result = IonParser.IonFile.Parse(input);

        That(result.Success);
        That(!result.Value.OfType<InvalidIonBlock>().Any());
    }

    #endregion

    #region Type Tests

    [Test]
    public void Type_GenericType_Success()
    {
        const string input = """
                             msg Response {
                                 data: Result<User>;
                             }
                             """;

        var result = IonParser.Message.Parse(input);

        That(result.Success);
    }

    //[Test]
    //public void Type_NestedGenericType_Success()
    //{
    //    const string input = """
    //                         msg Response {
    //                             data: Result<List<User>>;
    //                         }
    //                         """;

    //    var result = IonParser.Message.Parse(input);

    //    That(result.Success);
    //}

    [Test]
    public void Type_OptionalArray_Success()
    {
        const string input = """
                             msg Data {
                                 items: string[]?;
                             }
                             """;

        var result = IonParser.Message.Parse(input);

        That(result.Success);
    }

    #endregion

    #region Edge Cases

    [Test]
    public void EdgeCase_MinimalService_Success()
    {
        const string input = """
                             service S(){}
                             """;

        var result = IonParser.Service.Parse(input);

        That(result.Success);
    }

    [Test]
    public void EdgeCase_SingleLetterNames_Success()
    {
        const string input = """
                             msg A {
                                 b: i4;
                             }
                             """;

        var result = IonParser.Message.Parse(input);

        That(result.Success);
    }

    [Test]
    public void EdgeCase_UnderscoreInNames_Success()
    {
        const string input = """
                             msg User_Profile {
                                 user_id: guid;
                                 first_name: string;
                             }
                             """;

        var result = IonParser.Message.Parse(input);

        That(result.Success);
    }

    [Test]      
    public void EdgeCase_MultipleUseDirectives_Success()
    {
        const string input = """
                             #use "common"
                             #use "models"
                             #use "services"
                             
                             msg Empty {}
                             """;

        var result = IonParser.IonFile.Parse(input);

        That(result.Success);
    }

    #endregion
}