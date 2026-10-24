using System.Net;
using Microsoft.AspNetCore.Http.Features;

namespace CreatureLab.Endpoints;

public sealed record ErrorBody(string Error, int? RetryAfterSeconds = null);

internal static class RequestHelpers
{
    public const string PinHeader = "X-Admin-Pin";

    /// <summary>Errors are always { "error": "friendly message" } so pages can show them as-is.</summary>
    public static IResult Error(int statusCode, string message) => Results.Json(new ErrorBody(message), statusCode: statusCode);

    /// <summary>The visitor's IP. Behind a tunnel this comes from X-Forwarded-For (UseForwardedHeaders).</summary>
    public static string ClientIp(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>
    /// True only for requests made on the booth laptop itself (the TV browser at
    /// http://localhost:5000). Tunnel traffic also arrives from localhost, but it always
    /// carries forwarded headers and the tunnel's host name, so it is rejected here.
    /// </summary>
    public static bool IsLocalRequest(HttpContext http)
    {
        var remote = http.Connection.RemoteIpAddress;
        if (remote is null || !IPAddress.IsLoopback(remote)) return false;

        var headers = http.Request.Headers;
        if (headers.ContainsKey("X-Forwarded-For") || headers.ContainsKey("X-Original-For") ||
            headers.ContainsKey("Forwarded") || headers.ContainsKey("CF-Connecting-IP"))
        {
            return false;
        }

        return http.Request.Host.Host is "localhost" or "127.0.0.1" or "[::1]" or "::1";
    }

    public static void LimitRequestBody(HttpContext http, long maxBytes)
    {
        var feature = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = maxBytes;
    }
}
