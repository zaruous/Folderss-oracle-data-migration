using System;
using System.Collections.Generic;
using Xunit;

namespace MigrationStudio.Tests
{
    internal static class AssertEx
    {
        public static void Equal<T>(T expected, T actual)
        {
            Assert.Equal(expected, actual);
        }

        public static void Equal<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                Assert.Fail(message + " — expected [" + Format(expected) + "], actual [" + Format(actual) + "]");
            }
        }

        public static void Null(object value, string message)
        {
            if (value != null)
            {
                Assert.Fail(message + " — expected null, actual [" + Format(value) + "]");
            }
        }

        public static void NotNull(object value, string message)
        {
            if (value == null)
            {
                Assert.Fail(message + " — expected non-null");
            }
        }

        private static string Format(object value)
        {
            if (value == null)
            {
                return "null";
            }

            return value.ToString();
        }
    }
}
