using MigrationStudio.Tests;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace MigrationStudio.Tests.Golden
{
    internal static class JsonAssert
    {
        public static void Equal(JsonElement expected, JsonElement actual, string path = "$")
        {
            if (expected.ValueKind != actual.ValueKind)
            {
                if (TryFlexibleScalarEqual(expected, actual))
                {
                    return;
                }

                Assert.Fail(path + ": kind expected " + expected.ValueKind + ", actual " + actual.ValueKind);
            }

            switch (expected.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var prop in expected.EnumerateObject())
                    {
                        if (!actual.TryGetProperty(prop.Name, out var actualProp))
                        {
                            if (prop.Value.ValueKind == JsonValueKind.Null)
                            {
                                continue;
                            }

                            Assert.Fail(path + "." + prop.Name + ": missing in actual");
                        }
                        else
                        {
                            Equal(prop.Value, actualProp, path + "." + prop.Name);
                        }
                    }

                    foreach (var prop in actual.EnumerateObject())
                    {
                        if (!expected.TryGetProperty(prop.Name, out _))
                        {
                            Assert.Fail(path + "." + prop.Name + ": unexpected property in actual");
                        }
                    }

                    break;
                case JsonValueKind.Array:
                    var expArr = expected.EnumerateArray().ToList();
                    var actArr = actual.EnumerateArray().ToList();
                    AssertEx.Equal(expArr.Count, actArr.Count, path + ": array length");
                    for (var i = 0; i < expArr.Count; i++)
                    {
                        Equal(expArr[i], actArr[i], path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]");
                    }

                    break;
                case JsonValueKind.String:
                    AssertEx.Equal(expected.GetString(), actual.GetString(), path);
                    break;
                case JsonValueKind.Number:
                    AssertEx.Equal(expected.GetRawText(), actual.GetRawText(), path);
                    break;
                case JsonValueKind.True:
                case JsonValueKind.False:
                    AssertEx.Equal(expected.GetBoolean(), actual.GetBoolean(), path);
                    break;
                case JsonValueKind.Null:
                    break;
                default:
                    AssertEx.Equal(expected.GetRawText(), actual.GetRawText(), path);
                    break;
            }
        }

        public static void EqualJson(string expectedJson, string actualJson, string context)
        {
            using var exp = JsonDocument.Parse(expectedJson);
            using var act = JsonDocument.Parse(actualJson);
            try
            {
                Equal(exp.RootElement, act.RootElement, context);
            }
            catch (Exception ex)
            {
                Assert.Fail(context + ": " + ex.Message);
            }
        }

        private static bool TryFlexibleScalarEqual(JsonElement expected, JsonElement actual)
        {
            if (expected.ValueKind == JsonValueKind.Number && actual.ValueKind == JsonValueKind.String)
            {
                return expected.GetRawText() == actual.GetString();
            }

            if (expected.ValueKind == JsonValueKind.String && actual.ValueKind == JsonValueKind.Number)
            {
                return actual.GetRawText() == expected.GetString();
            }

            if (expected.ValueKind == JsonValueKind.Null && actual.ValueKind == JsonValueKind.String && actual.GetString() == "")
            {
                return true;
            }

            return false;
        }

        public static void EqualLists<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual, string label)
        {
            AssertEx.Equal(expected?.Count ?? 0, actual?.Count ?? 0, label + " count");
            if (expected == null)
            {
                return;
            }

            for (var i = 0; i < expected.Count; i++)
            {
                AssertEx.Equal(expected[i], actual[i], label + "[" + i.ToString(CultureInfo.InvariantCulture) + "]");
            }
        }
    }
}
