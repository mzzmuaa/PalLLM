using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PalLLM.Domain.Portable;

// ---------------------------------------------------------------------------
// Portable adapter contracts.
//
// These are the game-agnostic seams that the PalLLM runtime speaks in terms
// of — world clock, path provider, log writer, character abstraction, game
// adapter — along with a handful of math/text helpers that the runtime
// depends on (deterministic semantic embedder, reasoning-tag stripper,
// 3D vector).
//
// PalLLM was originally wired through a sibling portable-adapter library,
// but shipping a stand-alone release ZIP is easier when the runtime carries
// its own minimal portable surface. Keeping the abstraction means a future
// revision can target a different game (different Character shape, different
// Clock semantics, different world vector system) without rewriting the
// runtime. Everything here is stable, documented, and trimmable.
//
// None of these types reference any specific game's IP. They are engine-
// neutral primitives suitable for any sandbox / survival / companion game
// integration.
// ---------------------------------------------------------------------------

/// <summary>3D world-space coordinate in the adapter's native unit system.
/// Use <see cref="Invalid"/> + <see cref="IsValid"/> to represent unknown
/// positions without allocating a nullable wrapper.</summary>
public readonly struct Vec3 : IEquatable<Vec3>
{
    public readonly float X;
    public readonly float Y;
    public readonly float Z;

    public Vec3(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    /// <summary>Sentinel value for "position unknown". NaN propagates
    /// through arithmetic, so downstream math stays honest about the
    /// missing input.</summary>
    public static readonly Vec3 Invalid = new(float.NaN, 0f, float.NaN);

    public bool IsValid => !float.IsNaN(X) && !float.IsNaN(Z);

    public bool Equals(Vec3 other) => X == other.X && Y == other.Y && Z == other.Z;

    public override bool Equals(object? obj) => obj is Vec3 other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(X, Y, Z);

    public override string ToString() =>
        string.Format(CultureInfo.InvariantCulture, "({0:0.##},{1:0.##},{2:0.##})", X, Y, Z);
}

/// <summary>Read-only view of one in-game character (player or companion).
/// All fields are plain CLR types so the surface stays portable across game
/// backends.</summary>
public interface ICharacter
{
    int Id { get; }
    string DisplayName { get; }
    bool IsAlive { get; }
    bool IsPlayerFaction { get; }
    bool IsIncapacitated { get; }
    Vec3 Position { get; }
    int Age { get; }
    IReadOnlyDictionary<string, int> Skills { get; }
    IReadOnlyDictionary<string, float> Needs { get; }
    IReadOnlyList<string> Traits { get; }
}

/// <summary>Game-time clock exposed to the runtime in adapter-native ticks.
/// Runtime code uses the derived "ticks per hour / per day" ratios to
/// compute human-facing time without embedding any specific game's unit
/// convention.</summary>
public interface IWorldClock
{
    long CurrentTick { get; }
    long TicksPerHour { get; }
    long TicksPerDay { get; }
}

/// <summary>Where the runtime stores its bounded state on disk. Keep
/// directory names string-valued so adapters can point at read-only
/// mount points or tmpfs for ephemeral deployments.</summary>
public interface IPathProvider
{
    string ModelsDir { get; }

    /// <summary>Diffusion model weights (forward-looking — Stable Diffusion /
    /// Flux / Hunyuan / etc. for the portrait-variant + scene-narration lane
    /// in <c>docs/FUTURE_2035.md</c> idea #15). Default implementation derives
    /// from <see cref="ModelsDir"/>/Diffusion so it auto-tracks any operator
    /// override of the chat-model root.</summary>
    string DiffusionModelsDir => Path.Combine(ModelsDir, "Diffusion");

    string RuntimeRoot { get; }
    string TtsDir { get; }
    string PackDir { get; }
}

/// <summary>Simple three-level log sink. Adapters typically back this with
/// the host process's structured logger (Microsoft.Extensions.Logging,
/// Serilog, etc.) but a bounded in-memory ring buffer is also valid.</summary>
public interface ILogger
{
    void Info(string message);
    void Warning(string message);
    void Error(string message);
}

/// <summary>The whole game-side seam. A game adapter exposes its
/// characters, clock, paths, and log writer through this single contract
/// so the runtime never reaches into game-specific APIs directly.</summary>
public interface IGameAdapter
{
    string AdapterName { get; }
    ILogger Logger { get; }
    IWorldClock Clock { get; }
    IPathProvider Paths { get; }
    IEnumerable<ICharacter> Characters { get; }
    bool IsReadyForInference { get; }
}

/// <summary>Deterministic semantic embedder and cosine similarity.
///
/// <para>Used for conversation memory recall without a live model: a
/// hashed bag-of-tokens projection into a fixed-size float vector, plus
/// cosine similarity for ranking. Zero network calls, zero per-request
/// allocation beyond the output array. Reproducible across runs — the
/// same input text always yields the same vector on any machine.</para>
///
/// <para>This is not a substitute for a trained embedding model on large
/// corpora, but for runtime chat-memory recall at typical session sizes
/// (hundreds of entries) the recall is good enough and the latency is
/// sub-millisecond.</para>
/// </summary>
public static class SemanticEmbedder
{
    public const int VectorDimensions = 128;

    /// <summary>Hashed bag-of-tokens projection. Lower-cases, tokenises,
    /// hashes each token to a fixed-size bucket, increments the bucket,
    /// L2-normalises. Deterministic.</summary>
    public static float[] FallbackEmbed(string? text)
    {
        float[] vector = new float[VectorDimensions];
        if (string.IsNullOrWhiteSpace(text))
        {
            return vector;
        }

        ReadOnlySpan<char> source = text.AsSpan();
        int previousTokenStart = -1;
        int previousTokenLength = 0;
        int tokenCount = 0;
        int tokenStart = -1;

        for (int i = 0; i <= source.Length; i++)
        {
            bool atEnd = i == source.Length;
            bool isSeparator = atEnd || IsTokenSeparator(source[i]);
            if (!isSeparator)
            {
                if (tokenStart < 0)
                {
                    tokenStart = i;
                }

                continue;
            }

            if (tokenStart < 0)
            {
                continue;
            }

            int tokenLength = i - tokenStart;
            uint hash = StableHash(source.Slice(tokenStart, tokenLength));
            int bucket = (int)(hash % (uint)VectorDimensions);
            vector[bucket] += 1f;
            tokenCount++;

            // Bigram of adjacent tokens adds weak ordering signal without
            // blowing up the dimension.
            if (previousTokenStart >= 0)
            {
                uint bigramHash = StableBigramHash(
                    source.Slice(previousTokenStart, previousTokenLength),
                    source.Slice(tokenStart, tokenLength));
                int bigramBucket = (int)(bigramHash % (uint)VectorDimensions);
                vector[bigramBucket] += 0.5f;
            }

            previousTokenStart = tokenStart;
            previousTokenLength = tokenLength;
            tokenStart = -1;
        }

        if (tokenCount == 0)
        {
            return vector;
        }

        // L2 normalise so cosine similarity reduces to a dot product and
        // magnitude doesn't bias recall toward long text.
        double sumSq = 0;
        for (int i = 0; i < vector.Length; i++)
        {
            sumSq += vector[i] * vector[i];
        }
        if (sumSq > 0)
        {
            float inv = (float)(1.0 / Math.Sqrt(sumSq));
            for (int i = 0; i < vector.Length; i++)
            {
                vector[i] *= inv;
            }
        }
        return vector;
    }

    /// <summary>Cosine similarity on two L2-normalised vectors (== dot
    /// product in that case). Returns 0 when either is zero-magnitude,
    /// clamps to [-1, 1] to absorb floating-point drift.</summary>
    public static float CosineSimilarity(float[] a, float[] b)
    {
        if (a is null || b is null)
        {
            return 0f;
        }

        int length = Math.Min(a.Length, b.Length);
        if (length == 0)
        {
            return 0f;
        }

        double dot = 0;
        double magA = 0;
        double magB = 0;
        for (int i = 0; i < length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        if (magA <= 0 || magB <= 0)
        {
            return 0f;
        }

        double sim = dot / (Math.Sqrt(magA) * Math.Sqrt(magB));
        if (sim > 1.0) return 1f;
        if (sim < -1.0) return -1f;
        return (float)sim;
    }

    private static bool IsTokenSeparator(char value) =>
        value is ' ' or '\t' or '\n' or '\r' or '.' or ',' or ';' or ':'
            or '!' or '?' or '(' or ')' or '[' or ']' or '{' or '}' or '"'
            or '\'' or '/' or '\\' or '-' or '_' or '|';

    // FNV-1a-style stable hash. Not cryptographic; the point is that the
    // same string always hashes to the same bucket across processes and
    // machines. String.GetHashCode() is randomised per process on .NET
    // Core, so we roll our own.
    private static uint StableHash(ReadOnlySpan<char> token) =>
        StableHash(token, 2166136261u);

    private static uint StableBigramHash(ReadOnlySpan<char> first, ReadOnlySpan<char> second)
    {
        uint hash = StableHash(first, 2166136261u);
        hash ^= (uint)'\u001F';
        hash *= 16777619u;
        return StableHash(second, hash);
    }

    private static uint StableHash(ReadOnlySpan<char> token, uint seed)
    {
        uint hash = seed;
        for (int i = 0; i < token.Length; i++)
        {
            hash ^= (uint)char.ToLowerInvariant(token[i]);
            hash *= 16777619u;
        }

        return hash;
    }
}

/// <summary>Strips model-emitted reasoning / chain-of-thought tags so they
/// don't leak into the player-visible reply. Handles the common
/// &lt;think&gt;...&lt;/think&gt; pattern and several variants.</summary>
public static class ResponseCleanup
{
    // Recognised wrapper pairs. Order matters: longest / most specific
    // first so a nested "<think>...<thinking>...</thinking>...</think>"
    // doesn't confuse the unwrapper.
    private static readonly (string Open, string Close)[] ReasoningPairs =
    [
        ("<think>", "</think>"),
        ("<thinking>", "</thinking>"),
        ("<reasoning>", "</reasoning>"),
        ("<reflection>", "</reflection>"),
        ("<scratchpad>", "</scratchpad>"),
    ];

    public static string StripReasoning(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        string text = input!;
        // Repeat until no more pairs strip — one call handles all
        // nesting levels without a regex.
        bool changed;
        do
        {
            changed = false;
            foreach ((string open, string close) in ReasoningPairs)
            {
                int start = text.IndexOf(open, StringComparison.OrdinalIgnoreCase);
                if (start < 0) continue;
                int end = text.IndexOf(close, start + open.Length, StringComparison.OrdinalIgnoreCase);
                if (end < 0)
                {
                    // Unclosed opener: trim from the opener to the end.
                    text = text[..start].TrimEnd();
                    changed = true;
                    continue;
                }
                text = text[..start] + text[(end + close.Length)..];
                changed = true;
            }
        } while (changed);

        return text.Trim();
    }
}
