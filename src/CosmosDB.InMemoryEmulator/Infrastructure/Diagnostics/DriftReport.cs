#nullable enable
using System.Text.Json.Serialization;

namespace CosmosDB.InMemoryEmulator;

/// <summary>
/// Structured report produced by <see cref="SdkVersionDriftDetector.RunAsync"/>.
/// Serialisable to JSON for CI artifact consumption.
/// </summary>
public sealed class DriftReport
{
    [JsonPropertyName("sdkVersion")]
    public string SdkVersion { get; set; } = "";

    [JsonPropertyName("testSuiteVersion")]
    public string TestSuiteVersion { get; set; } = "";

    [JsonPropertyName("minTestedVersion")]
    public string MinTestedVersion { get; set; } = "";

    [JsonPropertyName("maxTestedVersion")]
    public string MaxTestedVersion { get; set; } = "";

    [JsonPropertyName("isWithinTestedRange")]
    public bool IsWithinTestedRange { get; set; }

    [JsonPropertyName("compatibilityPassed")]
    public bool CompatibilityPassed { get; set; }

    [JsonPropertyName("compatibilityError")]
    public string? CompatibilityError { get; set; }

    [JsonPropertyName("unrecognisedHeaders")]
    public List<string> UnrecognisedHeaders { get; set; } = [];
}
