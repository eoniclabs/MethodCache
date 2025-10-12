namespace MethodCache.Core.Configuration.Surfaces.Attributes
{
    public static class Any<T>
    {
        public static T Value => default(T)!;
    }
}
