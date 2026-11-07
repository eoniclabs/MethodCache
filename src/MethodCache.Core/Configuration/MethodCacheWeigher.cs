using System;

namespace MethodCache
{
    /// <summary>
    /// A ready-made <see cref="MethodCacheMemoryOptions.Weigher"/> that weighs entries in approximate bytes:
    /// <c>o.Weigher = MethodCacheWeigher.Bytes;</c>.
    /// </summary>
    public static class MethodCacheWeigher
    {
        /// <summary>The weight <see cref="Bytes"/> gives a value it can't measure, such as an object of your own: 64.</summary>
        public const long OtherValue = 64;

        /// <summary>
        /// The approximate size of an entry in bytes: two per character of its key, plus the size of its value. A string
        /// weighs two bytes per character, and an array of numbers, <c>bool</c> or <c>char</c> its length times the size
        /// of an element, such as four bytes per <c>float</c> of a <c>float[]</c> embedding. <c>null</c> weighs nothing,
        /// and any other value <see cref="OtherValue"/>. To weigh your own types, handle them and call this for the rest:
        /// <c>(key, value) =&gt; value is Report r ? r.Rows.Count * 200L : MethodCacheWeigher.Bytes(key, value)</c>.
        /// </summary>
        /// <param name="key">The entry's stored key.</param>
        /// <param name="value">The cached value.</param>
        /// <returns>The entry's weight, never negative.</returns>
        public static long Bytes(string key, object? value)
        {
            var keyBytes = (key?.Length ?? 0) * 2L;
            return keyBytes + value switch
            {
                null => 0,
                string text => text.Length * 2L,
                Array array when ElementSize(array.GetType().GetElementType()) is > 0 and var size => array.LongLength * size,
                _ => OtherValue,
            };
        }

        // The size of an element of an array of numbers, bool or char; 0 for any other element type
        private static long ElementSize(Type? element)
        {
            if (element is not { IsPrimitive: true }) return 0;
            if (element == typeof(IntPtr) || element == typeof(UIntPtr)) return IntPtr.Size;
            return Type.GetTypeCode(element) switch
            {
                TypeCode.Boolean or TypeCode.Byte or TypeCode.SByte => 1,
                TypeCode.Char or TypeCode.Int16 or TypeCode.UInt16 => 2,
                TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Single => 4,
                TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Double => 8,
                _ => 0,
            };
        }
    }
}
