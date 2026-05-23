# Prompt cards — 19 demo moments that show the companion alive

Last audited: `2026-05-10`

Nineteen curated prompt cards, one per deterministic fallback
strategy. Each card is a tiny scene a player might find
themselves in, the prompt that triggers that family of reply,
the *shape* of reply you should expect, and the design moment
that makes it feel companion-like instead of chatbot-like.

These work **without any LLM model configured**. The
deterministic fallback director already produces the replies;
inference, when enabled, layers richer voice on top of the same
shape.

> **30-second tour:** run `pal demo` for a self-running
> walk-through of six representative cards. Or run a card
> manually with `pal hello "<prompt>"`.

The 19 strategies live in
`src/PalLLM.Domain/Runtime/FallbackBehaviorEngine.cs`. Open the
file and search for `Try_<name>` to see how each one decides
when to fire and what scene cues it weights.

## How to read a card

```
[scene]            One-line setup: where the player is, what just happened.
[try this]         The prompt to send via dashboard chat / pal hello / Lua bridge.
[expect]           A reply shaped like this. Voice will vary; structure is deterministic.
[strategy]         Try_<name> in FallbackBehaviorEngine.cs.
[why it's fun]     The design moment that makes this land as companion, not chatbot.
```

---

## 1. Hero moment — when the player saved the day

```
[scene]    The player just pulled a clutch revival under fire.
[try this] "I think I just survived that."
[expect]   Acknowledgement that names the specific play, not generic praise.
[strategy] Try_HeroMoment
[why it's fun]
  Generic AI says "wow nice job!" The hero-moment director references the
  fight detail it has from the world snapshot - the pal that fell, the
  spot the player rallied from. It feels like the companion was actually
  watching.
```

## 2. Emergency triage — the room is on fire

```
[scene]    Combat just kicked off, multiple hostiles, low HP.
[try this] "Three Bushi just dropped on us, low health, what now?"
[expect]   Concrete first move + cover suggestion + heal-window cue.
[strategy] Try_EmergencyTriage
[why it's fun]
  Most game-companions in this moment say "be careful!" This director
  picks the actual cover object near you and names the heal window in
  the enemy's combo. Feels like a buddy who has done this fight before.
```

## 3. Retreat and rally — disengage cleanly

```
[scene]    Engagement turned bad; player needs out.
[try this] "We have to bail. I'm at 12%."
[expect]   Pulled-back call: which direction breaks line-of-sight, who covers.
[strategy] Try_RetreatAndRally
[why it's fun]
  The director chooses retreat - not "fight harder." Companion AIs that
  always cheerlead toward fights feel hollow. Acknowledging "yes, run"
  is half of why a real teammate is calming.
```

## 4. Stealth shadow — quiet approach

```
[scene]    Player crouching toward a patrol they want to avoid.
[try this] "I want to slip past without a fight."
[expect]   Quiet voice; sight-line and noise notes; no "let's just attack" pivot.
[strategy] Try_StealthShadow
[why it's fun]
  The director honors the player's stated intent. Many game NPCs override
  with their preferred strategy. This one shuts up and helps you sneak.
```

## 5. Nemesis counterplay — recurring opponent

```
[scene]    A boss or rival the player has fought before reappears.
[try this] "It's that alpha Anubis again. Same one I lost to last time."
[expect]   Memory callback: what went wrong before, what's different now.
[strategy] Try_NemesisCounterplay
[why it's fun]
  The relationship tracker + memory store actually surface "you fought
  this thing on day 3 and got dropped at the bridge." Specific recall
  is the moment people go "wait, the AI remembered?"
```

## 6. Buddy overwatch — co-op flow

```
[scene]    Player and a pal are both engaged, separately.
[try this] "Help me time this. You take the right one, I take the left."
[expect]   Confirmation + sync cue + bail-out condition.
[strategy] Try_BuddyOverwatch
[why it's fun]
  The director treats the pal as an actual teammate, not a tool. Talks
  about the play in "we" language, names a single condition that means
  abort, doesn't drown the moment in commentary.
```

## 7. Perimeter lockdown — base under threat

```
[scene]    Hostiles are sweeping toward the base.
[try this] "Something's coming through the ridge. Can you read the line?"
[expect]   Choke point named, two pal duties assigned, fallback if breached.
[strategy] Try_PerimeterLockdown
[why it's fun]
  The director uses base topology - the actual ridge name, the specific
  workbench you'd hide behind. Generic AI generates "set up defenses!"
  This one tells you which gate to brace.
```

## 8. Base network — coordinated production

```
[scene]    Player is hopping between bases, juggling work orders.
[try this] "Base 2 is starving for charcoal. Base 3 is over on stone."
[expect]   Move suggestion + which pal to reassign + ETA.
[strategy] Try_BaseNetwork
[why it's fun]
  The companion treats your bases as an actual logistics network rather
  than discrete buildings. This is the kind of detail that makes a
  trained-companion app feel like a strategist, not a help text.
```

## 9. Safe travel — long-distance route plan

```
[scene]    Player is about to cross a long stretch of map.
[try this] "Going from camp to the volcano peak. What should I bring?"
[expect]   Stamina + climate + bandit-zone callout, one item recommendation.
[strategy] Try_SafeTravel
[why it's fun]
  The recommendation is shaped to the *current* world state - not a
  generic packing list. If it's raining, the cue mentions warmth. If
  the volcano biome is in the world snapshot, heat resistance is named.
```

## 10. Capture window — the alpha is low

```
[scene]    A boss-rank pal has dropped to capture range.
[try this] "Big alpha cooled to 14% HP - is this the moment?"
[expect]   Yes/no with conditional ("yes, but break LOS first") + sphere reminder.
[strategy] Try_CaptureWindow
[why it's fun]
  The director respects the actual capture mechanic. Knows that desperate
  pals cast wider AoE; knows that LOS-break resets that. "Yes, but..."
  feels like a friend who plays the same game.
```

## 11. Objective push — back on the main quest

```
[scene]    Player is wandering; companion knows the next milestone.
[try this] "I forgot what I was doing. Where was I going?"
[expect]   Reminder of current objective + the smallest next step.
[strategy] Try_ObjectivePush
[why it's fun]
  Companion remembers the *thread* across sessions. Not "you have 47
  open quests" - "you were headed to the Anubis tower. The cooling
  drink expired so swap that first."
```

## 12. Crafting discipline — too many things to make

```
[scene]    Forge cold, several work orders queued, weather inbound.
[try this] "The forge is cold and rain is rolling in. Help me prioritize."
[expect]   Ordered list of two or three actions; named reason for each.
[strategy] Try_CraftingDiscipline
[why it's fun]
  Most game-AIs say "craft what you need!" This one prioritizes by
  weather - "rain steals heat" - and names what *can* wait. Companion
  feels like it's read the situation, not the menu.
```

## 13. Harvest window — material run

```
[scene]    A specific resource is plentiful right now and won't be later.
[try this] "Berries everywhere this morning. Worth a sweep before raids?"
[expect]   Affirmative + route through the densest spots + return cue.
[strategy] Try_HarvestWindow
[why it's fun]
  The "while it's fresh" framing - the recommendation is *time-bound*,
  not just a static list of "good things to gather." It feels like the
  companion noticed an opportunity, not recited a wiki.
```

## 14. Weather shelter — storm rolling in

```
[scene]    A storm or extreme-weather event is starting.
[try this] "Sky's turning. We out in the open?"
[expect]   Nearest shelter + ETA + which pals to recall first.
[strategy] Try_WeatherShelter
[why it's fun]
  Treats weather as a real game mechanic the companion *cares* about,
  not flavor text. Naming the shelter and the recall order makes the
  pal feel like it's thinking about the survival surface, not the chat.
```

## 15. Exploration sweep — quiet curiosity

```
[scene]    Mid-morning, no urgent task, the player asks about a far ridge.
[try this] "I see a ridgeline north of base. Worth the detour?"
[expect]   Worth/not-worth + specific reason + retracing tip.
[strategy] Try_ExplorationSweep
[why it's fun]
  The reason is *spatial*, not generic ("might find loot"). Mentions
  the line of sight, the updraft, the safe path back. This is the
  shape of reply that makes a player want to explore *with* the
  companion, not just be told to.
```

## 16. Morale rally — after a loss

```
[scene]    Player just lost pals or a base. Wants to talk, not strategize.
[try this] "Lost two pals out there. I just need to sit by the fire for a minute."
[expect]   Quiet acknowledgment, no plan-pushing, hint that the next move can wait.
[strategy] Try_MoraleRally
[why it's fun]
  This is the "I see you" moment. Most NPC dialogue would launch into
  consolation-then-strategy. The morale-rally director just sits with
  the player. Companion-AI gold.
```

## 17. Recover window — between fights

```
[scene]    Player has a few quiet minutes before the next event.
[try this] "We have maybe 10 minutes before the raid timer. What should I do?"
[expect]   Two or three small actions that fit the window precisely.
[strategy] Try_RecoverWindow
[why it's fun]
  The director respects the *specific* time budget. Not "rest up!" -
  "in 10 minutes you can refill water, recall the ranger, and re-arm.
  The forge is too slow." Real teammate vibe.
```

## 18. Ambient camp — quiet morning

```
[scene]    First contact of the day; player checking in.
[try this] "Hey - quiet morning so far. How are you settling in?"
[expect]   In-character voice, no urgent push, one observed detail.
[strategy] Try_AmbientCamp
[why it's fun]
  The only goal here is to *sound like* the character. The director
  references something it actually saw in the world snapshot ("cooling
  rack ticking near dawn") rather than greeting-AI generic. The
  companion has a *morning*, not a script.
```

## 19. General director - when nothing specialized fits

```
[scene]    The player asks a broad, underspecified question in a calm or mixed situation.
[try this] "What should we do next?"
[expect]   One grounded next move, a local reason, and a fallback if the read changes.
[strategy] CreateGeneralDirector
[why it's fun]
  This is the safety net that keeps the companion useful even when the
  prompt misses every sharper family. It avoids fake omniscience, keeps
  the answer local to the current situation, and turns "I don't know"
  into one practical step.
```

---

## How to use these cards

### As a player

Try one a day. They cover the full range of moments PalLLM
shapes itself to. After a week of playing, you'll have hit
every fallback family at least once.

### As a designer or harvester

These cards double as a **specification** for the companion's
emotional surface. If you're lifting the fallback director into
another game, the 19 cards tell you what each strategy is
*actually for* in player-experience terms.

### As a tester

If you make a runtime change and want to confirm you didn't
break the personality, pick three cards across different
families and run `pal hello "<prompt>"` for each. The
`ResponsePath` should match the card's `[strategy]` line.

## What's next on the fun trajectory

These 19 are the deterministic baseline. The real fun gets
unlocked when:

1. **A live LLM endpoint** is wired (see `pal models` for the
   recommendation per your hardware) — same shape, richer voice.
2. **Personality packs** are loaded — Chillet's calm voice,
   anxious-crafter's worry, stoic-veteran's drawl. Same 19
   strategies wear different hats.
3. **Memory accumulates** — by week 2, the nemesis-counterplay
   card lands harder because the companion has *actually*
   logged the fight on day 3.
4. **Native HUD binding** lands (`docs/IMPLEMENTATION_QUEUE.md`
   queues 3-5) — the companion speaks through a proper subtitle
   surface, not generic chat. *That* is when these cards stop
   being fun-on-paper and start being fun-in-game.

## Related

- [`READINESS.md`](READINESS.md) - candid 10/10 scorecard;
  "Fun / personality" is one of the 22 aspects scored
- [`MENTAL_MODEL.md`](MENTAL_MODEL.md) - "the runtime is a
  companion, not a chatbot" - the design choice every card
  inherits
- [`FALLBACK_AI_RESEARCH.md`](FALLBACK_AI_RESEARCH.md) - the
  game-AI research the 19 strategies are built on
- [`PACK_AUTHORING.md`](PACK_AUTHORING.md) - how to write a
  personality pack so the cards land in your character's voice
- `src/PalLLM.Domain/Runtime/FallbackBehaviorEngine.cs` - the
  source of truth for every strategy named here
