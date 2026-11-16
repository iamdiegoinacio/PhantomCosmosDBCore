#nullable enable
using System.Globalization;
using Newtonsoft.Json;

namespace Phantom.CosmosDB.Core.Infrastructure.Serialization;

/// <summary>
/// Custom <see cref="JsonTextWriter"/> that writes numbers in their minimal JSON representation,
/// matching how Cosmos DB stores numeric values (no trailing .0 on integers).
/// </summary>
internal sealed class CosmosJsonWriter : JsonTextWriter
{
    public CosmosJsonWriter(TextWriter writer) : base(writer) { }

    public override void WriteValue(decimal value)
    {
        if (value == decimal.Truncate(value) && value >= long.MinValue && value <= long.MaxValue)
        {
            WriteValue((long)value);
        }
        else
        {
            WriteRawValue(value.ToString("G29", CultureInfo.InvariantCulture));
        }
    }

    public override void WriteValue(double value)
    {
        if (value == Math.Truncate(value) && value >= long.MinValue && value < (double)long.MaxValue)
        {
            WriteValue((long)value);
        }
        else
        {
            base.WriteValue(value);
        }
    }
}
