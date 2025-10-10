using System;
using System.Collections.Generic;

namespace MethodCache.Core.Configuration
{
    public class GroupConfiguration : IGroupConfiguration
    {
        private readonly CacheMethodSettings _settings;

        public GroupConfiguration(CacheMethodSettings settings)
        {
            _settings = settings;
        }

        public IGroupConfiguration Duration(TimeSpan duration)
        {
            _settings.Duration = duration;
            return this;
        }

        public IGroupConfiguration TagWith(string tag)
        {
            _settings.Tags.Add(tag);
            return this;
        }

        public IGroupConfiguration Version(int version)
        {
            _settings.Version = version;
            return this;
        }

        public IGroupConfiguration KeyGenerator<TGenerator>() where TGenerator : ICacheKeyGenerator, new()
        {
            _settings.KeyGeneratorType = typeof(TGenerator);
            return this;
        }

        public IGroupConfiguration When(Func<CacheExecutionContext, bool> condition)
        {
            _settings.Condition = condition;
            return this;
        }

        public IGroupConfiguration OnHit(Action<CacheExecutionContext> onHitAction)
        {
            _settings.OnHitAction = onHitAction;
            return this;
        }

        public IGroupConfiguration OnMiss(Action<CacheExecutionContext> onMissAction)
        {
            _settings.OnMissAction = onMissAction;
            return this;
        }
    }
}
