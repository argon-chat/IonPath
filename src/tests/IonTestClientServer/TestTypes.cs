namespace IonTestClientServer;

using ion.runtime;
using System.Formats.Cbor;
using System.Security.Cryptography;
using TestContracts;

public static class GuidV7
{
    private static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

    public static Guid NewGuid()
    {
        // Timestamp: миллисекунды с Unix epoch
        long unixTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        Span<byte> bytes = stackalloc byte[16];
        Rng.GetBytes(bytes); // сначала всё рандом

        // Записываем timestamp в big-endian в первые 6 байт
        bytes[0] = (byte)((unixTimeMs >> 40) & 0xFF);
        bytes[1] = (byte)((unixTimeMs >> 32) & 0xFF);
        bytes[2] = (byte)((unixTimeMs >> 24) & 0xFF);
        bytes[3] = (byte)((unixTimeMs >> 16) & 0xFF);
        bytes[4] = (byte)((unixTimeMs >> 8) & 0xFF);
        bytes[5] = (byte)(unixTimeMs & 0xFF);

        // version (v7 = 0111)
        bytes[6] &= 0x0F;
        bytes[6] |= 0x70;

        // variant (10xx)
        bytes[8] &= 0x3F;
        bytes[8] |= 0x80;

        // теперь надо учесть, что Guid ctor little-endian для первых полей
        int a = (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];
        short b = (short)((bytes[4] << 8) | bytes[5]);
        short c = (short)((bytes[6] << 8) | bytes[7]);

        return new Guid(a, b, c, bytes[8], bytes[9], bytes[10], bytes[11],
            bytes[12], bytes[13], bytes[14], bytes[15]);
    }
}
public class TestTypes
{
    [Test]
    public void TestV7()
    {
        var v7_1 = Guid.CreateVersion7();
        var v7_2 = Guid.CreateVersion7();
        var v7_shim = GuidV7.NewGuid();


    }

    [Test]
    public void TestPartial()
    {
        var original = new Vector(2, 4, 8);

        var p = new IonPartial<Vector>();

        p.SetField(x => x.x, PartialField<float>.Modified(4));
        p.SetField(x => x.y, PartialField<float>.Removed());

        var writer = new CborWriter();

        writer.WriteStartArray(1);

        IonFormatterStorage<IonPartial<Vector>>.Write(writer, p);

        writer.WriteEndArray();

        var reader = new CborReader(writer.Encode());


        reader.ReadStartArray();

        var pOriginal = IonFormatterStorage<IonPartial<Vector>>.Read(reader);

        reader.ReadEndArray();


        pOriginal
            .On(x => x.x, x => original = original with { x = x })
            .On(x => x.y, y => original = original with { y = y })
            .On(x => x.z, z => original = original with { z = z });


        Assert.That(original.x, Is.EqualTo(4));
        Assert.That(original.y, Is.EqualTo(0));
        Assert.That(original.z, Is.EqualTo(8));
    }
}