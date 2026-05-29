using PalLLM.Domain.Memory;
using PalLLM.Domain.Portable;

namespace PalLLM.Tests;

public sealed class ConversationMemoryStoreTests
{
    [Test]
    public void GetRecent_ReturnsNewestMatchingEntriesInReverseChronologicalOrder()
    {
        var store = new ConversationMemoryStore();
        DateTimeOffset start = new(2026, 4, 22, 12, 0, 0, TimeSpan.Zero);

        store.Import(
        [
            CreateEntry(1, "CampScout", "older personal note", start.AddMinutes(1), importance: 0.2f),
            CreateEntry(2, "SpeciesAlpha", "other character note", start.AddMinutes(2), importance: 0.4f),
            CreateEntry(null, "System", "shared camp notice", start.AddMinutes(3), importance: 0.3f),
            CreateEntry(1, "CampScout", "newest personal note", start.AddMinutes(4), importance: 0.8f),
        ]);

        IReadOnlyList<ConversationMemoryEntry> recent = store.GetRecent(3, characterId: 1);

        Assert.That(recent.Select(entry => entry.Content), Is.EqualTo(new[]
        {
            "newest personal note",
            "shared camp notice",
            "older personal note",
        }));
    }

    [Test]
    public void AccumulatedImportance_SumsMostRecentMatchingWindow()
    {
        var store = new ConversationMemoryStore();
        DateTimeOffset start = new(2026, 4, 22, 12, 0, 0, TimeSpan.Zero);

        store.Import(
        [
            CreateEntry(1, "CampScout", "first personal note", start.AddMinutes(1), importance: 0.2f),
            CreateEntry(2, "SpeciesAlpha", "other character note", start.AddMinutes(2), importance: 0.9f),
            CreateEntry(null, "System", "shared camp notice", start.AddMinutes(3), importance: 0.4f),
            CreateEntry(1, "CampScout", "latest personal note", start.AddMinutes(4), importance: 0.7f),
        ]);

        float importance = store.AccumulatedImportance(2, characterId: 1);

        Assert.That(importance, Is.EqualTo(1.1f).Within(0.0001f));
    }

    [Test]
    public void Recall_RespectsLimitAndKeepsHighestScoringMatches()
    {
        var store = new ConversationMemoryStore();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        float[] campfireEmbedding = SemanticEmbedder.FallbackEmbed("campfire");
        float[] forgeEmbedding = SemanticEmbedder.FallbackEmbed("forge");

        Assert.That(
            SemanticEmbedder.CosineSimilarity(
                SemanticEmbedder.FallbackEmbed("Campfire Route"),
                SemanticEmbedder.FallbackEmbed("campfire route")),
            Is.EqualTo(1.0f).Within(0.0001f),
            "Fallback embeddings must stay deterministic and case-insensitive.");
        Assert.That(
            SemanticEmbedder.CosineSimilarity(
                SemanticEmbedder.FallbackEmbed("campfire route forge"),
                SemanticEmbedder.FallbackEmbed("forge route campfire")),
            Is.LessThan(1.0f),
            "Adjacent-token bigrams should preserve weak ordering signal.");

        store.Import(
        [
            CreateEntry(7, "CampScout", "older campfire plan", now.AddHours(-48), importance: 0.9f, embedding: campfireEmbedding),
            CreateEntry(9, "SpeciesAlpha", "forge work order", now.AddMinutes(-5), importance: 1.0f, embedding: forgeEmbedding),
            CreateEntry(7, "CampScout", "fresh campfire route", now.AddMinutes(-1), importance: 0.4f, embedding: campfireEmbedding),
        ]);

        IReadOnlyList<ConversationMemoryMatch> matches = store.Recall("campfire", characterId: 7, limit: 2);

        Assert.That(matches, Has.Count.EqualTo(2));
        Assert.That(matches.Select(match => match.Entry.Content), Is.EqualTo(new[]
        {
            "fresh campfire route",
            "older campfire plan",
        }));
        Assert.That(matches.All(match => match.Entry.Content != "forge work order"), Is.True);

        var lexicalStore = new ConversationMemoryStore();
        float[] sharedEmbedding = SemanticEmbedder.FallbackEmbed("shared retrieval bucket");
        DateTimeOffset tieTime = now.AddMinutes(-10);
        lexicalStore.Import(
        [
            CreateEntry(7, "CampScout", "generic camp chore with the same vector", tieTime, importance: 0.5f, embedding: sharedEmbedding),
            CreateEntry(7, "CampScout", "raider faction ambush at the north ridge", tieTime, importance: 0.5f, embedding: sharedEmbedding),
        ]);

        IReadOnlyList<ConversationMemoryMatch> lexicalMatches =
            lexicalStore.Recall("raider faction", characterId: 7, limit: 1);

        Assert.That(lexicalMatches.Single().Entry.Content, Is.EqualTo("raider faction ambush at the north ridge"),
            "Exact query-token overlap should rerank tied embedding candidates without changing memory-store determinism.");
    }

    private static ConversationMemoryEntry CreateEntry(
        int? characterId,
        string characterName,
        string content,
        DateTimeOffset createdAtUtc,
        float importance,
        float[]? embedding = null) =>
        new()
        {
            CharacterId = characterId,
            CharacterName = characterName,
            SpeakerRole = "user",
            Content = content,
            Tags = ["chat"],
            CreatedAtUtc = createdAtUtc,
            Embedding = embedding ?? SemanticEmbedder.FallbackEmbed(content),
            Importance = importance,
        };
}
