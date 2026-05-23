namespace PalLLM.Domain.Inference;

internal static class TransportFailureStatusBuilder
{
    public static string HttpStatus(string surface, int statusCode)
    {
        return statusCode switch
        {
            400 => $"{surface} endpoint rejected the request (HTTP 400).",
            401 => $"{surface} endpoint rejected authentication (HTTP 401).",
            403 => $"{surface} endpoint refused the request (HTTP 403).",
            404 => $"{surface} endpoint was not found (HTTP 404).",
            408 => $"{surface} endpoint timed out while handling the request (HTTP 408).",
            413 => $"{surface} endpoint rejected the request as too large (HTTP 413).",
            415 => $"{surface} endpoint rejected the request media type (HTTP 415).",
            422 => $"{surface} endpoint rejected the request payload (HTTP 422).",
            429 => $"{surface} endpoint rate-limited the request (HTTP 429).",
            >= 500 and <= 599 => $"{surface} endpoint failed while handling the request (HTTP {statusCode}).",
            _ => $"{surface} endpoint returned HTTP {statusCode}.",
        };
    }

    public static string Timeout(string surface) =>
        $"{surface} endpoint timed out before completing the response.";

    public static string Unreachable(string surface) =>
        $"{surface} endpoint is unreachable.";

    public static string MalformedJson(string surface) =>
        $"{surface} endpoint returned malformed JSON.";
}
