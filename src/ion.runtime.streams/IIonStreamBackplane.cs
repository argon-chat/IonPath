namespace ion.runtime.network;

using System.Formats.Cbor;

/// <summary>
/// Carries <see cref="IIonStreamConnections"/> operations to the other server nodes, so a push to a
/// group, a user or everyone reaches connections wherever they landed — SignalR's backplane.
/// </summary>
/// <remarks>
/// <para>Only what can be named travels: the whole server, connection ids, a group, a user, a
/// session. <see cref="IIonStreamConnections.Where"/> takes a predicate, which cannot cross a
/// process boundary, and stays local.</para>
///
/// <para>Delivery is at most once per node and in publish order per node. A node never receives
/// its own messages back — local delivery happens before the publish.</para>
/// </remarks>
public interface IIonStreamBackplane
{
    /// <summary>Publishes a message for every other node.</summary>
    ValueTask PublishAsync(IonBackplaneMessage message, CancellationToken ct = default);

    /// <summary>
    /// Starts delivering other nodes' messages to <paramref name="handler"/>. Returns once the
    /// subscription is live: anything published after that is delivered.
    /// </summary>
    Task StartAsync(string nodeId, Func<IonBackplaneMessage, ValueTask> handler, CancellationToken ct);

    /// <summary>Stops delivering. Messages published meanwhile are not replayed to this node.</summary>
    Task StopAsync(CancellationToken ct);
}

public enum IonBackplaneCommand : byte
{
    Send = 1,
    Close = 2,
    Abort = 3,
    AddToGroup = 4,
    RemoveFromGroup = 5
}

public enum IonBackplaneTargetKind : byte
{
    All = 1,
    Connections = 2,
    Group = 3,
    User = 4,
    Session = 5
}

/// <summary>One operation on a set of connections, as it travels between nodes.</summary>
public sealed class IonBackplaneMessage
{
    /// <summary>Bumped when the encoding changes shape. A node drops messages from a newer version.</summary>
    public const int CurrentVersion = 1;

    public required string OriginNodeId { get; init; }

    public required IonBackplaneCommand Command { get; init; }

    public required IonBackplaneTargetKind TargetKind { get; init; }

    /// <summary>Connection ids for <see cref="IonBackplaneTargetKind.Connections"/>; the one group, user or session name otherwise; empty for <see cref="IonBackplaneTargetKind.All"/>.</summary>
    public string[] Targets { get; init; } = [];

    /// <summary>Connection ids to leave out.</summary>
    public string[] Excluded { get; init; } = [];

    /// <summary>The group of an <see cref="IonBackplaneCommand.AddToGroup"/> / <see cref="IonBackplaneCommand.RemoveFromGroup"/>.</summary>
    public string? Group { get; init; }

    /// <summary>The assembly-qualified CLR type <see cref="Payload"/> was encoded as.</summary>
    public string? PayloadType { get; init; }

    /// <summary>The pushed item, CBOR-encoded with <see cref="PayloadType"/>'s formatter.</summary>
    public ReadOnlyMemory<byte> Payload { get; init; }

    /// <summary>The close or abort reason.</summary>
    public string? Reason { get; init; }

    public bool AllowReconnect { get; init; }

    /// <summary>Encodes the message as one CBOR array.</summary>
    public byte[] Encode()
    {
        var writer = new CborWriter();
        writer.WriteStartArray(11);
        writer.WriteInt32(CurrentVersion);
        writer.WriteTextString(OriginNodeId);
        writer.WriteInt32((int)Command);
        writer.WriteInt32((int)TargetKind);
        WriteStrings(writer, Targets);
        WriteStrings(writer, Excluded);
        WriteText(writer, Group);
        WriteText(writer, PayloadType);
        writer.WriteByteString(Payload.Span);
        WriteText(writer, Reason);
        writer.WriteBoolean(AllowReconnect);
        writer.WriteEndArray();
        return writer.Encode();
    }

    /// <summary>Decodes <see cref="Encode"/>'s output; null for a message from a newer encoding.</summary>
    /// <exception cref="CborContentException">The bytes are not a backplane message.</exception>
    public static IonBackplaneMessage? Decode(ReadOnlyMemory<byte> encoded)
    {
        var reader = new CborReader(encoded);
        var size = reader.ReadStartArray() ?? throw new CborContentException("A backplane message is a definite-length array");
        var version = reader.ReadInt32();
        if (version > CurrentVersion)
            return null;
        if (size < 11)
            throw new CborContentException($"A backplane message has 11 elements, not {size}");

        var message = new IonBackplaneMessage
        {
            OriginNodeId = reader.ReadTextString(),
            Command = (IonBackplaneCommand)reader.ReadInt32(),
            TargetKind = (IonBackplaneTargetKind)reader.ReadInt32(),
            Targets = ReadStrings(reader),
            Excluded = ReadStrings(reader),
            Group = ReadText(reader),
            PayloadType = ReadText(reader),
            Payload = reader.ReadByteString(),
            Reason = ReadText(reader),
            AllowReconnect = reader.ReadBoolean()
        };

        for (var i = 11; i < size; i++)
            reader.SkipValue();
        reader.ReadEndArray();
        return message;
    }

    private static void WriteStrings(CborWriter writer, string[] values)
    {
        writer.WriteStartArray(values.Length);
        foreach (var value in values)
            writer.WriteTextString(value);
        writer.WriteEndArray();
    }

    private static string[] ReadStrings(CborReader reader)
    {
        var count = reader.ReadStartArray() ?? throw new CborContentException("Expected a definite-length array");
        var values = count == 0 ? [] : new string[count];
        for (var i = 0; i < count; i++)
            values[i] = reader.ReadTextString();
        reader.ReadEndArray();
        return values;
    }

    private static void WriteText(CborWriter writer, string? value)
    {
        if (value is null) writer.WriteNull();
        else writer.WriteTextString(value);
    }

    private static string? ReadText(CborReader reader)
    {
        if (reader.PeekState() != CborReaderState.Null)
            return reader.ReadTextString();
        reader.ReadNull();
        return null;
    }
}
