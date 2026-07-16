using System.Collections;
using System.Reflection;
using Microsoft.SqlServer.Management.Smo;
using Obsync.Shared.Objects;
using Obsync.Smo;
using Xunit;

namespace Obsync.Engine.Tests;

/// <summary>
/// Regression guard for a production-failing bug: <c>SmoScriptProvider</c> reads
/// <c>IsSystemObject</c> through a dynamic binder, so declaring the flag for an SMO class that
/// does not expose the property throws at runtime and fails the WHOLE run (seen live with
/// <c>UserDefinedDataType</c>: any database containing an alias type failed every sync).
/// This test verifies every map entry's flag against the real SMO class by reflection.
/// </summary>
public class SmoTypeMapTests
{
    /// <summary>The SMO class each mapped SqlObjectType enumerates (mirrors the provider's collections).</summary>
    private static readonly Dictionary<SqlObjectType, Type> SmoClassByType = new()
    {
        [SqlObjectType.Table] = typeof(Table),
        [SqlObjectType.User] = typeof(User),
        [SqlObjectType.Role] = typeof(DatabaseRole),
        [SqlObjectType.ApplicationRole] = typeof(ApplicationRole),
        [SqlObjectType.UserDefinedDataType] = typeof(UserDefinedDataType),
        [SqlObjectType.UserDefinedTableType] = typeof(UserDefinedTableType),
        [SqlObjectType.XmlSchemaCollection] = typeof(XmlSchemaCollection),
        [SqlObjectType.UserDefinedType] = typeof(UserDefinedType),
        [SqlObjectType.UserDefinedAggregate] = typeof(UserDefinedAggregate),
        [SqlObjectType.PartitionFunction] = typeof(PartitionFunction),
        [SqlObjectType.PartitionScheme] = typeof(PartitionScheme),
        [SqlObjectType.Assembly] = typeof(SqlAssembly),
        [SqlObjectType.FullTextCatalog] = typeof(FullTextCatalog),
        [SqlObjectType.ColumnMasterKey] = typeof(ColumnMasterKey),
        [SqlObjectType.ColumnEncryptionKey] = typeof(ColumnEncryptionKey),
        [SqlObjectType.SecurityPolicy] = typeof(SecurityPolicy),
    };

    [Fact]
    public void EveryMapEntry_FlagMatchesTheRealSmoClass()
    {
        foreach (var (objectType, typeMap) in ReadProviderMap())
        {
            Assert.True(SmoClassByType.TryGetValue(objectType, out var smoClass),
                $"SmoScriptProvider maps {objectType} but this test doesn't know its SMO class — add it.");

            var declared = (bool)typeMap.GetType().GetProperty("HasIsSystemObject")!.GetValue(typeMap)!;
            var actual = smoClass!.GetProperty("IsSystemObject") is not null;

            Assert.True(declared == actual,
                $"{objectType}: map declares HasIsSystemObject={declared} but SMO {smoClass.Name} " +
                $"{(actual ? "has" : "does NOT have")} the property. A wrong 'true' fails whole runs at runtime.");
        }
    }

    [Fact]
    public void HeavyClrTypes_OnlyRequestInitFieldsTheClassExposes()
    {
        foreach (var (objectType, typeMap) in ReadProviderMap())
        {
            var heavy = (Type?)typeMap.GetType().GetProperty("HeavyClrType")!.GetValue(typeMap);
            if (heavy is null)
            {
                continue;
            }

            var declared = (bool)typeMap.GetType().GetProperty("HasIsSystemObject")!.GetValue(typeMap)!;
            if (declared)
            {
                Assert.True(heavy.GetProperty("IsSystemObject") is not null,
                    $"{objectType}: SetInitFields would request IsSystemObject on {heavy.Name}, which lacks it.");
            }
        }
    }

    private static IEnumerable<(SqlObjectType Type, object Map)> ReadProviderMap()
    {
        var field = typeof(SmoScriptProvider).GetField("Map", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var map = (IDictionary)field!.GetValue(null)!;
        Assert.True(map.Count > 0, "SmoScriptProvider.Map is empty — did the field move?");
        foreach (DictionaryEntry entry in map)
        {
            yield return ((SqlObjectType)entry.Key, entry.Value!);
        }
    }
}
