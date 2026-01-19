namespace ion.compiler.CodeGen;

public sealed class TemplateContext
{
    private readonly Dictionary<string, string> values = new();

    public TemplateContext Set(string key, string value)
    {
        values[key] = value;
        return this;
    }

    public string Apply(string template)
    {
        var result = template;
        foreach (var (key, value) in values)
        {
            result = result.Replace($"{{{key}}}", value);
        }
        return result;
    }
}
