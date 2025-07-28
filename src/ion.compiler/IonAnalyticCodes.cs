namespace ion.compiler;

public static class IonAnalyticCodes
{
    public static readonly IonAnalyticCode ION0001_CycleImportDetected = new("ION0001", "Cyclic module import detected: {0}");
    public static readonly IonAnalyticCode ION0002_DuplicateDefinition = new("ION0002", "Duplicate definition of '{0}' in module '{1}', first defined here {2}");
}

public record IonAnalyticCode(string code, string template);