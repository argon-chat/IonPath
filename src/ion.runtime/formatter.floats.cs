namespace ion.runtime;

public sealed class Ion_f2_Formatter : IonFormatter<Half>
{
    public Half Read(CborReader reader)
        => reader.ReadHalf();

    public void Write(CborWriter writer, ref Half value)
        => writer.WriteHalf(value);
}

public sealed class Ion_f4_Formatter : IonFormatter<float>
{
    public float Read(CborReader reader)
        => reader.ReadSingle();

    public void Write(CborWriter writer, ref float value)
        => writer.WriteSingle(value);
}

public sealed class Ion_f8_Formatter : IonFormatter<double>
{
    public double Read(CborReader reader)
        => reader.ReadDouble();

    public void Write(CborWriter writer, ref double value)
        => writer.WriteDouble(value);
}