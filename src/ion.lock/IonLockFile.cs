namespace ion.runtime.locking;

public class IonLockFile
{
    public static IonLockFile Open(FileInfo file)
    {
        ReadOnlyMemory<u1> bytes = File.ReadAllBytes(file.FullName);

        var header = bytes.Span[..3];

        var reader = new CborReader(bytes[4..]);

        reader.ReadStartArray();
        var r = IonFormatterStorage<LockFileEntity>.Read(reader);
        reader.ReadEndArray();
        return new();
    }

    public static unsafe void Write(IonLockFile entity, FileInfo file)
    {
        var writer = new CborWriter();




        writer.WriteStartArray(null);

        IonFormatterStorage<LockFileEntity>.Write(writer, new LockFileEntity(4, "123", "811", IonArray<u1>.Empty, IonArray<TypeEntity>.Empty));

        writer.WriteEndArray();


        Span<u1> span = stackalloc u1[writer.BytesWritten + 4];


        span[0] = (u1)'I';
        span[1] = (u1)'O';
        span[2] = (u1)'N';
        span[3] = (u1)'L';

        writer.Encode(span[4..]);

        File.WriteAllBytes(file.FullName, span);
    }
}