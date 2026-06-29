using System.Data;
using Dapper;

namespace Obsync.Data;

/// <summary>
/// Stores <see cref="DateTimeOffset"/> as round-trippable ISO-8601 text, sidestepping the
/// ambiguity of SQLite's typeless date handling when read back through Dapper.
/// </summary>
internal sealed class DateTimeOffsetTypeHandler : SqlMapper.TypeHandler<DateTimeOffset>
{
    public override DateTimeOffset Parse(object value) =>
        DateTimeOffset.Parse((string)value, null, System.Globalization.DateTimeStyles.RoundtripKind);

    public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
    {
        parameter.DbType = DbType.String;
        parameter.Value = value.ToString("O");
    }
}

/// <summary>Round-trips <see cref="Guid"/> as TEXT, which SQLite + Dapper do not convert by default.</summary>
internal sealed class GuidTypeHandler : SqlMapper.TypeHandler<Guid>
{
    public override Guid Parse(object value) => Guid.Parse((string)value);

    public override void SetValue(IDbDataParameter parameter, Guid value)
    {
        parameter.DbType = DbType.String;
        parameter.Value = value.ToString();
    }
}
