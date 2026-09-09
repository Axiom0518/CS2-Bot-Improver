using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;

namespace BotState;

// P3c — perception memory (humanlike).
// Hearing uses the same Valve player_sound Radius players get (Alt noise circle).
// No hardcoded footstep/gun ranges. Decisions + BotIntelHub consume this for P4.
public partial class BotState
{
    private enum BeliefKind : byte
    {
        Seen = 0,
        Heard = 1,
        Damage = 2,
    }

    private struct BeliefSpot
    {
        public float X, Y, Z;
        public float Radius;
        public float CreatedAt;
        public float ExpiresAt;
        public float Confidence;
        public BeliefKind Kind;
        public bool Active;
    }

    private readonly Dictionary<int, BeliefSpot[]> _beliefs = new();

    // Memory windows after a legal hear (engine already gated who can hear).
    // Duration comes from player_sound; these clamp human short-term retention.
    private const float BeliefSeenTtl = 5.0f;
    private const float BeliefHeardMemMin = 2.0f;
    private const float BeliefHeardMemMax = 5.0f;
    private const float BeliefDamageTtl = 2.5f;
    private const float BeliefSeenRadius = 48f;
    private const float BeliefDamageRadius = 240f;
    private const float BeliefDamageLookDist = 400f;
    // Teammate death is game-sense / callout, not engine audio.
    private const float BeliefDeathCalloutRange = 1400f;
    private const float BeliefLookMaxDist = 1100f;
    private const float BeliefMergeDist = 320f;
    // Native NoisePosition fallback when no fresh player_sound merge.
    private const float BeliefNativeNoiseFuzzy = 150f;
    private const float BeliefNativeNoiseTtl = 3.0f;

    private bool _debugBelief;
    private float _debugNextSummaryAt;
    private int _debugEventBudget; // remaining event lines until next summary window
    private const float DebugSummaryInterval = 2.0f;
    private const int DebugEventsPerWindow = 4;

    private void ClearBeliefState()
    {
        _beliefs.Clear();
        BotIntelHub.ClearAll();
    }

    private BeliefSpot[] GetBeliefSlots(int botIndex)
    {
        if (!_beliefs.TryGetValue(botIndex, out var slots) || slots == null || slots.Length < 3)
        {
            slots = new BeliefSpot[3];
            _beliefs[botIndex] = slots;
        }
        return slots;
    }

    private void UpsertBelief(int botIndex, BeliefKind kind, float x, float y, float z,
        float radius, float ttl, float confidence, float now, bool allowMerge = true)
    {
        var slots = GetBeliefSlots(botIndex);
        int i = (int)kind;
        ref var spot = ref slots[i];

        bool wasActive = spot.Active && spot.ExpiresAt > now;
        bool merged = false;

        if (wasActive && allowMerge && kind == BeliefKind.Heard)
        {
            float mdx = x - spot.X;
            float mdy = y - spot.Y;
            float mdz = z - spot.Z;
            if (mdx * mdx + mdy * mdy + mdz * mdz < BeliefMergeDist * BeliefMergeDist)
            {
                // Same contact — blend, refresh clock (human updating "still there").
                spot.X = spot.X * 0.65f + x * 0.35f;
                spot.Y = spot.Y * 0.65f + y * 0.35f;
                spot.Z = spot.Z * 0.65f + z * 0.35f;
                spot.Radius = Math.Max(spot.Radius * 0.9f, radius);
                spot.CreatedAt = now;
                spot.ExpiresAt = now + ttl;
                spot.Confidence = Math.Clamp(Math.Max(spot.Confidence, confidence), 0.05f, 1f);
                merged = true;
            }
        }

        if (!merged)
        {
            if (wasActive && kind != BeliefKind.Damage
                && spot.Confidence > confidence + 0.20f
                && spot.ExpiresAt - now > ttl * 0.45f)
                return;

            spot.X = x;
            spot.Y = y;
            spot.Z = z;
            spot.Radius = radius;
            spot.CreatedAt = now;
            spot.ExpiresAt = now + ttl;
            spot.Confidence = Math.Clamp(confidence, 0.05f, 1f);
            spot.Kind = kind;
            spot.Active = true;
        }

        slots[i] = spot;

        if (_debugBelief && !merged)
            DebugBeliefEvent(botIndex, spot, wasActive ? "refresh" : "new");
    }

    private void ExpireBeliefs(int botIndex, float now)
    {
        if (!_beliefs.TryGetValue(botIndex, out var slots)) return;
        for (int i = 0; i < slots.Length; i++)
        {
            if (!slots[i].Active) continue;
            if (now < slots[i].ExpiresAt) continue;
            if (_debugBelief)
                DebugBeliefEvent(botIndex, slots[i], "expire");
            slots[i].Active = false;
        }
    }

    private static float BeliefEffectiveRadius(in BeliefSpot spot, float now)
    {
        if (!spot.Active) return 0f;
        float age = Math.Max(0f, now - spot.CreatedAt);
        float grow = spot.Kind switch
        {
            BeliefKind.Heard => age * 45f,
            BeliefKind.Seen => age * 28f,
            _ => age * 18f,
        };
        return spot.Radius + grow;
    }

    private bool TryGetBestBelief(
        int botIndex, float now,
        out BeliefSpot best, out float effectiveRadius)
    {
        best = default;
        effectiveRadius = 0f;
        ExpireBeliefs(botIndex, now);
        if (!_beliefs.TryGetValue(botIndex, out var slots))
            return false;

        float bestScore = 0f;
        bool found = false;
        foreach (var spot in slots)
        {
            if (!spot.Active || now >= spot.ExpiresAt) continue;
            float life = (spot.ExpiresAt - now) / Math.Max(0.1f, spot.ExpiresAt - spot.CreatedAt);
            float kindBias = spot.Kind switch
            {
                BeliefKind.Damage => 1.30f,
                BeliefKind.Heard => 1.15f,
                _ => 1.00f,
            };
            float score = spot.Confidence * life * kindBias;
            if (!found || score > bestScore)
            {
                bestScore = score;
                best = spot;
                found = true;
            }
        }

        if (!found) return false;
        effectiveRadius = BeliefEffectiveRadius(best, now);
        return true;
    }

    // Used by decision + geometric fallback look.
    private bool TryGetBeliefLookTarget(
        int botIndex, float eyeX, float eyeY, float eyeZ, float now,
        out float lookX, out float lookY, out float lookZ, out BeliefKind kind)
    {
        lookX = lookY = lookZ = 0f;
        kind = BeliefKind.Seen;
        if (!TryGetBestBelief(botIndex, now, out var chosen, out _))
            return false;

        float dx = chosen.X - eyeX;
        float dy = chosen.Y - eyeY;
        float dz = chosen.Z - eyeZ;
        float dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        if (dist < 1f || dist > BeliefLookMaxDist)
            return false;

        lookX = chosen.X;
        lookY = chosen.Y;
        lookZ = chosen.Z;
        kind = chosen.Kind;
        return true;
    }

    private void PublishIntelSnapshot(int botIndex, float now)
    {
        if (!TryGetBestBelief(botIndex, now, out var best, out float radius))
        {
            BotIntelHub.Clear(botIndex);
            return;
        }

        BotIntelHub.Publish(botIndex, new BotIntelHub.Snapshot
        {
            Active = true,
            X = best.X,
            Y = best.Y,
            Z = best.Z,
            Radius = radius,
            Confidence = best.Confidence,
            ExpiresAt = best.ExpiresAt,
            UpdatedAt = now,
            Kind = (int)best.Kind,
        });
    }

    // ── Perception sources ─────────────────────────────────────

    private void UpdateVisibleBelief(CCSPlayerController botPlayer, CCSPlayerPawn pawn, CCSBot bot, float now)
    {
        if (!bot.IsEnemyVisible) return;

        CCSPlayerPawn? enemyPawn = null;
        try
        {
            var enemyHandle = bot.Enemy;
            if (enemyHandle.IsValid)
                enemyPawn = enemyHandle.Value;
        }
        catch { }

        if (enemyPawn == null || !enemyPawn.IsValid || enemyPawn.AbsOrigin == null)
            return; // never fall back to nearest-through-walls

        var pos = enemyPawn.AbsOrigin!;
        UpsertBelief((int)botPlayer.Index, BeliefKind.Seen,
            pos.X, pos.Y, pos.Z + 40f,
            BeliefSeenRadius, BeliefSeenTtl, 0.95f, now, allowMerge: true);
    }

    private void UpdateNativeNoiseBelief(CCSPlayerController botPlayer, CCSBot bot, float now)
    {
        try
        {
            float noiseTs = bot.NoiseTimestamp;
            if (noiseTs <= 0f || now - noiseTs > 1.25f) return;
            var noise = bot.NoisePosition;
            if (noise == null) return;

            int botIndex = (int)botPlayer.Index;
            var slots = GetBeliefSlots(botIndex);
            ref var heard = ref slots[(int)BeliefKind.Heard];
            // FeedNativeNoise already stores an eye-band world Z. Re-ingesting
            // that with +32 every tick drifts heard beliefs upward. Native
            // OnAudibleEvent (feet origin) is fallback only when the slot is empty.
            if (heard.Active && heard.ExpiresAt > now)
                return;

            UpsertBelief(botIndex, BeliefKind.Heard,
                noise.X, noise.Y, noise.Z + 32f,
                BeliefNativeNoiseFuzzy, BeliefNativeNoiseTtl, 0.65f, now);
        }
        catch { }
    }

    // Same audible rules as players: trust Valve player_sound Radius (Alt-circle).
    // Step=true → footsteps; Step=false → gun / land / other. Silent walk simply
    // never emits (or Radius<=0) — we invent no fake hear range.
    private HookResult OnPlayerSoundBelief(EventPlayerSound @event, GameEventInfo info)
    {
        try
        {
            return OnPlayerSoundBeliefCore(@event);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Smarter-Bot] OnPlayerSoundBelief failed (sound ignored)");
            return HookResult.Continue;
        }
    }

    private HookResult OnPlayerSoundBeliefCore(EventPlayerSound @event)
    {
        if (!HumanlikeMode.Enabled) return HookResult.Continue;

        var source = @event.Userid;
        if (source == null || !source.IsValid || !source.PawnIsAlive) return HookResult.Continue;

        int engineRadius = @event.Radius;
        float engineDuration = @event.Duration;
        if (engineRadius <= 0) return HookResult.Continue;

        var origin = source.PlayerPawn?.Value?.AbsOrigin;
        if (origin == null) return HookResult.Continue;

        bool isStep = @event.Step;
        if (engineDuration <= 0f)
            engineDuration = isStep ? 0.4f : 0.8f;
        float radius = engineRadius;
        float radiusSq = radius * radius;
        float now = Server.CurrentTime;
        int srcTeam = (int)source.TeamNum;

        // Short-term memory after the sound pulse (player keeps the contact in mind).
        float memTtl = Math.Clamp(engineDuration * 2.2f + (isStep ? 1.2f : 1.8f),
            BeliefHeardMemMin, BeliefHeardMemMax);

        foreach (var listener in Utilities.GetPlayers())
        {
            if (listener == null || !listener.IsValid || !listener.IsBot || !listener.PawnIsAlive)
                continue;
            if ((int)listener.TeamNum == srcTeam) continue;

            var listenerPawn = listener.PlayerPawn?.Value;
            var lp = listenerPawn?.AbsOrigin;
            if (lp == null) continue;

            float dx = lp.X - origin.X;
            float dy = lp.Y - origin.Y;
            float dz = lp.Z - origin.Z;
            float dist2 = dx * dx + dy * dy + dz * dz;
            if (dist2 > radiusSq) continue; // outside engine hear circle → silent to us too

            float dist = MathF.Sqrt(dist2);
            float edge = Math.Clamp(dist / Math.Max(1f, radius), 0f, 1f);
            // Localization error: near = tighter blob; at edge of hear range = vague.
            // IMPORTANT: Math.Clamp throws if min>max. Footsteps often have Radius≈100
            // → radius*0.35≈35 < 70. That killed OnPlayerSoundBelief on Inferno tests.
            float fuzzyHi = Math.Max(40f, radius * 0.35f);
            float fuzzyLo = Math.Min(55f, fuzzyHi);
            float fuzzy = Math.Clamp(55f + radius * (0.08f + 0.22f * edge), fuzzyLo, fuzzyHi);
            float jx = ((float)_random.NextDouble() * 2f - 1f) * fuzzy * 0.40f;
            float jy = ((float)_random.NextDouble() * 2f - 1f) * fuzzy * 0.40f;
            float conf = Math.Clamp(
                (isStep ? 0.78f : 0.88f) - edge * 0.40f,
                0.28f, 0.90f);

            float hx = origin.X + jx;
            float hy = origin.Y + jy;
            float hz = origin.Z + 36f;
            UpsertBelief((int)listener.Index, BeliefKind.Heard,
                hx, hy, hz, fuzzy, memTtl, conf, now);

            // Push into native InvestigateNoise on the sound event (not every tick).
            var listenerBot = listenerPawn!.Bot;
            if (listenerBot != null)
            {
                var heard = new BeliefSpot
                {
                    X = hx, Y = hy, Z = hz,
                    Radius = fuzzy,
                    Kind = BeliefKind.Heard,
                    Active = true,
                    Confidence = conf,
                    CreatedAt = now,
                    ExpiresAt = now + memTtl,
                };
                FeedNativeNoise(listenerBot, listenerPawn, in heard, now);
            }
        }

        return HookResult.Continue;
    }

    private void RecordDamageBelief(CCSPlayerController victim, CCSPlayerController attacker)
    {
        if (!HumanlikeMode.Enabled) return;

        var vOrigin = victim.PlayerPawn?.Value?.AbsOrigin;
        var aOrigin = attacker.PlayerPawn?.Value?.AbsOrigin;
        if (vOrigin == null || aOrigin == null) return;

        float dx = aOrigin.X - vOrigin.X;
        float dy = aOrigin.Y - vOrigin.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1f) len = 1f;

        float lookX = vOrigin.X + dx / len * BeliefDamageLookDist;
        float lookY = vOrigin.Y + dy / len * BeliefDamageLookDist;
        float lookZ = vOrigin.Z + 48f;

        UpsertBelief((int)victim.Index, BeliefKind.Damage,
            lookX, lookY, lookZ,
            BeliefDamageRadius, BeliefDamageTtl, 0.92f, Server.CurrentTime, allowMerge: false);
    }

    private void RecordTeammateDeathBelief(CCSPlayerController victim)
    {
        if (!HumanlikeMode.Enabled) return;
        if (victim == null || !victim.IsValid) return;

        var origin = victim.PlayerPawn?.Value?.AbsOrigin;
        if (origin == null) return;

        float rangeSq = BeliefDeathCalloutRange * BeliefDeathCalloutRange;
        float now = Server.CurrentTime;
        int team = (int)victim.TeamNum;

        foreach (var mate in Utilities.GetPlayers())
        {
            if (mate == null || !mate.IsValid || !mate.IsBot || !mate.PawnIsAlive) continue;
            if ((int)mate.TeamNum != team) continue;
            if (mate.Slot == victim.Slot) continue;

            var lp = mate.PlayerPawn?.Value?.AbsOrigin;
            if (lp == null) continue;
            float dx = lp.X - origin.X;
            float dy = lp.Y - origin.Y;
            float dz = lp.Z - origin.Z;
            if (dx * dx + dy * dy + dz * dz > rangeSq) continue;

            float jx = ((float)_random.NextDouble() * 2f - 1f) * 100f;
            float jy = ((float)_random.NextDouble() * 2f - 1f) * 100f;

            UpsertBelief((int)mate.Index, BeliefKind.Heard,
                origin.X + jx, origin.Y + jy, origin.Z + 36f,
                280f, 4.0f, 0.58f, now);
        }
    }

    private void TickBeliefPerception(CCSPlayerController player, CCSPlayerPawn pawn, CCSBot bot, float now)
    {
        int idx = (int)player.Index;
        ExpireBeliefs(idx, now);
        UpdateVisibleBelief(player, pawn, bot, now);
        UpdateNativeNoiseBelief(player, bot, now);
        PublishIntelSnapshot(idx, now);
    }

    // ── Debug (rate-limited) ───────────────────────────────────

    private void DebugBeliefEvent(int botIndex, in BeliefSpot spot, string action)
    {
        float now = Server.CurrentTime;
        if (_debugEventBudget <= 0)
        {
            if (now < _debugNextSummaryAt) return;
            _debugEventBudget = DebugEventsPerWindow;
            _debugNextSummaryAt = now + DebugSummaryInterval;
            WriteBeliefSummary(now);
        }

        _debugEventBudget--;
        BroadcastDebug(
            $"[intel] {action} bot#{botIndex} {spot.Kind} conf={spot.Confidence:F2} r={spot.Radius:F0} left={Math.Max(0f, spot.ExpiresAt - now):F1}s");
    }

    private void WriteBeliefSummary(float now)
    {
        int heard = 0, seen = 0, dmg = 0, bots = 0;
        foreach (var kv in _beliefs)
        {
            bool any = false;
            foreach (var s in kv.Value)
            {
                if (!s.Active || now >= s.ExpiresAt) continue;
                any = true;
                switch (s.Kind)
                {
                    case BeliefKind.Heard: heard++; break;
                    case BeliefKind.Seen: seen++; break;
                    case BeliefKind.Damage: dmg++; break;
                }
            }
            if (any) bots++;
        }

        BroadcastDebug($"[intel] summary bots={bots} heard={heard} seen={seen} dmg={dmg}");
    }

    [ConsoleCommand("css_botstate_beliefdebug", "Toggle Smarter-Bot intel debug (summary ~2s, few events)")]
    [CommandHelper(minArgs: 0, usage: "[0|1]", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnBeliefDebugCmd(CCSPlayerController? caller, CommandInfo cmd)
    {
        if (cmd.ArgCount > 1)
        {
            string arg = cmd.GetArg(1);
            _debugBelief = arg == "1"
                || arg.Equals("true", StringComparison.OrdinalIgnoreCase)
                || arg.Equals("on", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            _debugBelief = !_debugBelief;
        }

        _debugEventBudget = DebugEventsPerWindow;
        _debugNextSummaryAt = 0f;
        string msg = $"[Smarter-Bot] belief debug = {_debugBelief} (summary every {DebugSummaryInterval:0}s)";
        cmd.ReplyToCommand(msg);
        Logger.LogInformation("{Msg}", msg);
        Console.WriteLine(msg);
    }
}
