// Test-only subset of OneOf used by the generated vanilla message source.
namespace OneOf;

public readonly struct OneOf<T0, T1>
{
    private readonly int _index;
    public object Value { get; }
    private OneOf(object value, int index) { Value = value; _index = index; }
    public static implicit operator OneOf<T0, T1>(T0 value) => new(value!, 0);
    public static implicit operator OneOf<T0, T1>(T1 value) => new(value!, 1);
    public TResult Match<TResult>(Func<T0, TResult> f0, Func<T1, TResult> f1) => _index switch
    {
        0 => f0((T0)Value),
        1 => f1((T1)Value),
        _ => throw new InvalidOperationException("Uninitialized variant")
    };
}

public readonly struct OneOf<T0, T1, T2>
{
    private readonly int _index;
    public object Value { get; }
    private OneOf(object value, int index) { Value = value; _index = index; }
    public static implicit operator OneOf<T0, T1, T2>(T0 value) => new(value!, 0);
    public static implicit operator OneOf<T0, T1, T2>(T1 value) => new(value!, 1);
    public static implicit operator OneOf<T0, T1, T2>(T2 value) => new(value!, 2);
    public TResult Match<TResult>(Func<T0, TResult> f0, Func<T1, TResult> f1, Func<T2, TResult> f2) => _index switch
    {
        0 => f0((T0)Value),
        1 => f1((T1)Value),
        2 => f2((T2)Value),
        _ => throw new InvalidOperationException("Uninitialized variant")
    };
}

public readonly struct OneOf<T0, T1, T2, T3>
{
    private readonly int _index;
    public object Value { get; }
    private OneOf(object value, int index) { Value = value; _index = index; }
    public static implicit operator OneOf<T0, T1, T2, T3>(T0 value) => new(value!, 0);
    public static implicit operator OneOf<T0, T1, T2, T3>(T1 value) => new(value!, 1);
    public static implicit operator OneOf<T0, T1, T2, T3>(T2 value) => new(value!, 2);
    public static implicit operator OneOf<T0, T1, T2, T3>(T3 value) => new(value!, 3);
    public TResult Match<TResult>(Func<T0, TResult> f0, Func<T1, TResult> f1, Func<T2, TResult> f2, Func<T3, TResult> f3) => _index switch
    {
        0 => f0((T0)Value),
        1 => f1((T1)Value),
        2 => f2((T2)Value),
        3 => f3((T3)Value),
        _ => throw new InvalidOperationException("Uninitialized variant")
    };
}

public readonly struct OneOf<T0, T1, T2, T3, T4>
{
    private readonly int _index;
    public object Value { get; }
    private OneOf(object value, int index) { Value = value; _index = index; }
    public static implicit operator OneOf<T0, T1, T2, T3, T4>(T0 value) => new(value!, 0);
    public static implicit operator OneOf<T0, T1, T2, T3, T4>(T1 value) => new(value!, 1);
    public static implicit operator OneOf<T0, T1, T2, T3, T4>(T2 value) => new(value!, 2);
    public static implicit operator OneOf<T0, T1, T2, T3, T4>(T3 value) => new(value!, 3);
    public static implicit operator OneOf<T0, T1, T2, T3, T4>(T4 value) => new(value!, 4);
    public TResult Match<TResult>(Func<T0, TResult> f0, Func<T1, TResult> f1, Func<T2, TResult> f2, Func<T3, TResult> f3, Func<T4, TResult> f4) => _index switch
    {
        0 => f0((T0)Value),
        1 => f1((T1)Value),
        2 => f2((T2)Value),
        3 => f3((T3)Value),
        4 => f4((T4)Value),
        _ => throw new InvalidOperationException("Uninitialized variant")
    };
}

public readonly struct OneOf<T0, T1, T2, T3, T4, T5>
{
    private readonly int _index;
    public object Value { get; }
    private OneOf(object value, int index) { Value = value; _index = index; }
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5>(T0 value) => new(value!, 0);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5>(T1 value) => new(value!, 1);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5>(T2 value) => new(value!, 2);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5>(T3 value) => new(value!, 3);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5>(T4 value) => new(value!, 4);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5>(T5 value) => new(value!, 5);
    public TResult Match<TResult>(Func<T0, TResult> f0, Func<T1, TResult> f1, Func<T2, TResult> f2, Func<T3, TResult> f3, Func<T4, TResult> f4, Func<T5, TResult> f5) => _index switch
    {
        0 => f0((T0)Value),
        1 => f1((T1)Value),
        2 => f2((T2)Value),
        3 => f3((T3)Value),
        4 => f4((T4)Value),
        5 => f5((T5)Value),
        _ => throw new InvalidOperationException("Uninitialized variant")
    };
}

public readonly struct OneOf<T0, T1, T2, T3, T4, T5, T6>
{
    private readonly int _index;
    public object Value { get; }
    private OneOf(object value, int index) { Value = value; _index = index; }
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6>(T0 value) => new(value!, 0);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6>(T1 value) => new(value!, 1);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6>(T2 value) => new(value!, 2);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6>(T3 value) => new(value!, 3);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6>(T4 value) => new(value!, 4);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6>(T5 value) => new(value!, 5);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6>(T6 value) => new(value!, 6);
    public TResult Match<TResult>(Func<T0, TResult> f0, Func<T1, TResult> f1, Func<T2, TResult> f2, Func<T3, TResult> f3, Func<T4, TResult> f4, Func<T5, TResult> f5, Func<T6, TResult> f6) => _index switch
    {
        0 => f0((T0)Value),
        1 => f1((T1)Value),
        2 => f2((T2)Value),
        3 => f3((T3)Value),
        4 => f4((T4)Value),
        5 => f5((T5)Value),
        6 => f6((T6)Value),
        _ => throw new InvalidOperationException("Uninitialized variant")
    };
}

public readonly struct OneOf<T0, T1, T2, T3, T4, T5, T6, T7>
{
    private readonly int _index;
    public object Value { get; }
    private OneOf(object value, int index) { Value = value; _index = index; }
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7>(T0 value) => new(value!, 0);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7>(T1 value) => new(value!, 1);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7>(T2 value) => new(value!, 2);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7>(T3 value) => new(value!, 3);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7>(T4 value) => new(value!, 4);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7>(T5 value) => new(value!, 5);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7>(T6 value) => new(value!, 6);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7>(T7 value) => new(value!, 7);
    public TResult Match<TResult>(Func<T0, TResult> f0, Func<T1, TResult> f1, Func<T2, TResult> f2, Func<T3, TResult> f3, Func<T4, TResult> f4, Func<T5, TResult> f5, Func<T6, TResult> f6, Func<T7, TResult> f7) => _index switch
    {
        0 => f0((T0)Value),
        1 => f1((T1)Value),
        2 => f2((T2)Value),
        3 => f3((T3)Value),
        4 => f4((T4)Value),
        5 => f5((T5)Value),
        6 => f6((T6)Value),
        7 => f7((T7)Value),
        _ => throw new InvalidOperationException("Uninitialized variant")
    };
}

public readonly struct OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8>
{
    private readonly int _index;
    public object Value { get; }
    private OneOf(object value, int index) { Value = value; _index = index; }
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8>(T0 value) => new(value!, 0);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8>(T1 value) => new(value!, 1);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8>(T2 value) => new(value!, 2);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8>(T3 value) => new(value!, 3);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8>(T4 value) => new(value!, 4);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8>(T5 value) => new(value!, 5);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8>(T6 value) => new(value!, 6);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8>(T7 value) => new(value!, 7);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8>(T8 value) => new(value!, 8);
    public TResult Match<TResult>(Func<T0, TResult> f0, Func<T1, TResult> f1, Func<T2, TResult> f2, Func<T3, TResult> f3, Func<T4, TResult> f4, Func<T5, TResult> f5, Func<T6, TResult> f6, Func<T7, TResult> f7, Func<T8, TResult> f8) => _index switch
    {
        0 => f0((T0)Value),
        1 => f1((T1)Value),
        2 => f2((T2)Value),
        3 => f3((T3)Value),
        4 => f4((T4)Value),
        5 => f5((T5)Value),
        6 => f6((T6)Value),
        7 => f7((T7)Value),
        8 => f8((T8)Value),
        _ => throw new InvalidOperationException("Uninitialized variant")
    };
}

public readonly struct OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9>
{
    private readonly int _index;
    public object Value { get; }
    private OneOf(object value, int index) { Value = value; _index = index; }
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9>(T0 value) => new(value!, 0);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9>(T1 value) => new(value!, 1);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9>(T2 value) => new(value!, 2);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9>(T3 value) => new(value!, 3);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9>(T4 value) => new(value!, 4);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9>(T5 value) => new(value!, 5);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9>(T6 value) => new(value!, 6);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9>(T7 value) => new(value!, 7);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9>(T8 value) => new(value!, 8);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9>(T9 value) => new(value!, 9);
    public TResult Match<TResult>(Func<T0, TResult> f0, Func<T1, TResult> f1, Func<T2, TResult> f2, Func<T3, TResult> f3, Func<T4, TResult> f4, Func<T5, TResult> f5, Func<T6, TResult> f6, Func<T7, TResult> f7, Func<T8, TResult> f8, Func<T9, TResult> f9) => _index switch
    {
        0 => f0((T0)Value),
        1 => f1((T1)Value),
        2 => f2((T2)Value),
        3 => f3((T3)Value),
        4 => f4((T4)Value),
        5 => f5((T5)Value),
        6 => f6((T6)Value),
        7 => f7((T7)Value),
        8 => f8((T8)Value),
        9 => f9((T9)Value),
        _ => throw new InvalidOperationException("Uninitialized variant")
    };
}
