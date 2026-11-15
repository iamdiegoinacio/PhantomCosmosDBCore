#nullable enable
using System.Net;
using System.Text.RegularExpressions;

namespace CosmosDB.InMemoryEmulator.Domain.Validation;

/// <summary>
/// Validates Cosmos DB resource identifiers (Databases, Containers) and extracts simple
/// metadata query filters.
/// </summary>
internal static class CosmosResourceNameValidator
{
    public static void ValidateResourceName(string name, string resourceType)
    {
        if (name.Length > 255)
        {
            throw InMemoryCosmosException.Create(
                $"{resourceType} name must not exceed 255 characters.",
                HttpStatusCode.BadRequest,
                0,
                string.Empty,
                0);
        }

        if (name.IndexOfAny(['/', '\\', '#', '?']) >= 0)
        {
            throw InMemoryCosmosException.Create(
                $"{resourceType} name must not contain '/', '\\', '#', or '?' characters.",
                HttpStatusCode.BadRequest,
                0,
                string.Empty,
                0);
        }
    }

    /// <summary>
    /// Extracts a simple <c>WHERE c.id = 'value'</c> filter from SQL text.
    /// Returns the extracted id value, or null if no simple id filter is found.
    /// </summary>
    public static string? ExtractIdFilter(string? queryText)
    {
        if (string.IsNullOrWhiteSpace(queryText)) return null;
        var match = Regex.Match(
            queryText,
            @"WHERE\s+\w+\.id\s*=\s*'([^']*)'",
            RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    public static bool IsSelectValueIdQuery(string? queryText)
    {
        if (string.IsNullOrWhiteSpace(queryText))
            return false;
        return Regex.IsMatch(queryText, @"SELECT\s+VALUE\s*\(?.*\.id\)?", RegexOptions.IgnoreCase);
    }
}
