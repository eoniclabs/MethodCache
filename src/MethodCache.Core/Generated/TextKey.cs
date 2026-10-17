using System.ComponentModel;

namespace MethodCache.Generated;

/// <summary>Already materialized key text bound to one immutable invocation snapshot.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public readonly struct TextKey
{
    internal TextKey(string text, CallSnapshot binding) { Text = text; Binding = binding; }
    internal string Text { get; }
    internal CallSnapshot Binding { get; }
    /// <summary>Returns the owned key text without allocating another string.</summary>
    public override string ToString()
    {
        if (Text == null) return string.Empty;
        MethodCache.Core.Runtime.Keys.ProcessKeyHash.Remember(Text, Binding.HashKey(Text));
        return Text;
    }
}
