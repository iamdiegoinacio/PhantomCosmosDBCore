namespace CosmosDB.InMemoryEmulator.Domain.Primitives;

/// <summary>
/// Approximates Azure Cosmos DB request-unit (RU) charges so that emulated responses scale with
/// payload size and result size instead of always reporting a flat value.
/// </summary>
/// <remarks>
/// Reference points from the Cosmos DB documentation: a point read of a 1 KB item costs 1 RU,
/// a write of a 1 KB item costs about 5 RUs and a simple query costs a few RUs plus a small
/// per-document amount. This is a model, not a billing-accurate computation.
/// </remarks>
internal static class RequestChargeCalculator
{
    private const double BytesPerKilobyte = 1024d;
    private const double PointReadPerKb = 1.0;
    private const double WritePerKb = 5.0;
    private const double QueryBaseCharge = 2.5;
    private const double QueryPerDocument = 0.1;
    private const double MinimumCharge = 1.0;

    public static double PointRead(long itemSizeBytes)
        => Round(Math.Max(MinimumCharge, Math.Ceiling(Kilobytes(itemSizeBytes)) * PointReadPerKb));

    public static double Write(long itemSizeBytes)
        => Round(Math.Max(MinimumCharge, Math.Ceiling(Kilobytes(itemSizeBytes)) * WritePerKb));

    public static double Query(int documentCount, long resultSizeBytes = 0)
        => Round(QueryBaseCharge + (documentCount * QueryPerDocument) + (Kilobytes(resultSizeBytes) * 0.05));

    public static double Delete(long itemSizeBytes)
        => Round(Math.Max(MinimumCharge, Math.Ceiling(Kilobytes(itemSizeBytes)) * WritePerKb));

    private static double Kilobytes(long bytes) => Math.Max(0, bytes) / BytesPerKilobyte;

    private static double Round(double value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
