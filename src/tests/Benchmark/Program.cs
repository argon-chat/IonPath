
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using ion.runtime;
using MemoryPack;
using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;
using Newtonsoft.Json;
using System.Buffers;
using System.Formats.Cbor;
using System.Text;
using TestContracts;
using JsonSerializer = System.Text.Json.JsonSerializer;

[MemoryDiagnoser]
public class SerializationBenchmarks
{
    public static VectorOfVectorOfVector Entity = new(
        new VectorOfVector(new Vector(1, 2, 3), new Vector(4, 5, 6), new Vector(6, 7, 8)),
        new VectorOfVector(new Vector(1, 2, 3), new Vector(4, 5, 6), new Vector(6, 7, 8)));

    private static readonly CborWriter writerCbor = new();
    private static readonly IMemoryOwner<byte> bufferIon = MemoryPool<byte>.Shared.Rent(1024);
    private static readonly IMemoryOwner<byte> bufferMsgPack = MemoryPool<byte>.Shared.Rent(1024);

    [Benchmark(Baseline = true)]
    public ReadOnlySpan<byte> SystemJson() => JsonSerializer.SerializeToUtf8Bytes(Entity);

    [Benchmark]
    public ReadOnlySpan<byte> IonSerialization()
    {
        writerCbor.Reset();
        IonFormatterStorage<VectorOfVectorOfVector>.Write(writerCbor, Entity);
        writerCbor.Encode(bufferIon.Memory.Span);
        return bufferIon.Memory.Span[..writerCbor.BytesWritten];
    }

    [Benchmark]
    public ReadOnlySpan<byte> NewtonsoftJson() => Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(Entity));

    [Benchmark]
    public ReadOnlySpan<byte> MessagePackSerialization() => MessagePackSerializer.Serialize(typeof(VectorOfVectorOfVector), Entity, GetOptions.Value);


    private static readonly Lazy<MessagePackSerializerOptions> GetOptions = new(() =>
    {
        var resolver = CompositeResolver.Create([
                new MsgVectorFormatter(),
                new MsgVectorOfVectorOfVectorFormatter(),
                new MsgVectorOfVectorFormatter()
            ],
            [StandardResolver.Instance]);

        return MessagePackSerializerOptions.Standard.WithResolver(resolver);
    });
    public static void Main(string[] args)
    {
        BenchmarkRunner.Run<SerializationBenchmarks>();
    }
}


public class MsgVectorFormatter : IMessagePackFormatter<Vector?>
{
    public void Serialize(ref MessagePackWriter writer, Vector? value, MessagePackSerializerOptions options)
    {
        writer.WriteArrayHeader(3);
        writer.Write(value!.x);
        writer.Write(value.y);
        writer.Write(value.z);
    }

    public Vector Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        var count = reader.ReadArrayHeader();
        var x = reader.ReadInt32();
        var y = reader.ReadInt32();
        var z = reader.ReadInt32();
        return new Vector(x, y, z);
    }
}

public class MsgVectorOfVectorFormatter : IMessagePackFormatter<VectorOfVector?>
{
    public void Serialize(ref MessagePackWriter writer, VectorOfVector? value, MessagePackSerializerOptions options)
    {
        writer.WriteArrayHeader(3);
        MessagePackSerializer.Serialize(ref writer, value!.x, options);
        MessagePackSerializer.Serialize(ref writer, value.y, options);
        MessagePackSerializer.Serialize(ref writer, value.z, options);
    }

    public VectorOfVector Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        var count = reader.ReadArrayHeader();
        var a = MessagePackSerializer.Deserialize<Vector>(ref reader, options);
        var b = MessagePackSerializer.Deserialize<Vector>(ref reader, options);
        var c = MessagePackSerializer.Deserialize<Vector>(ref reader, options);
        return new VectorOfVector(a, b, c);
    }
}

public class MsgVectorOfVectorOfVectorFormatter : IMessagePackFormatter<VectorOfVectorOfVector?>
{
    public void Serialize(ref MessagePackWriter writer, VectorOfVectorOfVector? value, MessagePackSerializerOptions options)
    {
        writer.WriteArrayHeader(2);
        MessagePackSerializer.Serialize(ref writer, value!.z, options);
        MessagePackSerializer.Serialize(ref writer, value.w, options);
    }

    public VectorOfVectorOfVector Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        var count = reader.ReadArrayHeader();
        var v1 = MessagePackSerializer.Deserialize<VectorOfVector>(ref reader, options);
        var v2 = MessagePackSerializer.Deserialize<VectorOfVector>(ref reader, options);
        return new VectorOfVectorOfVector(v1, v2);
    }
}