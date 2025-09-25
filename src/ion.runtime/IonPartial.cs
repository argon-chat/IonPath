namespace ion.runtime;

using System.Linq.Expressions;
using System.Reflection;

public class IonPartial<T>
{
    private readonly Dictionary<string, object?> fields = new();

    public void SetField<TField>(Expression<Func<T, TField>> selector, PartialField<TField> value)
    {
        var name = GetMemberName(selector);
        fields[name] = value;
    }

    public PartialField<TField> GetField<TField>(Expression<Func<T, TField>> selector)
    {
        var name = GetMemberName(selector);
        if (fields.TryGetValue(name, out var v) && v is PartialField<TField> f)
            return f;

        return PartialField<TField>.None;
    }

    private static string GetMemberName<TField>(Expression<Func<T, TField>> selector) =>
        selector.Body switch
        {
            MemberExpression m => m.Member.Name,
            UnaryExpression { Operand: MemberExpression mm } => mm.Member.Name,
            _ => throw new ArgumentException("Selector must be a property access", nameof(selector))
        };

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

public class PartialFormatter<T> : IonFormatter<IonPartial<T>>
{
    private static readonly Dictionary<string, Action<IonPartial<T>, object?>> setters = new();
    private static readonly Dictionary<string, Func<IonPartial<T>, (bool HasValue, object? Value)>> getters = new();
    private static readonly Dictionary<string, Type> fieldTypes = new();

    static PartialFormatter()
    {
        foreach (var prop in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var fieldType = prop.PropertyType;
            fieldTypes[prop.Name] = fieldType;

            var param = Expression.Parameter(typeof(T), "x");
            var body = Expression.Property(param, prop);
            var delegateType = typeof(Func<,>).MakeGenericType(typeof(T), fieldType);
            var lambda = Expression.Lambda(delegateType, body, param);

            var setFieldMethod = typeof(IonPartial<T>)
                .GetMethod(nameof(IonPartial<T>.SetField))!
                .MakeGenericMethod(fieldType);

            var ionPartialParam = Expression.Parameter(typeof(IonPartial<T>), "partial");
            var valueParam = Expression.Parameter(typeof(object), "val");

            var convertedVal = Expression.Convert(valueParam, typeof(PartialField<>).MakeGenericType(fieldType));
            var call = Expression.Call(ionPartialParam, setFieldMethod, Expression.Constant(lambda), convertedVal);

            var setterLambda = Expression.Lambda<Action<IonPartial<T>, object?>>(
                call, ionPartialParam, valueParam);

            setters[prop.Name] = setterLambda.Compile();

            var getFieldMethod = typeof(IonPartial<T>)
                .GetMethod(nameof(IonPartial<T>.GetField))!
                .MakeGenericMethod(fieldType);

            var getCall = Expression.Call(ionPartialParam, getFieldMethod, Expression.Constant(lambda));

            var stateProp = Expression.Property(getCall, nameof(PartialField<int>.State));
            var valueProp = Expression.Property(getCall, nameof(PartialField<int>.Value));

            var tupleCtor = typeof(ValueTuple<,>).MakeGenericType(typeof(bool), typeof(object))
                .GetConstructor([typeof(bool), typeof(object)])!;

            var hasValueExpr = Expression.NotEqual(stateProp, Expression.Constant(PartialState.None));

            var boxedValue = Expression.Convert(valueProp, typeof(object));
            var newTuple = Expression.New(tupleCtor, hasValueExpr, boxedValue);

            var getterLambda = Expression.Lambda<Func<IonPartial<T>, (bool, object?)>>(newTuple, ionPartialParam);

            getters[prop.Name] = getterLambda.Compile();
        }
    }

    public IonPartial<T> Read(CborReader reader)
    {
        var partial = new IonPartial<T>();
        var length = reader.ReadStartMap();

        for (var i = 0; i < length.GetValueOrDefault(); i++)
        {
            var propName = reader.ReadTextString();
            if (!setters.TryGetValue(propName, out var setter))
            {
                reader.SkipValue();
                continue;
            }

            var fieldType = fieldTypes[propName];

            object pfObj;
            if (reader.PeekState() == CborReaderState.Null)
            {
                reader.ReadNull();
                var pfType = typeof(PartialField<>).MakeGenericType(fieldType);
                var removed = pfType.GetMethod(nameof(PartialField<object>.Removed))!;
                pfObj = removed.Invoke(null, null)!;
            }
            else
            {
                var fmtMethod = typeof(IonFormatterStorage)
                    .GetMethod(nameof(IonFormatterStorage.GetFormatter))!
                    .MakeGenericMethod(fieldType);
                dynamic formatter = fmtMethod.Invoke(null, null)!;
                var val = formatter.Read(reader);

                var pfType = typeof(PartialField<>).MakeGenericType(fieldType);
                var modified = pfType.GetMethod(nameof(PartialField<object>.Modified))!;
                pfObj = modified.Invoke(null, [val])!;
            }

            setter(partial, pfObj);
        }

        reader.ReadEndMap();
        return partial;
    }

    public void Write(CborWriter writer, IonPartial<T> value)
    {
        var present = value.PresentFields().ToList();
        writer.WriteStartMap(present.Count);

        foreach (var name in present)
        {
            writer.WriteTextString(name);

            var getter = getters[name];
            var (hasValue, fieldVal) = getter(value);

            if (!hasValue)
            {
                writer.WriteNull();
                continue;
            }

            if (fieldVal is null)
            {
                writer.WriteNull();
            }
            else
            {
                var fieldType = fieldTypes[name];
                var fmtMethod = typeof(IonFormatterStorage)
                    .GetMethod(nameof(IonFormatterStorage.GetFormatter))!
                    .MakeGenericMethod(fieldType);

                dynamic formatter = fmtMethod.Invoke(null, null)!;
                formatter.Write(writer, (dynamic)fieldVal);
            }
        }

        writer.WriteEndMap();
    }
}