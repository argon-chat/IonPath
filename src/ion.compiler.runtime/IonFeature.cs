namespace ion.runtime;

public record IonFeature(string name)
{
    public bool IsOrleansFeature => name.Equals("orleans");


    public static implicit operator IonFeature(string value) => new(value);
}