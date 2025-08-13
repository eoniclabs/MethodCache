using System;

namespace MethodCache.Core
{
    public class CacheExecutionContext
    {
        public string MethodName { get; }
        public Type ServiceType { get; }
        public object[] Args { get; }
        public IServiceProvider Services { get; }
        public CancellationToken CancellationToken { get; }

        public CacheExecutionContext(string methodName, Type serviceType, object[] args, IServiceProvider services, CancellationToken cancellationToken)
        {
            MethodName = methodName;
            ServiceType = serviceType;
            Args = args;
            Services = services;
            CancellationToken = cancellationToken;
        }

        public T GetArg<T>(int index)
        {
            return (T)Args[index];
        }

        public T GetService<T>() where T : notnull
        {
            return (T)Services.GetService(typeof(T))!;
        }
    }
}
