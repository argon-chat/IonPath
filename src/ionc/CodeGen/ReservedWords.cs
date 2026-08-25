namespace ion.compiler.CodeGen;

/// <summary>
/// The three target languages' reserved-word lists, and the escape each one wants when an Ion
/// name lands in a <em>declaration</em> position.
/// </summary>
/// <remarks>
/// <para>
/// An Ion identifier is not constrained by any target language's grammar, so a field spelled
/// <c>fixed:</c>, <c>type:</c> or <c>class:</c> is legal Ion that used to emit C#, Rust and
/// TypeScript that does not parse. Every generator funnels its declaration identifiers through
/// this type so the word lists live in one place instead of being repeated at each emission site.
/// </para>
/// <para>
/// <b>The boundary this type must never cross.</b> The escape belongs only where the Ion name
/// becomes an identifier the target compiler reads as a declaration, or as a reference to one. It
/// must <em>not</em> be applied where the same name is data:
/// </para>
/// <list type="bullet">
/// <item>a CBOR map key — <c>IonPartial&lt;T&gt;</c> is keyed by field name in all three runtimes;</item>
/// <item>a string literal — the formatter registry names, <c>ReadStartMessage(n, "Msg")</c>, the
/// method name a service router dispatches on;</item>
/// <item>an <c>ion.lock.json</c> entry, which records the Ion spelling;</item>
/// <item>a doc-comment reference (<c>&lt;param name="…"&gt;</c>, rustdoc <c># Arguments</c>), which
/// names the symbol rather than the token.</item>
/// </list>
/// <para>
/// Escaping one of those would move the wire format silently, which is a far worse failure than
/// the compile error being fixed here.
/// </para>
/// </remarks>
public static class ReservedWords
{
    // ═══════════════════════════════════════════════════════════════════
    // C#
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// C#'s reserved keywords — the words that may never be an identifier without <c>@</c>.
    /// </summary>
    /// <remarks>
    /// Contextual keywords (<c>value</c>, <c>record</c>, <c>var</c>, <c>when</c>, …) are
    /// deliberately absent: they are legal identifiers, and escaping them would churn generated
    /// output for names that compile perfectly well today.
    /// </remarks>
    private static readonly HashSet<string> CSharp = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
        "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual",
        "void", "volatile", "while"
    };

    /// <summary>Whether <paramref name="name"/> is a C# reserved keyword.</summary>
    public static bool IsCSharpKeyword(string name) => CSharp.Contains(name);

    /// <summary>
    /// <paramref name="name"/> as a C# declaration identifier: <c>fixed</c> becomes <c>@fixed</c>,
    /// everything else is returned unchanged.
    /// </summary>
    /// <remarks>
    /// The <c>@</c> is lexical only — the declared symbol is still named <c>fixed</c>, so
    /// <c>nameof(@fixed)</c> is <c>"fixed"</c> and <c>&lt;param name="fixed"&gt;</c> still binds.
    /// That is also what makes it safe at a member access (<c>value.@fixed</c>) and not only at the
    /// declaration.
    /// </remarks>
    public static string EscapeCSharp(string name) => IsCSharpKeyword(name) ? $"@{name}" : name;

    // ═══════════════════════════════════════════════════════════════════
    // TypeScript
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The words that may not be a TypeScript <em>binding</em> identifier inside a module.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Generated TypeScript is always a module, hence always strict mode, so the strict-mode
    /// reserved words (<c>implements</c>, <c>interface</c>, <c>let</c>, <c>package</c>,
    /// <c>private</c>, <c>protected</c>, <c>public</c>, <c>static</c>, <c>yield</c>) and
    /// <c>await</c> belong on the list beside the always-reserved ones.
    /// </para>
    /// <para>
    /// The list is consulted for binding positions only. A <em>property</em> name is an
    /// <c>IdentifierName</c> and accepts every reserved word, so
    /// <c>interface M { class: string }</c>, <c>enum E { default = 1 }</c>, <c>{ class: v }</c>,
    /// <c>m.class</c> and a class or interface method named <c>class</c> are all valid TypeScript
    /// and are left alone.
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> TypeScript = new(StringComparer.Ordinal)
    {
        "await", "break", "case", "catch", "class", "const", "continue", "debugger", "default",
        "delete", "do", "else", "enum", "export", "extends", "false", "finally", "for", "function",
        "if", "implements", "import", "in", "instanceof", "interface", "let", "new", "null",
        "package", "private", "protected", "public", "return", "static", "super", "switch", "this",
        "throw", "true", "try", "typeof", "var", "void", "while", "with", "yield"
    };

    /// <summary>Whether <paramref name="name"/> may not be a TypeScript binding identifier.</summary>
    public static bool IsTypeScriptReserved(string name) => TypeScript.Contains(name);

    /// <summary>
    /// <paramref name="name"/> as a TypeScript binding identifier — a parameter, a <c>const</c>,
    /// or the value half of an object-literal property.
    /// </summary>
    /// <remarks>
    /// TypeScript has no lexical escape, so this is a genuine <em>rename</em>: <c>class</c> becomes
    /// <c>__class</c>. Everywhere the Ion name still has to appear — the interface property, the
    /// object-literal key, the <c>Partial&lt;T&gt;</c> field descriptor — the caller keeps the
    /// unescaped name, which is why the two are separate calls at each site instead of one escape
    /// covering both.
    /// </remarks>
    public static string EscapeTypeScriptBinding(string name)
        => IsTypeScriptReserved(name) ? $"__{name}" : name;

    // ═══════════════════════════════════════════════════════════════════
    // Rust
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Rust's strict and reserved keywords, across the 2015/2018/2021 editions.</summary>
    private static readonly HashSet<string> Rust = new(StringComparer.Ordinal)
    {
        "as", "break", "const", "continue", "crate", "do", "else", "enum", "extern", "false", "fn",
        "for", "if", "impl", "in", "let", "loop", "match", "mod", "move", "mut", "pub", "ref",
        "return", "self", "Self", "static", "struct", "super", "trait", "true", "type", "unsafe",
        "use", "where", "while", "async", "await", "dyn", "abstract", "become", "box", "final",
        "macro", "override", "priv", "typeof", "unsized", "virtual", "yield", "try"
    };

    /// <summary>
    /// The four keywords a raw identifier may <b>not</b> spell: <c>r#crate</c>, <c>r#self</c>,
    /// <c>r#super</c> and <c>r#Self</c> are rejected by the grammar itself, so these take a
    /// trailing underscore instead.
    /// </summary>
    private static readonly HashSet<string> RustNonRaw = new(StringComparer.Ordinal)
    {
        "crate", "self", "Self", "super"
    };

    /// <summary>Whether <paramref name="name"/> is a Rust keyword.</summary>
    public static bool IsRustKeyword(string name) => Rust.Contains(name);

    /// <summary>
    /// <paramref name="name"/> as a Rust declaration identifier: <c>type</c> becomes
    /// <c>r#type</c>, and the four keywords the raw-identifier grammar excludes take a trailing
    /// underscore.
    /// </summary>
    /// <remarks>
    /// A raw identifier is lexical — the field is still <c>type</c> to a struct-literal shorthand
    /// and to <c>self.r#type</c> — and Ion encodes a message positionally, so nothing escaped here
    /// reaches the encoder. The one construct where it would is
    /// <c>ion_rustcore::ion_partial!</c>, which turns each field ident into its CBOR map key with
    /// <c>stringify!</c>: <c>stringify!(r#type)</c> is <c>"r#type"</c>. That path must therefore
    /// refuse rather than escape — see <c>ION0051</c> in
    /// <see cref="RustCodeGenerator.GeneratePartials"/>.
    /// </remarks>
    public static string EscapeRust(string name)
    {
        if (!IsRustKeyword(name))
            return name;

        return RustNonRaw.Contains(name) ? $"{name}_" : $"r#{name}";
    }
}
