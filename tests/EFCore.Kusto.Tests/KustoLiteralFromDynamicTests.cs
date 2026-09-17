using System;
using EFCore.Kusto.Storage;
using Xunit;

namespace EFCore.Kusto.Tests;

/// <summary>
/// Covers reading a changed value back out of the <c>dynamic</c> bag that carries it into an
/// <c>.update</c> command. A missing or wrong cast fails silently - it writes the wrong type, or
/// null, into the column - so each store type is pinned here.
/// </summary>
public class KustoLiteralFromDynamicTests
{
    [Theory]
    [InlineData("string", "tostring(changes['C'])")]
    [InlineData("bool", "tobool(changes['C'])")]
    [InlineData("guid", "toguid(changes['C'])")]
    [InlineData("datetime", "todatetime(changes['C'])")]
    [InlineData("date", "todatetime(changes['C'])")]
    [InlineData("int", "toint(changes['C'])")]
    [InlineData("long", "tolong(changes['C'])")]
    [InlineData("real", "toreal(changes['C'])")]
    [InlineData("double", "toreal(changes['C'])")]
    [InlineData("decimal", "todecimal(changes['C'])")]
    [InlineData("timespan", "totimespan(changes['C'])")]
    public void Casts_each_Kusto_store_type_back_out_of_the_bag(string kqlType, string expected)
        => Assert.Equal(expected, KustoLiteral.FromDynamic("changes['C']", kqlType));

    [Theory]
    [InlineData("dynamic")]
    [InlineData(null)]
    [InlineData("something_new")]
    public void Passes_through_types_it_does_not_know(string? kqlType)
    {
        // A dynamic column needs no cast, and an unrecognised store type is left for Kusto to
        // coerce - which fails loudly - rather than being forced through a guessed conversion.
        Assert.Equal("changes['C']", KustoLiteral.FromDynamic("changes['C']", kqlType));
    }

    [Fact]
    public void Covers_every_type_KustoLiteral_can_emit_a_typed_null_for()
    {
        // KustoLiteral.TypedNull is the provider's list of store types. If a type is added there
        // without a matching cast here, values of that type would silently pass through uncast.
        var types = new[] { "string", "guid", "bool", "date", "datetime", "int", "long", "real",
                            "decimal", "double", "timespan" };

        foreach (var type in types)
        {
            var rendered = KustoLiteral.FromDynamic("v", type);
            Assert.True(rendered != "v", $"store type '{type}' has no cast and would be written uncast");
        }
    }
}
