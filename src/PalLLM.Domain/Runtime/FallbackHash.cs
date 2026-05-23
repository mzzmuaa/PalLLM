using System.Text;
using PalLLM.Domain.Integration;

namespace PalLLM.Domain.Runtime;

/// <summary>
/// Stable FNV-1a 32-bit hashing used by the fallback director for deterministic selection.
/// Previously duplicated in <c>FallbackBehaviorContext</c> and <c>FallbackBehaviorEngine</c>.
/// Lowercases each input character so seed derivation and variant picking are case-insensitive.
/// </summary>
internal static class FallbackHash
{
    private const int FnvOffsetBasis = unchecked((int)2166136261);
    private const int FnvPrime = 16777619;

    public static int OfString(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return FnvOffsetBasis;
        }

        unchecked
        {
            int hash = FnvOffsetBasis;
            foreach (char character in value)
            {
                hash ^= char.ToLowerInvariant(character);
                hash *= FnvPrime;
            }

            return hash;
        }
    }

    public static int Seed(
        ChatRequest request,
        GameCharacterSnapshot? character,
        GameWorldSnapshot snapshot)
    {
        var builder = new StringBuilder(64);
        builder.Append(request.TaskTag);
        builder.Append('|');
        builder.Append(request.UserMessage.Trim());
        builder.Append('|');
        builder.Append(character?.DisplayName ?? request.CharacterName ?? "Palworld");
        builder.Append('|');
        builder.Append(snapshot.WorldName);
        builder.Append('|');
        builder.Append(snapshot.CurrentObjective);
        return OfString(builder.ToString());
    }

    public static int PositiveModulo(int value, int modulo)
    {
        int result = value % modulo;
        return result < 0 ? result + modulo : result;
    }
}
