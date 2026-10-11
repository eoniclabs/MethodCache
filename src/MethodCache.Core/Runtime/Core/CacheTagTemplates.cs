using System.ComponentModel;

namespace MethodCache.Core.Runtime.Core
{
    /// <summary>
    /// Expands parameter templates such as <c>"user:{userId}"</c> in a policy's tags, so a cached entry carries
    /// the same tag that <see cref="CacheInvalidateAttribute"/> builds from its arguments.
    /// </summary>
    /// <remarks>Called by generated decorators on a cache miss; not intended for application code.</remarks>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class CacheTagTemplates
    {
        /// <summary>
        /// Returns <paramref name="policy"/> with every <c>{name}</c> in its tags replaced by the matching value.
        /// Parameter names match case-insensitively; placeholders without a matching name are left as they are.
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
                throw new ArgumentException("Every parameter name needs exactly one value.", nameof(values));
            }

            if (!policy.Tags.Any(tag => tag.Contains('{')))
            {
                return policy;
            }

            var tags = new List<string>(policy.Tags.Count);
            foreach (var tag in policy.Tags)
            {
                var expanded = tag;
                for (var i = 0; i < parameterNames.Length && expanded.Contains('{'); i++)
                {
                    expanded = expanded.Replace("{" + parameterNames[i] + "}", values[i], StringComparison.OrdinalIgnoreCase);
                }

                tags.Add(expanded);
            }

            return policy with { Tags = tags };
        }
    }
}
