namespace ion.compiler;

public static class IonAnalyticCodes
{
    private static readonly Dictionary<string, IonAnalyticCode> _codeMap = new();

    static IonAnalyticCodes()
    {
        // Auto-register all codes via reflection
        foreach (var field in typeof(IonAnalyticCodes).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        {
            if (field.FieldType == typeof(IonAnalyticCode))
            {
                var code = (IonAnalyticCode)field.GetValue(null)!;
                _codeMap[code.code] = code;
            }
        }
    }

    /// <summary>
    /// Resolve a diagnostic code string to its IonAnalyticCode definition.
    /// </summary>
    public static IonAnalyticCode? Resolve(string code) => _codeMap.GetValueOrDefault(code);

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
    public static readonly IonAnalyticCode ION0009_UnresolvedTypeReferenceWithSuggestion
        = new("ION0009", "Unresolved reference to type '{0}'. Did you mean '{1}'?");

    public static readonly IonAnalyticCode ION0011_EnumBitwiseOverlap
        = new("ION0011", "Enum item '{0}' in '{1}' has overlapping bits with '{2}', both resolve to value '{3}'");

    public static readonly IonAnalyticCode ION0012_UnionSharedFieldsWithReferencedCase
        = new("ION0012", "Union '{0}' declares shared fields but contains case '{1}' that is a type reference; unions with referenced cases cannot declare shared fields.");

    public static readonly IonAnalyticCode ION0013_MultipleStreamParameters
        = new("ION0013", "Method '{0}' declares multiple stream parameters; only one parameter may be marked as 'stream'.");

    // ── Schema Lock validation codes (ION0020–ION0029) ──

    public static readonly IonAnalyticCode ION0030_CircularTypeReference
        = new("ION0030", "Circular type reference detected: {0}. This would cause infinite recursion during serialization.");

    public static readonly IonAnalyticCode ION0020_LockFieldRemoved
        = new("ION0020", "Breaking change: field '{0}' (index {1}) was removed from '{2}'. Use 'reserved' or '--update-lock' to acknowledge.");
    public static readonly IonAnalyticCode ION0021_LockFieldReordered
        = new("ION0021", "Breaking change: field '{0}' in '{1}' changed index from {2} to {3}. Field order determines wire identity.");
    public static readonly IonAnalyticCode ION0022_LockFieldTypeChanged
        = new("ION0022", "Breaking change: field '{0}' in '{1}' changed type from '{2}' to '{3}'.");
    public static readonly IonAnalyticCode ION0023_LockDefinitionRemoved
        = new("ION0023", "Breaking change: definition '{0}' ({1}) was removed.");
    public static readonly IonAnalyticCode ION0024_LockDefinitionKindChanged
        = new("ION0024", "Breaking change: definition '{0}' changed kind from '{1}' to '{2}'.");
    public static readonly IonAnalyticCode ION0025_LockMethodRemoved
        = new("ION0025", "Service '{0}' removed method '{1}'. Existing clients will fail.");
    public static readonly IonAnalyticCode ION0026_LockMethodSignatureChanged
        = new("ION0026", "Breaking change: method '{0}.{1}' signature changed: {2}.");
    public static readonly IonAnalyticCode ION0027_LockEnumValueChanged
        = new("ION0027", "Breaking change: {0} '{1}' member '{2}' changed value from '{3}' to '{4}'.");
    public static readonly IonAnalyticCode ION0028_LockUnionCaseReordered
        = new("ION0028", "Breaking change: union '{0}' case '{1}' changed index from {2} to {3}. Index is the wire discriminator.");
    public static readonly IonAnalyticCode ION0029_LockFieldAddedNonNullable
        = new("ION0029", "Field '{0}' added to '{1}' is not nullable. Older readers will fail to deserialize. Consider using '{0}: {2}?'.");
}

public record IonAnalyticCode(string code, string template);