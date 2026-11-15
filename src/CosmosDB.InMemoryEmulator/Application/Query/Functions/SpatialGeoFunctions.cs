#nullable enable
using System.Globalization;
using CosmosDB.InMemoryEmulator.Domain.Primitives;
using Newtonsoft.Json.Linq;

namespace CosmosDB.InMemoryEmulator.Application.Query.Functions;

/// <summary>
/// Evaluates Cosmos DB GeoJSON spatial SQL functions (<c>ST_DISTANCE</c>, <c>ST_WITHIN</c>,
/// <c>ST_INTERSECTS</c>, <c>ST_ISVALID</c>, <c>ST_ISVALIDDETAILED</c>, and <c>ST_AREA</c>).
/// </summary>
internal static class SpatialGeoFunctions
{
    private readonly record struct GeoPoint(double Lat, double Lon);

    private readonly record struct GeoCircle(GeoPoint Center, double RadiusMeters);

    public static object? StDistance(object? left, object? right)
    {
        var p1 = ExtractPoint(left);
        var p2 = ExtractPoint(right);
        if (p1 is null || p2 is null)
        {
            return null;
        }

        return HaversineDistanceMeters(p1.Value.Lat, p1.Value.Lon, p2.Value.Lat, p2.Value.Lon);
    }

    public static bool StWithin(object? point, object? region)
    {
        var p = ExtractPoint(point);
        if (p is null)
        {
            return false;
        }

        var polygon = ExtractPolygonRings(region);
        if (polygon is not null)
        {
            return PointInPolygon(p.Value, polygon);
        }

        var circle = ExtractCircle(region);
        if (circle is not null)
        {
            var dist = HaversineDistanceMeters(p.Value.Lat, p.Value.Lon, circle.Value.Center.Lat, circle.Value.Center.Lon);
            return dist <= circle.Value.RadiusMeters;
        }

        return false;
    }

    public static bool StIntersects(object? geo1, object? geo2)
    {
        var p1 = ExtractPoint(geo1);
        var p2 = ExtractPoint(geo2);

        if (p1 is not null && p2 is not null)
        {
            return Math.Abs(p1.Value.Lat - p2.Value.Lat) < 1e-10 &&
                   Math.Abs(p1.Value.Lon - p2.Value.Lon) < 1e-10;
        }

        if (p1 is not null)
        {
            return StWithin(geo1, geo2);
        }

        if (p2 is not null)
        {
            return StWithin(geo2, geo1);
        }

        var poly1 = ExtractPolygonRings(geo1);
        var poly2 = ExtractPolygonRings(geo2);
        if (poly1 is not null && poly2 is not null)
        {
            return PolygonsShareAnyPoint(poly1, poly2);
        }

        return false;
    }

    public static bool StIsValid(object? geo)
    {
        var obj = AsJObject(geo);
        if (obj is null)
        {
            return false;
        }

        var type = obj["type"]?.ToString();
        return type switch
        {
            "Point" => ExtractPoint(geo) is not null,
            "Polygon" => ExtractPolygonRings(geo) is { } rings && ValidatePolygonRings(rings),
            "LineString" => ExtractCoordinateArray(obj["coordinates"]) is { Count: >= 2 },
            "MultiPoint" => ExtractCoordinateArray(obj["coordinates"]) is { Count: >= 1 },
            _ => false
        };
    }

    public static JObject StIsValidDetailed(object? geo)
    {
        var obj = AsJObject(geo);
        if (obj is null)
        {
            return JObject.FromObject(new { valid = false, reason = "Not a valid GeoJSON object." });
        }

        var type = obj["type"]?.ToString();
        if (string.IsNullOrEmpty(type))
        {
            return JObject.FromObject(new { valid = false, reason = "GeoJSON object missing 'type' property." });
        }

        switch (type)
        {
            case "Point":
                if (ExtractPoint(geo) is null)
                {
                    return JObject.FromObject(new { valid = false, reason = "Point must have coordinates [longitude, latitude]." });
                }

                return JObject.FromObject(new { valid = true, reason = "" });

            case "Polygon":
                var rings = ExtractPolygonRings(geo);
                if (rings is null)
                {
                    return JObject.FromObject(new { valid = false, reason = "Polygon coordinates must be an array of linear rings." });
                }

                if (!ValidatePolygonRings(rings))
                {
                    return JObject.FromObject(new { valid = false, reason = "Polygon rings must be closed (first and last position must be identical) and have at least 4 positions." });
                }

                return JObject.FromObject(new { valid = true, reason = "" });

            case "LineString":
                if (ExtractCoordinateArray(obj["coordinates"]) is not { Count: >= 2 })
                {
                    return JObject.FromObject(new { valid = false, reason = "LineString must have at least 2 positions." });
                }

                return JObject.FromObject(new { valid = true, reason = "" });

            default:
                return JObject.FromObject(new { valid = false, reason = $"Unsupported GeoJSON type: {type}." });
        }
    }

    public static object? StArea(object? geo)
    {
        var rings = ExtractPolygonRings(geo);
        if (rings is null || rings.Count == 0) return null;
        var area = SphericalPolygonArea(rings[0]);
        for (var i = 1; i < rings.Count; i++)
            area -= SphericalPolygonArea(rings[i]);
        return Math.Abs(area);
    }

    private static double SphericalPolygonArea(List<GeoPoint> ring)
    {
        double sum = 0;
        for (var i = 0; i < ring.Count - 1; i++)
        {
            var lon1 = ring[i].Lon * Math.PI / 180;
            var lat1 = ring[i].Lat * Math.PI / 180;
            var lon2 = ring[(i + 1) % (ring.Count - 1)].Lon * Math.PI / 180;
            var lat2 = ring[(i + 1) % (ring.Count - 1)].Lat * Math.PI / 180;
            sum += (lon2 - lon1) * (2 + Math.Sin(lat1) + Math.Sin(lat2));
        }
        return Math.Abs(sum * ContainerConstants.EarthRadiusMeters * ContainerConstants.EarthRadiusMeters / 2.0);
    }

    private static JObject? AsJObject(object? value)
    {
        return value switch
        {
            JObject jo => jo,
            string s when s.TrimStart().StartsWith("{", StringComparison.Ordinal) => JsonParseHelpers.ParseJson(s),
            _ => null
        };
    }

    private static GeoPoint? ExtractPoint(object? value)
    {
        var obj = AsJObject(value);
        if (obj is null) return null;
        if (!string.Equals(obj["type"]?.ToString(), "Point", StringComparison.OrdinalIgnoreCase)) return null;
        if (obj["coordinates"] is not JArray coords || coords.Count < 2) return null;

        var lon = coords[0].Value<double>();
        var lat = coords[1].Value<double>();
        if (lat < -90 || lat > 90 || lon < -180 || lon > 180) return null;

        return new GeoPoint(lat, lon);
    }

    private static List<List<GeoPoint>>? ExtractPolygonRings(object? value)
    {
        var obj = AsJObject(value);
        if (obj is null) return null;
        if (!string.Equals(obj["type"]?.ToString(), "Polygon", StringComparison.OrdinalIgnoreCase)) return null;
        if (obj["coordinates"] is not JArray coords || coords.Count == 0) return null;

        var rings = new List<List<GeoPoint>>();
        foreach (var ring in coords)
        {
            var points = ExtractCoordinateArray(ring);
            if (points is null || points.Count < 4) return null;
            rings.Add(points);
        }

        return rings;
    }

    private static GeoCircle? ExtractCircle(object? value)
    {
        var obj = AsJObject(value);
        if (obj is null) return null;

        var center = obj["center"];
        var radius = obj["radius"];
        if (center is null || radius is null) return null;

        var centerPoint = ExtractPoint(center);
        if (centerPoint is null) return null;

        if (!double.TryParse(radius.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var radiusMeters))
            return null;

        return new GeoCircle(centerPoint.Value, radiusMeters);
    }

    private static List<GeoPoint>? ExtractCoordinateArray(JToken? token)
    {
        if (token is not JArray arr) return null;

        var points = new List<GeoPoint>();
        foreach (var item in arr)
        {
            if (item is JArray coord && coord.Count >= 2)
            {
                var lon = coord[0].Value<double>();
                var lat = coord[1].Value<double>();
                points.Add(new GeoPoint(lat, lon));
            }
            else
            {
                return null;
            }
        }

        return points;
    }

    private static bool ValidatePolygonRings(List<List<GeoPoint>> rings)
    {
        foreach (var ring in rings)
        {
            if (ring.Count < 4) return false;

            var first = ring[0];
            var last = ring[^1];
            if (Math.Abs(first.Lat - last.Lat) > 1e-10 || Math.Abs(first.Lon - last.Lon) > 1e-10)
            {
                return false;
            }
        }

        return true;
    }

    private static double HaversineDistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = DegreesToRadians(lat2 - lat1);
        var dLon = DegreesToRadians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(DegreesToRadians(lat1)) * Math.Cos(DegreesToRadians(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return ContainerConstants.EarthRadiusMeters * c;
    }

    private static double DegreesToRadians(double degrees) => degrees * (Math.PI / 180.0);

    private static bool PointInPolygon(GeoPoint point, List<List<GeoPoint>> rings)
    {
        if (rings.Count == 0) return false;
        if (!PointInRing(point, rings[0])) return false;

        for (var i = 1; i < rings.Count; i++)
        {
            if (PointInRing(point, rings[i])) return false;
        }

        return true;
    }

    private static bool PointInRing(GeoPoint point, List<GeoPoint> ring)
    {
        var inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            if ((ring[i].Lat > point.Lat) != (ring[j].Lat > point.Lat) &&
                point.Lon < (ring[j].Lon - ring[i].Lon) * (point.Lat - ring[i].Lat) / (ring[j].Lat - ring[i].Lat) + ring[i].Lon)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static bool PolygonsShareAnyPoint(List<List<GeoPoint>> poly1, List<List<GeoPoint>> poly2)
    {
        foreach (var point in poly1[0])
        {
            if (PointInPolygon(point, poly2)) return true;
        }

        foreach (var point in poly2[0])
        {
            if (PointInPolygon(point, poly1)) return true;
        }

        return false;
    }
}
