namespace MethodCache
{
    /// <summary>
    /// Stands for any argument of type <typeparamref name="T"/> when pointing at a method in an expression, as in
    /// <c>s =&gt; s.GetAsync(Any&lt;int&gt;.Value)</c>.
    /// </summary>
    public static class Any<T>
    {
        /// <summary>
        /// A placeholder for any argument; only its position in the expression matters, never its value.
        /// </summary>
        public static T Value => default(T)!;
    }
}
