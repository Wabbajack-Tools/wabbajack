using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Wabbajack.Common;

public static class IEnumerableExtensions
{
    public static void Do<T>(this IEnumerable<T> coll, Action<T> f)
    {
        foreach (var i in coll) f(i);
    }


    public static IEnumerable<TOut> TryKeep<TIn, TOut>(this IEnumerable<TIn> coll, Func<TIn, (bool, TOut)> fn)
    {
        return coll.Select(fn).Where(p => p.Item1).Select(p => p.Item2);
    }

    public static IEnumerable<T> Shuffle<T>(this IEnumerable<T> coll)
    {
        var rnd = new Random();
        var data = coll.ToArray();
        for (var x = 0; x < data.Length; x++)
        {
            var a = rnd.Next(0, data.Length);
            var b = rnd.Next(0, data.Length);

            (data[b], data[a]) = (data[a], data[b]);
        }

        return data;
    }

    public static IEnumerable<T> OnEach<T>(this IEnumerable<T> coll, Action<T> fn)
    {
        foreach (var itm in coll)
        {
            fn(itm);
            yield return itm;
        }
    }

    public static async IAsyncEnumerable<T> OnEach<T>(this IEnumerable<T> coll, Func<T, Task> fn)
    {
        foreach (var itm in coll)
        {
            await fn(itm);
            yield return itm;
        }
    }
}