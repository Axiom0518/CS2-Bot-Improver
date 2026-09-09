using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using System;
using System.Collections.Generic;

namespace BotState;

// Decision layer: nudge native noise + occasional LookAtSpot.
// Must stay cheap — never raytrace or rewrite native fields every tick for all bots.
public partial class BotState
{
    private const float DecisionLookIntervalMin = 0.50f;
    private const float DecisionLookIntervalMax = 0.90f;
    private const float DecisionLookHold = 0.55f;
    private const float DecisionAlertSeconds = 3.0f;
    // Only re-feed native noise when the belief is still "fresh".
    private const float DecisionNoiseFeedMaxAge = 1.25f;

    private readonly Dictionary<int, float> _decisionNextLookAt = new();
    private readonly Dictionary<int, float> _decisionLastNoiseFeedAt = new();

    private void ClearDecisionState()
    {
        _decisionNextLookAt.Clear();
        _decisionLastNoiseFeedAt.Clear();
    }

    private void TickBeliefDecisions(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        CCSBot bot,
        int botIndex,
        float now)
    {
        if (!TryGetBestBelief(botIndex, now, out var threat, out float radius))
        {
            _decisionNextLookAt.Remove(botIndex);
            _decisionLastNoiseFeedAt.Remove(botIndex);
            return;
        }

        // Feed native InvestigateNoise at most ~3 Hz, and only for fresh hears/hits.
        float age = now - threat.CreatedAt;
        if ((threat.Kind == BeliefKind.Heard || threat.Kind == BeliefKind.Damage)
            && age <= DecisionNoiseFeedMaxAge)
        {
            if (!_decisionLastNoiseFeedAt.TryGetValue(botIndex, out float last)
                || now - last >= 0.33f)
            {
                FeedNativeNoise(bot, pawn, in threat, now);
                _decisionLastNoiseFeedAt[botIndex] = now;
            }
        }

        RaiseAlertFromIntel(bot, now);

        if (ShouldYieldIntelLook(player, bot, botIndex, now))
            return;

        if (!_decisionNextLookAt.TryGetValue(botIndex, out float nextAt))
            nextAt = now;
        if (now < nextAt)
            return;

        ApplyIntelLook(bot, pawn, threat, radius, now);

        float gap = DecisionLookIntervalMin
            + (float)_random.NextDouble() * (DecisionLookIntervalMax - DecisionLookIntervalMin);
        if (threat.Kind == BeliefKind.Damage)
            gap *= 0.55f;
        else if (threat.Kind == BeliefKind.Heard)
            gap *= 0.85f;
        else if (threat.Confidence < 0.45f)
            gap *= 1.20f;

        _decisionNextLookAt[botIndex] = now + gap;
    }

    private static void FeedNativeNoise(CCSBot bot, CCSPlayerPawn pawn, in BeliefSpot threat, float now)
    {
        if (threat.Kind != BeliefKind.Heard && threat.Kind != BeliefKind.Damage)
            return;

        try
        {
            var origin = pawn.AbsOrigin;
            float eyeZ = origin != null
                ? origin.Z + Math.Clamp(pawn.ViewOffset?.Z ?? 64f, 40f, 72f)
                : threat.Z;
            // Keep look height near eye band — never sky.
            float z = Math.Clamp(threat.Z, eyeZ - 48f, eyeZ + 24f);

            var noise = bot.NoisePosition;
            if (noise != null)
            {
                noise.X = threat.X;
                noise.Y = threat.Y;
                noise.Z = z;
            }

            ref float ts = ref bot.NoiseTimestamp;
            ts = now;

            // Do not touch NoiseTravelDistance / pathfinder flags every pulse —
            // that fought native look and tipped barrels skyward.
            ref float inhibit = ref bot.InhibitLookAroundTimestamp;
            inhibit = 0f;
        }
        catch
        {
            // Schema field missing after an update — look path below still runs.
        }
    }

    private static void RaiseAlertFromIntel(CCSBot bot, float now)
    {
        CountdownTimer alertTimer = bot.AlertTimer;
        ref float alertTs = ref alertTimer.Timestamp;
        float wantUntil = now + DecisionAlertSeconds;
        if (alertTs < wantUntil)
        {
            ref float alertDur = ref alertTimer.Duration;
            alertDur = DecisionAlertSeconds;
            alertTs = wantUntil;
            ref float alertScale = ref alertTimer.Timescale;
            alertScale = 1.0f;
        }
    }

    private bool ShouldYieldIntelLook(
        CCSPlayerController player, CCSBot bot, int botIndex, float now)
    {
        if (bot.IsAttacking || bot.IsEnemyVisible || bot.IsAimingAtEnemy)
            return true;

        if (_flashPlantedUntil.TryGetValue(botIndex, out float until) && now < until)
            return true;

        if (_fakeDefuseSearchingBots.Contains(player.Slot))
            return true;

        if (_botController != null
            && BotControllerBridge.IsReplaying(_botController, player.Slot))
            return true;

        // Fresh heard/damage may interrupt idle approach; otherwise yield briefly
        // so author Vision_AlwaysWatchApproachPoints still works.
        if (TryGetBestBelief(botIndex, now, out var threat, out _))
        {
            if ((threat.Kind == BeliefKind.Heard || threat.Kind == BeliefKind.Damage)
                && now - threat.CreatedAt < 1.5f)
                return false;
        }

        float remaining = bot.LookAtSpotTimestamp + bot.LookAtSpotDuration - now;
        if (remaining > 0.25f)
            return true;

        return false;
    }

    private static void ApplyIntelLook(
        CCSBot bot, CCSPlayerPawn pawn, in BeliefSpot threat, float radius, float now)
    {
        var spot = bot.LookAtSpot;
        if (spot == null) return;

        var origin = pawn.AbsOrigin;
        float eyeZ = origin != null
            ? origin.Z + Math.Clamp(pawn.ViewOffset?.Z ?? 64f, 40f, 72f)
            : threat.Z;
        float z = Math.Clamp(threat.Z, eyeZ - 48f, eyeZ + 24f);

        spot.X = threat.X;
        spot.Y = threat.Y;
        spot.Z = z;

        ref float lookDur = ref bot.LookAtSpotDuration;
        lookDur = DecisionLookHold;
        ref float lookTs = ref bot.LookAtSpotTimestamp;
        lookTs = now;

        ref bool lookAttack = ref bot.LookAtSpotAttack;
        lookAttack = false;
        ref bool lookClear = ref bot.LookAtSpotClearIfClose;
        lookClear = true;
        ref float lookTol = ref bot.LookAtSpotAngleTolerance;
        lookTol = Math.Clamp(8f + radius * 0.015f, 8f, 16f);

        ref bool pathControl = ref bot.EyeAnglesUnderPathFinderControl;
        pathControl = false;

        ref float inhibit = ref bot.InhibitLookAroundTimestamp;
        inhibit = 0f;
    }
}
