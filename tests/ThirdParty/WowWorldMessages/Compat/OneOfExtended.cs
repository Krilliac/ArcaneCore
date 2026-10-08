namespace OneOf;

public readonly struct OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>
{
    private readonly int _index;
    public object Value { get; }
    private OneOf(object value, int index) { Value = value; _index = index; }
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(T0 value) => new(value!, 0);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(T1 value) => new(value!, 1);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(T2 value) => new(value!, 2);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(T3 value) => new(value!, 3);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(T4 value) => new(value!, 4);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(T5 value) => new(value!, 5);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(T6 value) => new(value!, 6);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(T7 value) => new(value!, 7);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(T8 value) => new(value!, 8);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(T9 value) => new(value!, 9);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(T10 value) => new(value!, 10);
    public TResult Match<TResult>(Func<T0, TResult> f0, Func<T1, TResult> f1, Func<T2, TResult> f2, Func<T3, TResult> f3, Func<T4, TResult> f4, Func<T5, TResult> f5, Func<T6, TResult> f6, Func<T7, TResult> f7, Func<T8, TResult> f8, Func<T9, TResult> f9, Func<T10, TResult> f10) => _index switch
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
        10 => f10((T10)Value),
        _ => throw new InvalidOperationException("Uninitialized variant")
    };
}

public readonly struct OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>
{
    private readonly int _index;
    public object Value { get; }
    private OneOf(object value, int index) { Value = value; _index = index; }
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>(T0 value) => new(value!, 0);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>(T1 value) => new(value!, 1);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>(T2 value) => new(value!, 2);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>(T3 value) => new(value!, 3);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>(T4 value) => new(value!, 4);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>(T5 value) => new(value!, 5);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>(T6 value) => new(value!, 6);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>(T7 value) => new(value!, 7);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>(T8 value) => new(value!, 8);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>(T9 value) => new(value!, 9);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>(T10 value) => new(value!, 10);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>(T11 value) => new(value!, 11);
    public TResult Match<TResult>(Func<T0, TResult> f0, Func<T1, TResult> f1, Func<T2, TResult> f2, Func<T3, TResult> f3, Func<T4, TResult> f4, Func<T5, TResult> f5, Func<T6, TResult> f6, Func<T7, TResult> f7, Func<T8, TResult> f8, Func<T9, TResult> f9, Func<T10, TResult> f10, Func<T11, TResult> f11) => _index switch
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
        10 => f10((T10)Value),
        11 => f11((T11)Value),
        _ => throw new InvalidOperationException("Uninitialized variant")
    };
}

public readonly struct OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>
{
    private readonly int _index;
    public object Value { get; }
    private OneOf(object value, int index) { Value = value; _index = index; }
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>(T0 value) => new(value!, 0);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>(T1 value) => new(value!, 1);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>(T2 value) => new(value!, 2);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>(T3 value) => new(value!, 3);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>(T4 value) => new(value!, 4);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>(T5 value) => new(value!, 5);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>(T6 value) => new(value!, 6);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>(T7 value) => new(value!, 7);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>(T8 value) => new(value!, 8);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>(T9 value) => new(value!, 9);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>(T10 value) => new(value!, 10);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>(T11 value) => new(value!, 11);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>(T12 value) => new(value!, 12);
    public TResult Match<TResult>(Func<T0, TResult> f0, Func<T1, TResult> f1, Func<T2, TResult> f2, Func<T3, TResult> f3, Func<T4, TResult> f4, Func<T5, TResult> f5, Func<T6, TResult> f6, Func<T7, TResult> f7, Func<T8, TResult> f8, Func<T9, TResult> f9, Func<T10, TResult> f10, Func<T11, TResult> f11, Func<T12, TResult> f12) => _index switch
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
        10 => f10((T10)Value),
        11 => f11((T11)Value),
        12 => f12((T12)Value),
        _ => throw new InvalidOperationException("Uninitialized variant")
    };
}

public readonly struct OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13>
{
    private readonly int _index;
    public object Value { get; }
    private OneOf(object value, int index) { Value = value; _index = index; }
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13>(T0 value) => new(value!, 0);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13>(T1 value) => new(value!, 1);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13>(T2 value) => new(value!, 2);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13>(T3 value) => new(value!, 3);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13>(T4 value) => new(value!, 4);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13>(T5 value) => new(value!, 5);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13>(T6 value) => new(value!, 6);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13>(T7 value) => new(value!, 7);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13>(T8 value) => new(value!, 8);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13>(T9 value) => new(value!, 9);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13>(T10 value) => new(value!, 10);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13>(T11 value) => new(value!, 11);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13>(T12 value) => new(value!, 12);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13>(T13 value) => new(value!, 13);
    public TResult Match<TResult>(Func<T0, TResult> f0, Func<T1, TResult> f1, Func<T2, TResult> f2, Func<T3, TResult> f3, Func<T4, TResult> f4, Func<T5, TResult> f5, Func<T6, TResult> f6, Func<T7, TResult> f7, Func<T8, TResult> f8, Func<T9, TResult> f9, Func<T10, TResult> f10, Func<T11, TResult> f11, Func<T12, TResult> f12, Func<T13, TResult> f13) => _index switch
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
        10 => f10((T10)Value),
        11 => f11((T11)Value),
        12 => f12((T12)Value),
        13 => f13((T13)Value),
        _ => throw new InvalidOperationException("Uninitialized variant")
    };
}

public readonly struct OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>
{
    private readonly int _index;
    public object Value { get; }
    private OneOf(object value, int index) { Value = value; _index = index; }
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>(T0 value) => new(value!, 0);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>(T1 value) => new(value!, 1);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>(T2 value) => new(value!, 2);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>(T3 value) => new(value!, 3);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>(T4 value) => new(value!, 4);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>(T5 value) => new(value!, 5);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>(T6 value) => new(value!, 6);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>(T7 value) => new(value!, 7);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>(T8 value) => new(value!, 8);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>(T9 value) => new(value!, 9);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>(T10 value) => new(value!, 10);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>(T11 value) => new(value!, 11);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>(T12 value) => new(value!, 12);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>(T13 value) => new(value!, 13);
    public static implicit operator OneOf<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>(T14 value) => new(value!, 14);
    public TResult Match<TResult>(Func<T0, TResult> f0, Func<T1, TResult> f1, Func<T2, TResult> f2, Func<T3, TResult> f3, Func<T4, TResult> f4, Func<T5, TResult> f5, Func<T6, TResult> f6, Func<T7, TResult> f7, Func<T8, TResult> f8, Func<T9, TResult> f9, Func<T10, TResult> f10, Func<T11, TResult> f11, Func<T12, TResult> f12, Func<T13, TResult> f13, Func<T14, TResult> f14) => _index switch
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
        10 => f10((T10)Value),
        11 => f11((T11)Value),
        12 => f12((T12)Value),
        13 => f13((T13)Value),
        14 => f14((T14)Value),
        _ => throw new InvalidOperationException("Uninitialized variant")
    };
}
