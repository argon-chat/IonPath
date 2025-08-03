namespace ion.compiler;

public static class IonAnalyticCodes
{
    public static readonly IonAnalyticCode ION0001_CycleImportDetected 
        = new("ION0001", "Cyclic module import detected: {0}");
    public static readonly IonAnalyticCode ION0002_DuplicateDefinition 
        = new("ION0002", "Duplicate definition of '{0}' in module '{1}', first defined here {2}");
    public static readonly IonAnalyticCode ION0003_TypeNotFoundOrNotBuiltin 
        = new("ION0003", "Type '{0}' not found or is not a standard builtin type.");
    public static readonly IonAnalyticCode ION0004_TypeNotAllowedInAttributeArguments 
        = new("ION0004", "Type '{0}' is not allowed in attribute arguments.");
    public static readonly IonAnalyticCode ION0005_AttributeNotFoundOrMissingDependency
        = new("ION0005", "Attribute '{0}' not found. It may be missing a required import or feature.");
    public static readonly IonAnalyticCode ION0006_DuplicateEnumName
        = new("ION0006", "Duplicate enum item name '{0}' in enum '{1}', first defined here {2}");
    public static readonly IonAnalyticCode ION0007_InvalidEnumValue
        = new("ION0007", "Invalid value '{0}' for enum '{1}': value must be a constant integer.");
    public static readonly IonAnalyticCode ION0008_DuplicateEnumValue
        = new("ION0008", "Duplicate enum value '{0}' in enum '{1}', previously assigned to '{2}' at {3}");
    public static readonly IonAnalyticCode ION0009_UnresolvedTypeReference
        = new("ION0009", "Unresolved reference to type '{0}'. The type may be missing, misspelled, or not imported.");
}

public record IonAnalyticCode(string code, string template);