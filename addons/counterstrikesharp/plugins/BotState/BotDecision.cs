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
    // Soften belief whips: never demand more than this yaw per LookAtSpot write.
    private const float BeliefMaxYawStep = 52f;
    private const float BeliefLargeYawHoldMul = 1.50f;
    private const float BeliefLargeYawHoldFrom = 38f;

    private readonly Dictionary<int, float> _decisionNextLookAt = new();
    private readonly Dictionary<int, float> _decisionLastNoiseFeedAt = new();
    // Last LookAtSpot we wrote for intel — P5a ownership sampling.
    private readonly Dictionary<int, IntelLookState> _intelLook = new();
    private int _intelStealCount;

    private struct IntelLookState
    {
        public float TargetX, TargetY, TargetZ;
        public float HoldUntil;
        public BeliefKind Kind;
        public bool Active;
        // P5c: when look snapped to a doorway instead of blob center.
        public bool ViaPortal;
        public int FromAreaId;
        public int ToAreaId;
        public float NextReassertAt;
    }

    private void ClearDecisionState()
    {
        _decisionNextLookAt.Clear();
        _decisionLastNoiseFeedAt.Clear();
        _intelLook.Clear();
        _intelStealCount = 0;
    }

    private bool IsOurIntelLook(int botIndex, CCSBot bot, float now)
        => IsDirectedLook(botIndex, bot, now, LookOwner.Belief);

    private void TickBeliefDecisions(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        CCSBot bot,
        int botIndex,
        float now)
    {
        if (!TryGetBestBelief(botIndex, now, out _, out _))
            return;

        // Alert only. Do NOT write LookAtSpot here and do NOT feed NoisePosition:
        // native investigate-noise fights the look director and makes bots stop/spin.
        RaiseAlertFromIntel(bot, now);
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

            // Do not clear InhibitLookAround here — P5d keeps native approach
            // from stealing while belief/portal owns the eyes. NoisePosition still
            // feeds investigate path; LookAtSpot is ours.
            // (Previously inhibit=0 invited AlwaysWatchApproachPoints mid-hold.)
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

    private void ApplyIntelLook(
        CCSBot bot, CCSPlayerPawn pawn, int botIndex,
        in BeliefSpot threat, float radius, float now)
    {
        var spot = bot.LookAtSpot;
        if (spot == null) return;

        var origin = pawn.AbsOrigin;
        float eyeZ = origin != null
            ? origin.Z + Math.Clamp(pawn.ViewOffset?.Z ?? 64f, 40f, 72f)
            : threat.Z;

        float tx = threat.X;
        float ty = threat.Y;
        float tz = Math.Clamp(threat.Z, eyeZ - 48f, eyeZ + 24f);
        bool viaPortal = false;
        int fromId = -1, toId = -1;

        // P5c: occupy the doorway toward the belief, not the fuzzy blob center.
        if (origin != null
            && TryPickBeliefPortal(origin.X, origin.Y, origin.Z, eyeZ,
                threat.X, threat.Y,
                out float px, out float py, out float pz,
                out fromId, out toId))
        {
            tx = px;
            ty = py;
            tz = pz;
            viaPortal = true;
        }

        float holdMul = 1f;
        if (origin != null)
            SoftenBeliefLookXy(pawn, origin.X, origin.Y, ref tx, ref ty, out holdMul);

        float hold = DecisionLookHold * holdMul;
        if (threat.Kind == BeliefKind.Damage)
            hold *= 0.85f;

        spot.X = tx;
        spot.Y = ty;
        spot.Z = tz;

        ref float lookDur = ref bot.LookAtSpotDuration;
        lookDur = hold;
        ref float lookTs = ref bot.LookAtSpotTimestamp;
        lookTs = now;

        ref bool lookAttack = ref bot.LookAtSpotAttack;
        lookAttack = false;
        ref bool lookClear = ref bot.LookAtSpotClearIfClose;
        lookClear = false;
        // Slightly tighter when we locked a doorway; wider for raw blob.
        ref float lookTol = ref bot.LookAtSpotAngleTolerance;
        lookTol = viaPortal
            ? Math.Clamp(7f + radius * 0.008f, 7f, 12f)
            : Math.Clamp(8f + radius * 0.015f, 8f, 16f);

        ref bool pathControl = ref bot.EyeAnglesUnderPathFinderControl;
        pathControl = false;

        // P5d: hold native look-around off for the duration of this intel look.
        InhibitNativeLookAround(bot, now + hold + 0.08f);

        _intelLook[botIndex] = new IntelLookState
        {
            TargetX = tx,
            TargetY = ty,
            TargetZ = tz,
            HoldUntil = now + hold,
            Kind = threat.Kind,
            Active = true,
            ViaPortal = viaPortal,
            FromAreaId = fromId,
            ToAreaId = toId,
            NextReassertAt = now + 0.16f,
        };
    }

    private void ReassertIntelLook(CCSBot bot, int botIndex, in IntelLookState state, float now)
    {
        var spot = bot.LookAtSpot;
        if (spot != null)
        {
            spot.X = state.TargetX;
            spot.Y = state.TargetY;
            spot.Z = state.TargetZ;
        }

        float rem = Math.Max(0.08f, state.HoldUntil - now);
        ref float lookDur = ref bot.LookAtSpotDuration;
        lookDur = rem;
        ref float lookTs = ref bot.LookAtSpotTimestamp;
        lookTs = now;

        try
        {
            ref bool pathControl = ref bot.EyeAnglesUnderPathFinderControl;
            pathControl = false;
        }
        catch { /* schema */ }

        InhibitNativeLookAround(bot, state.HoldUntil + 0.08f);
        _intelStealCount++;

        var stored = state;
        stored.NextReassertAt = now + 0.16f;
        _intelLook[botIndex] = stored;
    }

    // Cap demanded yaw so LookAtSpot doesn't whip 100°+ in one write.
    // Next decision pulse continues toward the portal — progressive turn.
    private static void SoftenBeliefLookXy(
        CCSPlayerPawn pawn,
        float originX, float originY,
        ref float tx, ref float ty,
        out float holdMul,
        float maxYawStep = BeliefMaxYawStep)
    {
        holdMul = 1f;
        if (maxYawStep < 8f) maxYawStep = 8f;
        float eyeYaw = pawn.EyeAngles.Y;
        float dx = tx - originX;
        float dy = ty - originY;
        float dist2 = dx * dx + dy * dy;
        if (dist2 < 40f * 40f)
            return;

        float wantYaw = MathF.Atan2(dy, dx) * (180f / MathF.PI);
        float delta = (float)NormalizeAngleDeg(wantYaw - eyeYaw);
        float abs = MathF.Abs(delta);
        if (abs >= BeliefLargeYawHoldFrom)
            holdMul = BeliefLargeYawHoldMul;
        if (abs <= maxYawStep)
            return;

        float step = MathF.CopySign(maxYawStep, delta);
        float softYaw = eyeYaw + step;
        float rad = softYaw * (MathF.PI / 180f);
        float         dist = MathF.Sqrt(dist2);
        dist = Math.Min(dist, 520f);
        tx = originX + MathF.Cos(rad) * dist;
        ty = originY + MathF.Sin(rad) * dist;
        holdMul = BeliefLargeYawHoldMul;
    }
}
