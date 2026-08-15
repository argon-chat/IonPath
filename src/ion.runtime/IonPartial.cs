namespace ion.runtime;

using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;

// ─────────────────────────────────────────────────────────────────────────────
//  Partial<T>  ("T~" in Ion source) — WIRE FORMAT
//  This must stay byte-identical with packages/ion.webcore.js and
//  packages/ion.rustcore. Golden vectors: /tests/golden/partial.golden.json
//
//      partial := map(N)             definite length on write.
//                                    Readers MUST also accept an
//                                    indefinite-length map (0xBF … 0xFF).
//        key   := text string        the Ion field name
//        value := null (0xF6)        the field is CLEARED
//               | <field encoding>   the field is MODIFIED to that value
//
//  A field that does not appear in the map is UNTOUCHED.
//  Unknown keys are skipped on read (forward compatibility).
//  Fields are written in Ion declaration order — i.e. the order of the
//  registered schema — so the same patch produces the same bytes everywhere.
//
//  "Cleared" and "modified to null" are indistinguishable on the wire (both are
//  0xF6). This is deliberate: `null` in the map means "cleared", and for a
//  Maybe<T> field "cleared" and "set to none" are therefore the same patch.
//
//  MIGRATION NOTE (roadmap 1.1 — explicit field indices + reserved):
//  integer keys would be smaller — map(N) { 0: …, 3: null } — but they require
//  a stable per-field number, which the language does not have yet. When 1.1
//  lands, the key type becomes an unsigned integer and this becomes a wire
//  break; the reader can be made to accept both (text = name, uint = index)
//  during a transition window. Not implemented.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// A sparse patch over <typeparamref name="T"/>: every field is either
/// untouched, modified to a value, or cleared.
/// </summary>
/// <remarks>
/// The set of encodable fields comes from a code-generated schema registered
/// through <see cref="IonPartialSchema{T}.Register"/> — never from reflection
/// over <typeparamref name="T"/> at encode time. When no schema was registered
/// the formatter falls back to a reflection-derived schema
/// (<see cref="ReflectionPartialSchema"/>), which is AOT/trimming hostile and
/// only best-effort about field order.
/// </remarks>
public class IonPartial<T>
{
    private readonly Dictionary<string, Entry> fields = new(StringComparer.Ordinal);

    private readonly record struct Entry(PartialState State, object? Boxed);

    /// <summary>Number of touched (modified or cleared) fields.</summary>
    public int Count => fields.Count;

    public void SetField<TField>(Expression<Func<T, TField>> selector, PartialField<TField> value)
        => SetField(GetMemberName(selector), value);

    /// <summary>
    /// Name-based setter. <paramref name="name"/> is the Ion field name, which is
    /// also the CLR member name used by the expression-based overload.
    /// Setting a field to <see cref="PartialState.None"/> untouches it.
    /// </summary>
    public void SetField<TField>(string name, PartialField<TField> value)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (value.State == PartialState.None)
        {
            fields.Remove(name);
            return;
        }

        fields[name] = new Entry(value.State, value);
    }

    public PartialField<TField> GetField<TField>(Expression<Func<T, TField>> selector)
        => GetField<TField>(GetMemberName(selector));

    /// <summary>Name-based getter. Returns <see cref="PartialField{T}.None"/> when untouched.</summary>
    public PartialField<TField> GetField<TField>(string name)
    {
        if (fields.TryGetValue(name, out var e) && e.Boxed is PartialField<TField> f)
            return f;

        return PartialField<TField>.None;
    }

    /// <summary>State of a field by name, without needing to know its type.</summary>
    public PartialState StateOf(string name)
        => fields.TryGetValue(name, out var e) ? e.State : PartialState.None;

    /// <summary>Fluent sugar for <c>SetField(selector, PartialField&lt;TField&gt;.Modified(value))</c>.</summary>
    public IonPartial<T> Modify<TField>(Expression<Func<T, TField>> selector, TField value)
    {
        SetField(selector, PartialField<TField>.Modified(value));
        return this;
    }

    /// <summary>Fluent sugar for <c>SetField(selector, PartialField&lt;TField&gt;.Removed())</c>.</summary>
    public IonPartial<T> Remove<TField>(Expression<Func<T, TField>> selector)
    {
        SetField(selector, PartialField<TField>.Removed());
        return this;
    }

    public void Clear() => fields.Clear();

    private static string GetMemberName<TField>(Expression<Func<T, TField>> selector) =>
        selector.Body switch
        {
            MemberExpression m => m.Member.Name,
            UnaryExpression { Operand: MemberExpression mm } => mm.Member.Name,
            _ => throw new ArgumentException("Selector must be a property access", nameof(selector))
        };

    /// <summary>Names of all touched fields. Order is unspecified; the encoder uses schema order.</summary>
    public IEnumerable<string> PresentFields() => fields.Keys;

    public IonPartial<T> On<TField>(Expression<Func<T, TField>> selector, Action<TField?> handler)
    {
        var field = GetField(selector);
        switch (field.State)
        {
            case PartialState.Modified:
                handler(field.Value);
                return this;
            case PartialState.Removed:
                handler(default!);
                return this;
            case PartialState.None:
            default:
                return this;
        }
    }

    /// <summary>
    /// Like <see cref="On{TField}(Expression{Func{T,TField}},Action{TField})"/> but keeps
    /// "cleared" distinguishable from "modified to default".
    /// </summary>
    public IonPartial<T> On<TField>(
        Expression<Func<T, TField>> selector,
        Action<TField?> onModified,
        Action onRemoved)
    {
        var field = GetField(selector);
        switch (field.State)
        {
            case PartialState.Modified:
                onModified(field.Value);
                return this;
            case PartialState.Removed:
                onRemoved();
                return this;
            case PartialState.None:
            default:
                return this;
        }
    }

    /// <summary>Internal access for schema descriptors: the raw entry for a field.</summary>
    internal bool TryGetEntry(string name, out PartialState state, out object? boxed)
    {
        if (fields.TryGetValue(name, out var e))
        {
            state = e.State;
            boxed = e.Boxed;
            return true;
        }

        state = PartialState.None;
        boxed = null;
        return false;
    }

    public override string ToString()
        => $"IonPartial<{typeof(T).Name}>[{string.Join(", ", fields.Select(kv => $"{kv.Key}={kv.Value.State}"))}]";
}

public enum PartialState
{
    None,
    Modified,
    Removed
}

public readonly struct PartialField<T>
{
    public PartialState State { get; }
    public T? Value { get; }

    private PartialField(PartialState state, T? value)
    {
        State = state;
        Value = value;
    }

    public static PartialField<T> None => new(PartialState.None, default);
    public static PartialField<T> Modified(T? value) => new(PartialState.Modified, value);
    public static PartialField<T> Removed() => new(PartialState.Removed, default);

    public bool HasValue => State == PartialState.Modified;
    public bool IsRemoved => State == PartialState.Removed;

    public override string ToString() =>
        State switch
        {
            PartialState.None => $"[None]",
            PartialState.Removed => $"[Removed]",
            PartialState.Modified when Value is null => $"[Modified: null]",
            PartialState.Modified => $"[Modified: {Value}]",
            _ => $"[Unknown]"
        };
}

// ─────────────────────────────────────────────────────────────────────────────
//  Schema
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// One field of a <see cref="IonPartial{T}"/> schema. Codegen builds these via
/// the factories on <see cref="IonPartialSchema{T}"/>.
/// </summary>
public abstract class IonPartialFieldSchema<T>
{
    protected IonPartialFieldSchema(string name)
        => Name = name ?? throw new ArgumentNullException(nameof(name));

    /// <summary>The Ion field name — used as the CBOR map key *and* as the
    /// dictionary key produced by <c>SetField(x =&gt; x.Member)</c>.</summary>
    public string Name { get; }

    public abstract Type FieldType { get; }

    /// <summary>Patch state of this field, independent of the stored value.</summary>
    public PartialState GetState(IonPartial<T> partial) => partial.StateOf(Name);

    /// <summary>Writes the map <b>value</b> for this field (the key is written by the formatter).</summary>
    public abstract void WriteValue(CborWriter writer, IonPartial<T> partial);

    /// <summary>Reads a non-null map value and records it as <see cref="PartialState.Modified"/>.</summary>
    public abstract void ReadModified(CborReader reader, IonPartial<T> partial);

    /// <summary>Records this field as <see cref="PartialState.Removed"/>.</summary>
    public abstract void SetRemoved(IonPartial<T> partial);
}

/// <summary>
/// Concrete field descriptor: knows the field's CLR type and its Ion read/write path.
/// </summary>
public class IonPartialFieldSchema<T, TField> : IonPartialFieldSchema<T>
{
    private readonly Func<CborReader, TField> read;
    private readonly Action<CborWriter, TField> write;

    /// <summary>Uses the formatter registered for <typeparamref name="TField"/>.</summary>
    public IonPartialFieldSchema(string name)
        : this(name, IonFormatterStorage<TField>.Read, IonFormatterStorage<TField>.Write)
    {
    }

    public IonPartialFieldSchema(string name, Func<CborReader, TField> read, Action<CborWriter, TField> write)
        : base(name)
    {
        this.read = read ?? throw new ArgumentNullException(nameof(read));
        this.write = write ?? throw new ArgumentNullException(nameof(write));
    }

    public override Type FieldType => typeof(TField);

    public override void WriteValue(CborWriter writer, IonPartial<T> partial)
    {
        if (!partial.TryGetEntry(Name, out var state, out var boxed))
            throw new InvalidOperationException(
                $"Field '{Name}' of IonPartial<{typeof(T).Name}> is not present; the encoder must not write it.");

        // R3: removal is decided by the state, NEVER by null-checking the (boxed) value.
        // For a value-typed field the boxed value of Removed() is default(TField) —
        // e.g. 0f — and null-checking it would silently turn "cleared" into "set to zero".
        if (state == PartialState.Removed)
        {
            writer.WriteNull();
            return;
        }

        if (boxed is not PartialField<TField> typed)
            throw new InvalidOperationException(
                $"Field '{Name}' of IonPartial<{typeof(T).Name}> was set as " +
                $"{boxed?.GetType().Name ?? "null"}, but the registered schema declares " +
                $"PartialField<{typeof(TField).Name}>.");

        var value = typed.Value;

        // R4: Modified(null) is encoded as null, i.e. it is the same patch as Removed().
        if (value is null)
        {
            writer.WriteNull();
            return;
        }

        write(writer, value);
    }

    public override void ReadModified(CborReader reader, IonPartial<T> partial)
        => partial.SetField(Name, PartialField<TField>.Modified(read(reader)));

    public override void SetRemoved(IonPartial<T> partial)
        => partial.SetField(Name, PartialField<TField>.Removed());
}

/// <summary>Descriptor for an <c>X[]</c> field (<see cref="IonArray{TItem}"/>).</summary>
public sealed class IonPartialArrayFieldSchema<T, TItem> : IonPartialFieldSchema<T, IonArray<TItem>>
{
    public IonPartialArrayFieldSchema(string name)
        : base(name, IonFormatterStorage<TItem>.ReadArray, IonFormatterStorage<TItem>.WriteArray)
    {
    }
}

/// <summary>Descriptor for an <c>X?</c> field mapped to <see cref="IonMaybe{TItem}"/>.</summary>
public sealed class IonPartialMaybeFieldSchema<T, TItem> : IonPartialFieldSchema<T, IonMaybe<TItem>>
{
    public IonPartialMaybeFieldSchema(string name)
        : base(name, IonFormatterStorage<TItem>.ReadMaybe, IonFormatterStorage<TItem>.WriteMaybe)
    {
    }
}

/// <summary>Descriptor for an <c>X?</c> field mapped to <see cref="Nullable{TItem}"/>.</summary>
public sealed class IonPartialNullableFieldSchema<T, TItem> : IonPartialFieldSchema<T, TItem?>
    where TItem : struct
{
    public IonPartialNullableFieldSchema(string name)
        : base(name,
            static reader => IonFormatterStorage<TItem>.Read(reader),
            static (writer, value) =>
            {
                if (value is null) writer.WriteNull();
                else IonFormatterStorage<TItem>.Write(writer, value.Value);
            })
    {
    }
}

/// <summary>
/// Per-message registry of <see cref="IonPartial{T}"/> field schemas. This is the
/// contract generated code targets.
/// </summary>
/// <example>
/// <code>
/// IonPartialSchema&lt;Vector&gt;.Register(
///     IonPartialSchema&lt;Vector&gt;.Field&lt;f4&gt;("x"),
///     IonPartialSchema&lt;Vector&gt;.Field&lt;f4&gt;("y"),
///     IonPartialSchema&lt;Vector&gt;.Field&lt;f4&gt;("z"));
/// </code>
/// </example>
public static class IonPartialSchema<T>
{
    private static IonPartialFieldSchema<T>[]? registered;

    /// <summary>True when codegen (or user code) registered an explicit schema.</summary>
    public static bool IsRegistered => registered is not null;

    /// <summary>
    /// Registers the field schema for <c>Partial&lt;T&gt;</c>, in Ion declaration order,
    /// and installs a concrete <see cref="PartialFormatter{T}"/> into
    /// <see cref="IonFormatterStorage"/> so no reflection is needed to resolve it.
    /// Safe to call from a <c>[ModuleInitializer]</c>; the field formatters are
    /// resolved lazily, so registration order versus formatter registration does not matter.
    /// </summary>
    public static void Register(params IonPartialFieldSchema<T>[] fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in fields)
        {
            if (f is null)
                throw new ArgumentException($"Null field descriptor in the schema of Partial<{typeof(T).Name}>.", nameof(fields));
            if (!seen.Add(f.Name))
                throw new ArgumentException($"Duplicate field '{f.Name}' in the schema of Partial<{typeof(T).Name}>.", nameof(fields));
        }

        registered = fields;
        IonFormatterStorage<IonPartial<T>>.Value = new PartialFormatter<T>();
    }

    /// <summary>The registered schema, or a reflection-derived one when nothing was registered.</summary>
    public static IReadOnlyList<IonPartialFieldSchema<T>> Fields => Resolve();

    internal static IonPartialFieldSchema<T>[] Resolve()
        => registered ?? ReflectionPartialSchema.Build<T>();

    // ── factories (codegen-facing) ──────────────────────────────────────────

    /// <summary>Plain field encoded with the formatter registered for <typeparamref name="TField"/>.</summary>
    public static IonPartialFieldSchema<T> Field<TField>(string name)
        => new IonPartialFieldSchema<T, TField>(name);

    /// <summary>Field with an explicit read/write path (escape hatch).</summary>
    public static IonPartialFieldSchema<T> Field<TField>(
        string name, Func<CborReader, TField> read, Action<CborWriter, TField> write)
        => new IonPartialFieldSchema<T, TField>(name, read, write);

    /// <summary><c>TItem[]</c> field.</summary>
    public static IonPartialFieldSchema<T> Array<TItem>(string name)
        => new IonPartialArrayFieldSchema<T, TItem>(name);

    /// <summary><c>TItem?</c> field represented as <see cref="IonMaybe{TItem}"/>.</summary>
    public static IonPartialFieldSchema<T> Maybe<TItem>(string name)
        => new IonPartialMaybeFieldSchema<T, TItem>(name);

    /// <summary><c>TItem?</c> field represented as <see cref="Nullable{TItem}"/>.</summary>
    public static IonPartialFieldSchema<T> NullableValue<TItem>(string name) where TItem : struct
        => new IonPartialNullableFieldSchema<T, TItem>(name);

    /// <summary>
    /// <c>TItem?</c> field represented as a nullable reference. A <c>null</c> value is
    /// encoded as CBOR null, i.e. the same bytes as "cleared".
    /// </summary>
    public static IonPartialFieldSchema<T> NullableRef<TItem>(string name) where TItem : class
        => new IonPartialFieldSchema<T, TItem?>(name);
}

/// <summary>
/// Fallback schema derivation for <c>IonPartial&lt;T&gt;</c> of types that have no
/// generated schema. Reflection-based, therefore AOT/trimming hostile, and field
/// order is only best-effort (metadata order, which normally matches declaration
/// order but is not guaranteed by the CLR).
/// </summary>
[RequiresUnreferencedCode("Derives the Partial<T> field schema from the properties of T. Register a generated schema with IonPartialSchema<T>.Register to stay trim-safe.")]
[RequiresDynamicCode("Constructs closed generic field descriptors at runtime. Register a generated schema with IonPartialSchema<T>.Register to stay AOT-safe.")]
public static class ReflectionPartialSchema
{
    public static IonPartialFieldSchema<T>[] Build<T>()
    {
        var props = typeof(T)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .OrderBy(p => p.MetadataToken)
            .ToArray();

        var result = new List<IonPartialFieldSchema<T>>(props.Length);

        foreach (var prop in props)
        {
            var fieldType = prop.PropertyType;
            Type closed;

            if (fieldType.IsGenericType && fieldType.GetGenericTypeDefinition() == typeof(IonArray<>))
                closed = typeof(IonPartialArrayFieldSchema<,>).MakeGenericType(typeof(T), fieldType.GetGenericArguments()[0]);
            else if (fieldType.IsGenericType && fieldType.GetGenericTypeDefinition() == typeof(IonMaybe<>))
                closed = typeof(IonPartialMaybeFieldSchema<,>).MakeGenericType(typeof(T), fieldType.GetGenericArguments()[0]);
            else if (Nullable.GetUnderlyingType(fieldType) is { } underlying)
                closed = typeof(IonPartialNullableFieldSchema<,>).MakeGenericType(typeof(T), underlying);
            else
                closed = typeof(IonPartialFieldSchema<,>).MakeGenericType(typeof(T), fieldType);

            result.Add((IonPartialFieldSchema<T>)Activator.CreateInstance(closed, prop.Name)!);
        }

        return result.ToArray();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Formatter
// ─────────────────────────────────────────────────────────────────────────────

public sealed class PartialFormatter<T> : IonFormatter<IonPartial<T>>
{
    private IonPartialFieldSchema<T>[]? schema;
    private Dictionary<string, IonPartialFieldSchema<T>>? byName;

    private IonPartialFieldSchema<T>[] Schema
    {
        get
        {
            if (schema is null)
            {
                var resolved = IonPartialSchema<T>.Resolve();
                byName = resolved.ToDictionary(f => f.Name, StringComparer.Ordinal);
                schema = resolved;
            }

            return schema;
        }
    }

    public IonPartial<T> Read(CborReader reader)
    {
        _ = Schema;
        var partial = new IonPartial<T>();

        // R5: ReadStartMap() returns null for an indefinite-length map. Drive the loop
        // off the reader state instead of the count so both forms work.
        reader.ReadStartMap();

        while (true)
        {
            var state = reader.PeekState();
            if (state == CborReaderState.EndMap)
                break;
            if (state is CborReaderState.Finished or CborReaderState.EndArray)
                throw new CborContentException($"Unexpected {state} inside a Partial<{typeof(T).Name}> map");

            var name = reader.ReadTextString();

            if (!byName!.TryGetValue(name, out var field))
            {
                reader.SkipValue();
                continue;
            }

            if (reader.PeekState() == CborReaderState.Null)
            {
                reader.ReadNull();
                field.SetRemoved(partial);
            }
            else
            {
                field.ReadModified(reader, partial);
            }
        }

        reader.ReadEndMap();
        return partial;
    }

    public void Write(CborWriter writer, IonPartial<T> value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var fields = Schema;

        // Schema order — deterministic and identical across runtimes (R6).
        var present = new List<IonPartialFieldSchema<T>>(value.Count);
        foreach (var field in fields)
            if (field.GetState(value) != PartialState.None)
                present.Add(field);

        if (present.Count != value.Count)
        {
            var known = new HashSet<string>(fields.Select(f => f.Name), StringComparer.Ordinal);
            var unknown = value.PresentFields().Where(n => !known.Contains(n)).ToArray();
            throw new InvalidOperationException(
                $"IonPartial<{typeof(T).Name}> has {value.Count} touched field(s) but only {present.Count} " +
                $"are covered by the registered schema. Unknown field(s): " +
                $"{(unknown.Length == 0 ? "<none>" : string.Join(", ", unknown))}.");
        }

        writer.WriteStartMap(present.Count);

        foreach (var field in present)
        {
            writer.WriteTextString(field.Name);
            field.WriteValue(writer, value);
        }

        writer.WriteEndMap();
    }
}
