using System.Data;
using System.Globalization;
using Dapper;

namespace HomeBudget.Web.Data;

public sealed class SqliteDecimalHandler : SqlMapper.TypeHandler<decimal>
{
    public override void SetValue(IDbDataParameter parameter, decimal value)
        => parameter.Value = (double)value;

    public override decimal Parse(object value) => value switch
    {
        decimal d => d,
        double db => Convert.ToDecimal(db),
        float f => Convert.ToDecimal(f),
        long l => l,
        int i => i,
        string s when decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var p) => p,
        _ => Convert.ToDecimal(value, CultureInfo.InvariantCulture)
    };
}

public sealed class SqliteBoolHandler : SqlMapper.TypeHandler<bool>
{
    public override void SetValue(IDbDataParameter parameter, bool value)
        => parameter.Value = value ? 1L : 0L;

    public override bool Parse(object value) => value switch
    {
        bool b => b,
        long l => l != 0,
        int i => i != 0,
        string s => s == "1" || bool.TryParse(s, out var b) && b,
        _ => Convert.ToBoolean(value)
    };
}
