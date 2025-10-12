namespace MethodCache.Core.Runtime.Core
{
    /// <summary>
    /// Extensions for CacheContext to support enhanced conditional logic.
    /// </summary>
    public static class CacheContextExtensions
    {
        private const string ArgumentsKey = "_cache_arguments";

        /// <summary>
        /// Gets a typed argument from the cache context by index.
        /// </summary>
        /// <typeparam name="T">The type of the argument</typeparam>
        /// <param name="context">The cache context</param>
        /// <param name="index">The zero-based index of the argument</param>
        /// <returns>The argument value cast to the specified type</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when index is out of range</exception>
        /// <exception cref="InvalidCastException">Thrown when argument cannot be cast to specified type</exception>
        public static T GetArg<T>(this CacheContext context, int index)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            if (!context.Items.TryGetValue(ArgumentsKey, out var argsObj) || argsObj is not object[] args)
            {
                throw new InvalidOperationException("No arguments available in cache context. This method can only be used with method chaining API.");
            }

            if (index < 0 || index >= args.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index), $"Argument index {index} is out of range. Available arguments: {args.Length}");
            }

            var arg = args[index];
            if (arg is T typed)
            {
                return typed;
            }

            // Try to convert
            try
            {
                return (T)Convert.ChangeType(arg, typeof(T));
            }
            catch (Exception ex)
            {
                throw new InvalidCastException($"Cannot cast argument at index {index} of type {arg?.GetType()?.Name ?? "null"} to {typeof(T).Name}", ex);
            }
        }

        /// <summary>
        /// Gets all arguments from the cache context.
        /// </summary>
        /// <param name="context">The cache context</param>
        /// <returns>Array of all arguments</returns>
        public static object[] GetArgs(this CacheContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            if (!context.Items.TryGetValue(ArgumentsKey, out var argsObj) || argsObj is not object[] args)
            {
                return Array.Empty<object>();
            }

            return args;
        }

        /// <summary>
        /// Gets the count of arguments in the cache context.
        /// </summary>
        /// <param name="context">The cache context</param>
        /// <returns>The number of arguments</returns>
        public static int GetArgCount(this CacheContext context)
        {
            return GetArgs(context).Length;
        }

        /// <summary>
        /// Gets the type of an argument at the specified index.
        /// </summary>
        /// <param name="context">The cache context</param>
        /// <param name="index">The zero-based index of the argument</param>
        /// <returns>The type of the argument, or null if the argument is null</returns>
        public static Type? GetArgType(this CacheContext context, int index)
        {
            var args = GetArgs(context);
            if (index < 0 || index >= args.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index), $"Argument index {index} is out of range. Available arguments: {args.Length}");
            }

            return args[index]?.GetType();
        }

        /// <summary>
        /// Internal method to set arguments in the cache context.
        /// Used by the method chaining API to store factory arguments.
        /// </summary>
        /// <param name="context">The cache context</param>
        /// <param name="arguments">The arguments to store</param>
        internal static void SetArguments(this CacheContext context, object[] arguments)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            context.Items[ArgumentsKey] = arguments ?? Array.Empty<object>();
        }
    }
}