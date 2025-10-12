namespace MethodCache.Core
{
    public static class Any<T>
    {
        public static T Value => default(T)!;
    }
}
