using System;
using System.Globalization;
using System.Text;

namespace MethodCache.Internal
{
    // Shared spelling for reflection and Roslyn. Assembly versions never participate.
    internal static class AssemblyIdentity
    {
        internal static string Prefix(string name)
        {
            var text = new StringBuilder(name.Length + 2).Append('[');
            foreach (var character in name)
            {
                if (character >= 'a' && character <= 'z' || character >= 'A' && character <= 'Z' ||
                    character >= '0' && character <= '9' || character == '.' || character == '_' || character == '-') text.Append(character);
                else text.Append('%').Append(((ushort)character).ToString("X4", CultureInfo.InvariantCulture));
            }
            return text.Append(']').ToString();
        }

        internal static string PolicyPrefix(string name) => Prefix(name);
    }
}
