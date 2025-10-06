using System;
using System.Collections.Generic;
using System.Threading;

namespace MethodCache.Abstractions.Context;

public readonly record struct CacheCallContext(
    string MethodId,
    IReadOnlyList<object?> Arguments,
    IServiceProvider ServiceProvider,
    CancellationToken CancellationToken,
    IReadOnlyDictionary<string, object?>? Items = null);
