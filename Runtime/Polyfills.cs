#if NET48
using System;
using System.Collections.Generic;
using System.Text;

namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}

namespace RhinoModifiers.Runtime
{
    internal static class Polyfills
    {
        public static int Clamp(this int value, int minimum, int maximum)
        {
            return value < minimum ? minimum
                : value > maximum ? maximum
                : value;
        }

        public static double Clamp(this double value, double minimum, double maximum)
        {
            return value < minimum ? minimum
                : value > maximum ? maximum
                : value;
        }

        public static string Replace(
            this string value,
            string oldValue,
            string newValue,
            StringComparison comparisonType
        )
        {
            if (value is null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            if (oldValue is null)
            {
                throw new ArgumentNullException(nameof(oldValue));
            }

            if (oldValue.Length == 0)
            {
                throw new ArgumentException(
                    "The string to be replaced cannot be empty.",
                    nameof(oldValue)
                );
            }

            newValue ??= string.Empty;

            var builder = new StringBuilder(value.Length);
            var currentIndex = 0;

            while (true)
            {
                var matchIndex = value.IndexOf(oldValue, currentIndex, comparisonType);
                if (matchIndex < 0)
                {
                    builder.Append(value, currentIndex, value.Length - currentIndex);
                    return builder.ToString();
                }

                builder.Append(value, currentIndex, matchIndex - currentIndex);
                builder.Append(newValue);
                currentIndex = matchIndex + oldValue.Length;
            }
        }

        public static bool Remove<TKey, TValue>(
            this IDictionary<TKey, TValue> dictionary,
            TKey key,
            out TValue value
        )
        {
            if (dictionary is null)
            {
                throw new ArgumentNullException(nameof(dictionary));
            }

            if (dictionary.TryGetValue(key, out value))
            {
                dictionary.Remove(key);
                return true;
            }

            value = default!;
            return false;
        }
    }
}
#endif
