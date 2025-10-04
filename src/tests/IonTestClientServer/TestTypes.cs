namespace IonTestClientServer;

using ion.runtime;
using System.Formats.Cbor;
using System.Security.Cryptography;
using TestContracts;

public class TestTypes
{
    [Test]
    public void TestDateOnly()
    {
        var date = new DateTime(1992, 12, 4, 12, 19, 5);
        var dateonly = DateOnly.FromDateTime(date);

        var writer = new CborWriter();
        writer.WriteStartArray(1);


        IonFormatterStorage<DateOnly>.Write(writer, dateonly);

        writer.WriteEndArray();


        var reader = new CborReader(writer.Encode());


        reader.ReadStartArray();

        var dateOriginal = IonFormatterStorage<DateOnly>.Read(reader);

        reader.ReadEndArray();
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