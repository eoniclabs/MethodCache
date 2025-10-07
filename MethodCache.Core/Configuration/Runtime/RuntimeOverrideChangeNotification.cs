using System;
using System.Collections.Generic;

namespace MethodCache.Core.Configuration.Runtime;

internal enum RuntimeOverrideChangeKind
{
    Upsert,
    Removed,
    Cleared
}

internal sealed class RuntimeOverridesChangedEventArgs : EventArgs
{
    public RuntimeOverridesChangedEventArgs(RuntimeOverrideChangeKind kind, IReadOnlyCollection<string> affectedMethodKeys)
    {
        Kind = kind;
        AffectedMethodKeys = affectedMethodKeys ?? Array.Empty<string>();
    }

    public RuntimeOverrideChangeKind Kind { get; }

    public IReadOnlyCollection<string> AffectedMethodKeys { get; }
}
