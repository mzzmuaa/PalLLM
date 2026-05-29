using System.Security.Cryptography;
using System.Text;

namespace PalLLM.Domain.Inference;

internal static class MediaCacheIdBuilder
{
    public static string Build(string modality, string mediaType, string base64Payload)
    {
        string normalizedModality = string.IsNullOrWhiteSpace(modality)
            ? "media"
            : modality.Trim().ToLowerInvariant();
        string canonical = string.Concat(
            string.IsNullOrWhiteSpace(mediaType) ? "application/octet-stream" : mediaType.Trim().ToLowerInvariant(),
            "\n",
            base64Payload.Trim());
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return string.Concat(
            "palllm-",
            normalizedModality,
            "-sha256-",
            Convert.ToHexString(hash, 0, 16).ToLowerInvariant());
    }
}
