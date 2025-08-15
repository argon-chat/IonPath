namespace ion.runtime.network;

using System.Formats.Cbor;

public record struct IonProtocolError(string code, string msg)
{
    public static IonProtocolError UPSTREAM_ERROR(string msg) => new("UPSTREAM_ERROR", msg);
}

public class IonProtocolErrorFormatter : IonFormatter<IonProtocolError>
{
    public IonProtocolError Read(CborReader reader)
    {
        var code = reader.ReadTextString();
        var msg = reader.ReadTextString();
        return new(code, msg);
    }

    public void Write(CborWriter writer, IonProtocolError value)
    {
        writer.WriteTextString(value.code);
        writer.WriteTextString(value.msg);
    }
}