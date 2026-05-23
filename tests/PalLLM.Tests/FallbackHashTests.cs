using PalLLM.Domain.Integration;
using PalLLM.Domain.Runtime;

namespace PalLLM.Tests;

// Pass 300 - direct unit tests for the FNV-1a hash + composite seed helper
// used by the fallback director for deterministic strategy selection and
// variant picking. The hash is load-bearing: every fallback response is
// chosen from a strategy's variant list using
// `PositiveModulo(seed, variants.Length)`, so a regression that shifts
// the modulo (or makes the hash non-deterministic, or breaks
// case-insensitivity) would silently change which fallback line a given
// player input maps to — observable to a player as "the companion
// suddenly says a different thing for the same prompt."
//
// Until this pass the helper was only covered indirectly via the
// fallback engine's per-strategy tests. The 3 public functions
// (`OfString`, `Seed`, `PositiveModulo`) and their edge cases (empty,
// case-insensitivity, null character snapshot, negative dividend) had no
// direct fast-feedback coverage.
public sealed class FallbackHashTests
{
    // ---------- OfString: determinism + case-insensitivity ----------

    [Test]
    public void OfString_SameInput_ProducesSameHash()
    {
        // Determinism: hashing the same value twice produces the same int.
        // Capture once into a local so the NUnit analyzer doesn't flag this
        // as a tautology (NUnit2009); the test is still verifying that
        // separate invocations agree.
        int first = FallbackHash.OfString("hello pal");
        int second = FallbackHash.OfString("hello pal");

        Assert.That(second, Is.EqualTo(first));
    }

    [TestCase("hello pal", "HELLO PAL")]
    [TestCase("MiXeD CaSe", "mixed case")]
    [TestCase("åéîøü", "ÅÉÎØÜ")]
    public void OfString_CaseInsensitive_ProducesSameHash(string lower, string upper)
    {
        Assert.That(FallbackHash.OfString(lower),
            Is.EqualTo(FallbackHash.OfString(upper)),
            $"Case-insensitivity violated: '{lower}' vs '{upper}'.");
    }

    [Test]
    public void OfString_DifferentInputs_ProduceDifferentHashes()
    {
        // FNV-1a is not collision-free in theory, but for these distinct
        // short inputs the hashes differ — a regression that made the
        // hash always return the same value would fail this.
        int a = FallbackHash.OfString("a");
        int b = FallbackHash.OfString("b");
        int hello = FallbackHash.OfString("hello");
        int world = FallbackHash.OfString("world");

        Assert.That(a, Is.Not.EqualTo(b));
        Assert.That(hello, Is.Not.EqualTo(world));
        Assert.That(a, Is.Not.EqualTo(hello));
    }

    // ---------- OfString: empty / null ----------

    [TestCase("")]
    [TestCase(null)]
    public void OfString_EmptyOrNull_ReturnsOffsetBasis(string? input)
    {
        // Per FNV-1a spec, the empty-input hash is the offset basis. The
        // implementation returns 2166136261 (unchecked cast to int).
        int expected = unchecked((int)2166136261);

        Assert.That(FallbackHash.OfString(input!), Is.EqualTo(expected));
    }

    [Test]
    public void OfString_SingleCharacter_IsDeterministicAndDistinct()
    {
        // For single-character inputs the hash should be deterministic
        // and distinct from the empty-input offset basis.
        int empty = FallbackHash.OfString(string.Empty);
        int hashA = FallbackHash.OfString("a");

        Assert.That(hashA, Is.Not.EqualTo(empty));
        Assert.That(FallbackHash.OfString("a"), Is.EqualTo(hashA));
    }

    // ---------- Seed: composite of request + character + snapshot ----------

    [Test]
    public void Seed_DeterministicForSameInputs()
    {
        var request = NewRequest("hi", taskTag: "player_chat", characterName: "Pal-A");
        var character = NewCharacter("Pal-A");
        var snapshot = NewSnapshot(world: "Palpagos", objective: "find shards");

        int first = FallbackHash.Seed(request, character, snapshot);
        int second = FallbackHash.Seed(request, character, snapshot);

        Assert.That(first, Is.EqualTo(second));
    }

    [Test]
    public void Seed_TrimsUserMessageWhitespace()
    {
        // Leading/trailing whitespace must not change the seed — a player
        // who pasted with stray whitespace should still get the same
        // fallback variant as one who typed cleanly.
        var character = NewCharacter("Pal-A");
        var snapshot = NewSnapshot(world: "Palpagos");

        int trimmed = FallbackHash.Seed(NewRequest("hi"), character, snapshot);
        int padded = FallbackHash.Seed(NewRequest("   hi   "), character, snapshot);

        Assert.That(padded, Is.EqualTo(trimmed));
    }

    [Test]
    public void Seed_DifferentTaskTag_DifferentSeed()
    {
        var character = NewCharacter("Pal-A");
        var snapshot = NewSnapshot(world: "Palpagos");

        int chat = FallbackHash.Seed(
            NewRequest("hi", taskTag: "player_chat"), character, snapshot);
        int proof = FallbackHash.Seed(
            NewRequest("hi", taskTag: "proof_replay"), character, snapshot);

        Assert.That(chat, Is.Not.EqualTo(proof));
    }

    [Test]
    public void Seed_NullCharacter_FallsBackToRequestCharacterName()
    {
        var snapshot = NewSnapshot(world: "Palpagos");

        // When character snapshot is null but request has a CharacterName,
        // the seed uses the request's CharacterName.
        int viaRequest = FallbackHash.Seed(
            NewRequest("hi", characterName: "FromRequest"),
            character: null,
            snapshot);

        // Same name on the character snapshot must produce the same seed.
        int viaCharacter = FallbackHash.Seed(
            NewRequest("hi", characterName: string.Empty),
            character: NewCharacter("FromRequest"),
            snapshot);

        Assert.That(viaRequest, Is.EqualTo(viaCharacter),
            "Character name from snapshot or from request must be interchangeable.");
    }

    [Test]
    public void Seed_EmptyCharacterNameOnRequest_IsForwardedAsEmpty()
    {
        // ChatRequest.CharacterName defaults to string.Empty (not null), so
        // the `?? "Palworld"` fallback inside Seed only fires when the
        // value is actually null. With a default request, the character
        // name slot in the seed is the empty string — which is deterministic
        // and distinct from any non-empty name.
        var snapshot = NewSnapshot(world: "Palpagos");

        int empty = FallbackHash.Seed(
            NewRequest("hi", characterName: string.Empty),
            character: null,
            snapshot);

        int withName = FallbackHash.Seed(
            NewRequest("hi", characterName: "Pal-A"),
            character: null,
            snapshot);

        Assert.That(empty, Is.Not.EqualTo(withName),
            "Empty CharacterName must produce a distinct seed from a named character.");

        // Determinism: repeated calls with the same empty-name request give
        // the same seed.
        int emptyAgain = FallbackHash.Seed(
            NewRequest("hi", characterName: string.Empty),
            character: null,
            snapshot);
        Assert.That(empty, Is.EqualTo(emptyAgain));
    }

    [Test]
    public void Seed_DifferentWorldName_DifferentSeed()
    {
        var request = NewRequest("hi");
        var character = NewCharacter("Pal-A");

        int worldA = FallbackHash.Seed(request, character, NewSnapshot(world: "Palpagos"));
        int worldB = FallbackHash.Seed(request, character, NewSnapshot(world: "Sakurajima"));

        Assert.That(worldA, Is.Not.EqualTo(worldB));
    }

    [Test]
    public void Seed_DifferentObjective_DifferentSeed()
    {
        var request = NewRequest("hi");
        var character = NewCharacter("Pal-A");

        int objA = FallbackHash.Seed(request, character,
            NewSnapshot(world: "Palpagos", objective: "find shards"));
        int objB = FallbackHash.Seed(request, character,
            NewSnapshot(world: "Palpagos", objective: "rescue Zoe"));

        Assert.That(objA, Is.Not.EqualTo(objB));
    }

    // ---------- PositiveModulo ----------

    [TestCase(0, 5, 0)]
    [TestCase(1, 5, 1)]
    [TestCase(4, 5, 4)]
    [TestCase(5, 5, 0)]
    [TestCase(7, 5, 2)]
    [TestCase(10, 5, 0)]
    public void PositiveModulo_NonNegativeDividend_ReturnsStandardModulo(int value, int modulo, int expected)
    {
        Assert.That(FallbackHash.PositiveModulo(value, modulo), Is.EqualTo(expected));
    }

    [TestCase(-1, 5, 4)]
    [TestCase(-2, 5, 3)]
    [TestCase(-5, 5, 0)]
    [TestCase(-6, 5, 4)]
    [TestCase(-10, 5, 0)]
    public void PositiveModulo_NegativeDividend_ReturnsNonNegativeResult(int value, int modulo, int expected)
    {
        // The whole point of `PositiveModulo`: C# `%` returns the sign of
        // the dividend, but we need a stable array-index. A regression that
        // returned `result` directly without the `+ modulo` correction
        // would land here.
        Assert.That(FallbackHash.PositiveModulo(value, modulo), Is.EqualTo(expected));
    }

    [Test]
    public void PositiveModulo_HashIntoVariantArray_StaysInBounds()
    {
        // The most common production use of `PositiveModulo`: hash a
        // string, take it modulo the variant-array length, and use it as
        // an index. This must always land in `[0, length)`.
        string[] inputs =
        [
            "hello",
            "different",
            "another input",
            "edge",
            string.Empty,
            "🎮 emoji test",
        ];
        int modulo = 7;

        foreach (string input in inputs)
        {
            int hash = FallbackHash.OfString(input);
            int index = FallbackHash.PositiveModulo(hash, modulo);

            Assert.That(index, Is.GreaterThanOrEqualTo(0),
                $"Index for '{input}' must be non-negative; got {index}.");
            Assert.That(index, Is.LessThan(modulo),
                $"Index for '{input}' must be < {modulo}; got {index}.");
        }
    }

    // ---------- Helpers ----------

    private static ChatRequest NewRequest(
        string message,
        string taskTag = "player_chat",
        string characterName = "Pal-A") => new()
    {
        UserMessage = message,
        TaskTag = taskTag,
        CharacterName = characterName,
    };

    private static GameCharacterSnapshot NewCharacter(string displayName) => new()
    {
        DisplayName = displayName,
    };

    private static GameWorldSnapshot NewSnapshot(
        string world = "Palpagos",
        string objective = "") => new()
    {
        WorldName = world,
        CurrentObjective = objective,
    };
}
