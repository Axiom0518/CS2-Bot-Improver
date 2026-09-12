using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using System;
using System.Collections.Generic;

namespace BotState;

// P3b/P3c — light angle assist on top of author Vision_AlwaysWatchApproachPoints.
//
// Rules:
// - Never replace author approach watching. If native LookAtSpot still has time, yield.
// - Belief (heard/damage/last-seen) may suggest a LookAtSpot only when native is idle.
// - Geometric corner rays are a weak fallback while moving — write LookAtSpot only,
//   do not Teleport/LookYaw-fight the author's look pipeline (that felt like "smooth
//   turns", not pre-aim).
public partial class BotState
{
    private const float ClearMinSpeed = 40f;
    private const float ClearRayLength = 420f;
    private const float ClearMinCornerDelta = 85f;
    private const float ClearLookHoldMin = 0.28f;
    private const float ClearLookHoldMax = 0.50f;
    private const float ClearRescanMin = 0.20f;
    private const float ClearLookAngleTolerance = 10f;
    // If native still owning look-at for longer than this, we do not touch it.
    private const float ClearNativeLookYield = 0.12f;
    private static readonly float[] ClearSweepOffsets =
        [-60f, -45f, -30f, -15f, 0f, 15f, 30f, 45f, 60f];

    private readonly Dictionary<int, AngleClearState> _angleClear = new();
    private readonly Vector _clearEye = new();
    private readonly Vector _clearEnd = new();
    private int _clearTickCounter;

    private struct AngleClearState
    {
        public float HoldUntil;
        public float NextScanAt;
        public float TargetX;
        public float TargetY;
        public float TargetZ;
        public bool HasTarget;
        public bool FromBelief;
    }

    private void ClearAngleClearingState() => _angleClear.Clear();

    private void UpdateAngleClearing(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        CCSBot bot,
        int botIndex,
        float now)
    {
        _clearTickCounter++;
        if ((_clearTickCounter + botIndex) % 4 != 0)
        {
            if (_angleClear.TryGetValue(botIndex, out var pending) && pending.HasTarget && now < pending.HoldUntil)
                ApplyClearLook(bot, pawn, pending, now);
            return;
        }

        if (_isFreezeTime || !player.PawnIsAlive)
        {
            _angleClear.Remove(botIndex);
            return;
        }

        if (ShouldYieldAngleClearing(player, bot, botIndex, now))
        {
            if (_angleClear.TryGetValue(botIndex, out var yielded) && yielded.HasTarget)
            {
                yielded.HasTarget = false;
                yielded.HoldUntil = 0f;
                _angleClear[botIndex] = yielded;
            }
            return;
        }

        if (!_angleClear.TryGetValue(botIndex, out var state))
            state = default;

        if (state.HasTarget && now < state.HoldUntil)
        {
            ApplyClearLook(bot, pawn, state, now);
            _angleClear[botIndex] = state;
            return;
        }

        if (now < state.NextScanAt)
            return;

        var origin = pawn.AbsOrigin;
        if (origin == null) return;

        float eyeZ = origin.Z + (pawn.ViewOffset?.Z ?? 64f);
        _clearEye.X = origin.X;
        _clearEye.Y = origin.Y;
        _clearEye.Z = eyeZ;

        // Intel look is owned by TickBeliefDecisions — geometric clear only fills empty FOV.
        float vx = pawn.AbsVelocity.X;
        float vy = pawn.AbsVelocity.Y;
        float speed2 = vx * vx + vy * vy;
        if (speed2 < ClearMinSpeed * ClearMinSpeed)
        {
            state.HasTarget = false;
            state.FromBelief = false;
            state.NextScanAt = now + ClearRescanMin;
            _angleClear[botIndex] = state;
            return;
        }

        float speed = MathF.Sqrt(speed2);
        float baseYaw = MathF.Atan2(vy / speed, vx / speed) * (180f / MathF.PI);

        Span<float> hits = stackalloc float[ClearSweepOffsets.Length];
        for (int i = 0; i < ClearSweepOffsets.Length; i++)
            hits[i] = TraceClearDistance(baseYaw + ClearSweepOffsets[i]);

        if (!TryPickClearCorner(origin.X, origin.Y, eyeZ, baseYaw, hits,
                out float lookX, out float lookY, out float lookZ))
        {
            state.HasTarget = false;
            state.FromBelief = false;
            state.NextScanAt = now + ClearRescanMin;
            _angleClear[botIndex] = state;
            return;
        }

        float holdSeconds = ClearLookHoldMin
            + (float)_random.NextDouble() * (ClearLookHoldMax - ClearLookHoldMin);
        state.HasTarget = true;
        state.FromBelief = false;
        state.TargetX = lookX;
        state.TargetY = lookY;
        state.TargetZ = lookZ;
        state.HoldUntil = now + holdSeconds;
        state.NextScanAt = state.HoldUntil + ClearRescanMin;
        _angleClear[botIndex] = state;

        ApplyClearLook(bot, pawn, state, now);
    }

    private bool ShouldYieldAngleClearing(
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

        // Heard/seen intel owns the eyes when fresh; geometric clear is idle pre-aim.
        if (TryGetBestBelief(botIndex, now, out var threat, out _)
            && now - threat.CreatedAt < 1.5f
            && (threat.Kind == BeliefKind.Heard || threat.Kind == BeliefKind.Damage))
            return true;

        // Author approach / native look owns the eyes — we only fill gaps.
        float remaining = bot.LookAtSpotTimestamp + bot.LookAtSpotDuration - now;
        if (remaining > ClearNativeLookYield && !IsOurClearLook(botIndex, bot, now))
            return true;

        return false;
    }

    private bool IsOurClearLook(int botIndex, CCSBot bot, float now)
    {
        if (!_angleClear.TryGetValue(botIndex, out var state) || !state.HasTarget)
            return false;
        if (now >= state.HoldUntil)
            return false;

        var spot = bot.LookAtSpot;
        if (spot == null) return false;
        float dx = spot.X - state.TargetX;
        float dy = spot.Y - state.TargetY;
        float dz = spot.Z - state.TargetZ;
        return dx * dx + dy * dy + dz * dz < 40f * 40f;
    }

    private float TraceClearDistance(float yawDeg)
    {
        float rad = yawDeg * (MathF.PI / 180f);
        _clearEnd.X = _clearEye.X + MathF.Cos(rad) * ClearRayLength;
        _clearEnd.Y = _clearEye.Y + MathF.Sin(rad) * ClearRayLength;
        _clearEnd.Z = _clearEye.Z;

        try
        {
            var opts = new TraceOptions { InteractsWith = Masks.SolidBrushOnly };
            var result = Trace.TraceEndShape(_clearEye, _clearEnd, options: opts);
            float frac = Math.Clamp(result.Fraction, 0f, 1f);
            return frac * ClearRayLength;
        }
        catch
        {
            return ClearRayLength;
        }
    }

    private static bool TryPickClearCorner(
        float eyeX, float eyeY, float eyeZ, float baseYaw, Span<float> hits,
        out float lookX, out float lookY, out float lookZ)
    {
        lookX = lookY = lookZ = 0f;
        int bestI = -1;
        float bestDelta = ClearMinCornerDelta;
        bool bestDeeperOnRight = true;

        for (int i = 0; i < hits.Length - 1; i++)
        {
            float delta = hits[i + 1] - hits[i];
            float abs = MathF.Abs(delta);
            if (abs < bestDelta) continue;
            bestDelta = abs;
            bestI = i;
            bestDeeperOnRight = delta > 0f;
        }

        if (bestI < 0) return false;

        int openIdx = bestDeeperOnRight ? bestI + 1 : bestI;
        float openDist = hits[openIdx];
        float aimDist = Math.Clamp(openDist * 0.72f, 120f, ClearRayLength * 0.9f);
        float yaw = baseYaw + ClearSweepOffsets[openIdx];
        float rad = yaw * (MathF.PI / 180f);
        lookX = eyeX + MathF.Cos(rad) * aimDist;
        lookY = eyeY + MathF.Sin(rad) * aimDist;
        lookZ = eyeZ;
        return true;
    }

    // LookAtSpot only — let CCSBot::UpdateLookAngles chase it. No Teleport / LookYaw
    // override (that fought author approach watching and felt like fake pre-aim).
    private void ApplyClearLook(CCSBot bot, CCSPlayerPawn pawn, in AngleClearState state, float now)
    {
        var spot = bot.LookAtSpot;
        if (spot != null)
        {
            spot.X = state.TargetX;
            spot.Y = state.TargetY;
            spot.Z = state.TargetZ;
        }

        ref float lookDur = ref bot.LookAtSpotDuration;
        lookDur = Math.Max(0.05f, state.HoldUntil - now);
        ref float lookTs = ref bot.LookAtSpotTimestamp;
        lookTs = now;

        ref bool lookAttack = ref bot.LookAtSpotAttack;
        lookAttack = false;
        ref bool lookClear = ref bot.LookAtSpotClearIfClose;
        lookClear = true;
        ref float lookTol = ref bot.LookAtSpotAngleTolerance;
        lookTol = ClearLookAngleTolerance;

        // Damage cues: briefly release pathfinder eye lock so they can snap to the hit dir.
        if (state.FromBelief)
        {
            ref bool pathControl = ref bot.EyeAnglesUnderPathFinderControl;
            pathControl = false;
        }
    }
}
