using System;
using System.Collections.Generic;

namespace MethodCache.Core.Configuration
{
    public class MethodConfiguration : IMethodConfiguration
    {
        private readonly CacheMethodSettings _settings;

        public MethodConfiguration(CacheMethodSettings settings)
        {
            _settings = settings;
        }

        public IMethodConfiguration Duration(TimeSpan duration)
        {
            _settings.Duration = duration;
            return this;
        }

        public IMethodConfiguration Duration(Func<CacheExecutionContext, TimeSpan> durationFactory)
        {
            // This will be implemented later when we have a more dynamic settings object
            return this;
        }

        public IMethodConfiguration TagWith(string tag)
        {
            _settings.Tags.Add(tag);
            return this;
        }

        public IMethodConfiguration TagWith(Func<CacheExecutionContext, string> tagFactory)
        {
            // This will be implemented later when we have a more dynamic settings object
            return this;
        }

        public IMethodConfiguration Version(int version)
        {
            _settings.Version = version;
            return this;
        }

        public IMethodConfiguration KeyGenerator<TGenerator>() where TGenerator : ICacheKeyGenerator, new()
        {
            _settings.KeyGeneratorType = typeof(TGenerator);
            return this;
        }

        public IMethodConfiguration When(Func<CacheExecutionContext, bool> condition)
        {
            _settings.Condition = condition;
            return this;
        }

        public IMethodConfiguration OnHit(Action<CacheExecutionContext> onHitAction)
        {
            _settings.OnHitAction = onHitAction;
            return this;
        }

        public IMethodConfiguration OnMiss(Action<CacheExecutionContext> onMissAction)
        {
            _settings.OnMissAction = onMissAction;
            return this;
        }
    }
}
