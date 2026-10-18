using System.ComponentModel;
using System.Globalization;
using System.Text;

namespace MethodCache.Infrastructure
{
    /// <summary>
    /// Expands parameter templates such as <c>"user:{userId}"</c> in a policy's tags, so a cached entry carries
    /// the same tag that <see cref="CacheInvalidateAttribute"/> builds from its arguments.
    /// </summary>
    /// <remarks>Called by generated decorators on a cache miss; not intended for application code.</remarks>
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class CacheTagTemplates
    {
        /// <summary>
        /// Returns <paramref name="policy"/> with every <c>{name}</c> in its tags replaced by the matching value.
        /// Parameter names match exactly first, then case-insensitively when only one name matches.
        /// Unknown or ambiguous placeholders are left as they are.
        /// </summary>
        /// <param name="policy">The method's policy.</param>
        /// <param name="parameterNames">The method's parameter names.</param>
        /// <param name="values">The formatted argument values, in the same order as <paramref name="parameterNames"/>.</param>
        /// <returns>The same policy when no tag contains a placeholder; otherwise a copy with expanded tags.</returns>
        public static CacheRuntimePolicy Expand(CacheRuntimePolicy policy, string[] parameterNames, string[] values)
        {
            ArgumentNullException.ThrowIfNull(policy);
            ArgumentNullException.ThrowIfNull(parameterNames);
            ArgumentNullException.ThrowIfNull(values);

            if (parameterNames.Length != values.Length)
            {
                throw new ArgumentException($"Every parameter name needs exactly one value ({parameterNames.Length} names, {values.Length} values).", nameof(values));
            }

            if (!policy.Tags.Any(tag => tag.Contains('{')))
            {
                return policy;
            }

            var tags = new List<string>(policy.Tags.Count);
            foreach (var tag in policy.Tags)
            {
                tags.Add(tag.Contains('{') ? ExpandTag(tag, parameterNames, index => values[index]) : tag);
            }

            return policy with { Tags = tags };
        }

        /// <summary>Expands tag templates, formatting only the argument values their placeholders actually reference.</summary>
        /// <param name="policy">The policy whose tags to expand.</param>
        /// <param name="parameterNames">The method's parameter names.</param>
        /// <param name="values">Argument values in the same order as the names.</param>
        /// <returns>The original policy for static tags, or a copy with the expanded tags.</returns>
        public static CacheRuntimePolicy Expand(CacheRuntimePolicy policy, string[] parameterNames, object?[] values)
        {
            ArgumentNullException.ThrowIfNull(policy);
            ArgumentNullException.ThrowIfNull(parameterNames);
            ArgumentNullException.ThrowIfNull(values);
            if (parameterNames.Length != values.Length)
                throw new ArgumentException($"Every parameter name needs exactly one value ({parameterNames.Length} names, {values.Length} values).", nameof(values));
            if (!policy.Tags.Any(tag => tag.Contains('{'))) return policy;

            var formatted = new string?[values.Length];
            string Format(int index) => formatted[index] ??= (!policy.CanonicalTagParts
                ? Convert.ToString(values[index], CultureInfo.InvariantCulture) ?? "null"
                : CanonicalTagPart.Format(values[index]));
            return policy with { Tags = policy.Tags.Select(tag => tag.Contains('{') ? ExpandTag(tag, parameterNames, Format) : tag).ToArray() };
        }

        /// <summary>
        /// Replaces the placeholders in one left-to-right pass, as the <c>string.Format</c> that
        /// <see cref="CacheInvalidateAttribute"/> uses does, so an argument whose text looks like a placeholder
        /// (such as <c>"{orgId}"</c>) is kept as it is and the entry gets the tag the invalidation removes.
        /// </summary>
        private static string ExpandTag(string tag, string[] parameterNames, Func<int, string> value)
        {
            var result = new StringBuilder(tag.Length + 16);
            var position = 0;
            while (position < tag.Length)
            {
                var open = tag.IndexOf('{', position);
                var close = open < 0 ? -1 : tag.IndexOf('}', open + 1);
                if (close < 0)
                {
                    break;
                }

                var index = IndexOf(parameterNames, tag.AsSpan(open + 1, close - open - 1));
                if (index < 0)
                {
                    // Not a parameter: keep the brace and look for a placeholder after it
                    result.Append(tag, position, open + 1 - position);
                    position = open + 1;
                    continue;
                }

                result.Append(tag, position, open - position).Append(value(index));
                position = close + 1;
            }

            return result.Append(tag, position, tag.Length - position).ToString();
        }

        private static int IndexOf(string[] parameterNames, ReadOnlySpan<char> name)
        {
            for (var i = 0; i < parameterNames.Length; i++)
            {
                if (name.Equals(parameterNames[i], StringComparison.Ordinal)) return i;
            }
            var match = -1;
            for (var i = 0; i < parameterNames.Length; i++)
            {
                if (!name.Equals(parameterNames[i], StringComparison.OrdinalIgnoreCase)) continue;
                if (match >= 0) return -1;
                match = i;
            }
            return match;
        }
    }
}
