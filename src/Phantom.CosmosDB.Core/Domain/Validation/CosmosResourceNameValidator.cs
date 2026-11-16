#nullable enable
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Phantom.CosmosDB.Core.Domain.Primitives;

namespace Phantom.CosmosDB.Core.Domain.Validation;

/// <summary>
/// Validates Cosmos DB resource identifiers (Databases, Containers, Documents) and extracts simple
/// metadata query filters.
/// </summary>
internal static class CosmosResourceNameValidator
{
    private static readonly char[] ProhibitedIdChars = ['/', '\\', '#', '?'];

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

        if (name.IndexOfAny(ProhibitedIdChars) >= 0)
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
    /// Validates a document identifier against Cosmos DB rules:
    /// non-empty, max 1023 UTF-8 bytes, no '/', '\\', '?', '#', and no control characters.
    /// </summary>
    public static void ValidateDocumentId(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            throw InMemoryCosmosException.Create(
                "The 'id' property must be a non-empty string.",
                HttpStatusCode.BadRequest,
                0,
                Guid.NewGuid().ToString(),
                ContainerConstants.SyntheticRequestCharge);
        }

        if (Encoding.UTF8.GetByteCount(id) > 1023)
        {
            throw InMemoryCosmosException.Create(
                $"The 'id' value '{id}' exceeds the maximum allowed size of 1023 bytes.",
                HttpStatusCode.BadRequest,
                0,
                Guid.NewGuid().ToString(),
                ContainerConstants.SyntheticRequestCharge);
        }

        if (id.IndexOfAny(ProhibitedIdChars) >= 0)
        {
            throw InMemoryCosmosException.Create(
                $"The 'id' value '{id}' contains illegal character(s). The characters '/', '\\', '?', and '#' are not allowed.",
                HttpStatusCode.BadRequest,
                0,
                Guid.NewGuid().ToString(),
                ContainerConstants.SyntheticRequestCharge);
        }

        for (var i = 0; i < id.Length; i++)
        {
            if (char.IsControl(id[i]))
            {
                throw InMemoryCosmosException.Create(
                    $"The 'id' value contains illegal control characters.",
                    HttpStatusCode.BadRequest,
                    0,
                    Guid.NewGuid().ToString(),
                    ContainerConstants.SyntheticRequestCharge);
            }
        }
    }

    /// <summary>
    /// Checks whether an 'id' is valid without throwing.
    /// </summary>
    public static bool IsDocumentIdValid(string? id)
    {
        if (string.IsNullOrEmpty(id))
            return false;

        if (Encoding.UTF8.GetByteCount(id) > 1023)
            return false;

        if (id.IndexOfAny(ProhibitedIdChars) >= 0)
            return false;

        for (var i = 0; i < id.Length; i++)
        {
            if (char.IsControl(id[i]))
                return false;
        }

        return true;
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
