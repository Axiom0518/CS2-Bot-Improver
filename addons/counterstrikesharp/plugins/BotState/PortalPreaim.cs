using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using System;
using System.Collections.Generic;

namespace BotState;

// P5b — path-ahead portal pre-aim. Replaces geometric AngleClearing when nav graph ready.
// Write LookAtSpot only; never Teleport / LookYaw.
public partial class BotState
{
    private const float PortalMinSpeed = 40f;
    private const float PortalLookHoldMin = 0.32f;
    private const float PortalLookHoldMax = 0.55f;
    private const float PortalRescanMin = 0.18f;
    private const float PortalLookAngleTolerance = 10f;
    private const float PortalPushMin = 40f;
    private const float PortalPushMax = 72f;
    private const float PortalMinDot = 0.12f; // must be somewhat ahead of travel
    // P5d: keep native UpdateLookAround off a hair past our hold.
    private const float PortalInhibitPad = 0.08f;
    private const float PortalStealReassertMin = 0.16f;

    private readonly Dictionary<int, PortalLookState> _portalLook = new();
    private int _portalPickCount;   // debug counters (cleared by look summary)
    private int _portalMissCount;
    private int _portalStealCount;  // native overwrote our LookAtSpot mid-hold

    private struct PortalLookState
    {
        public float HoldUntil;
        public float NextScanAt;
        public float NextReassertAt;
        public float TargetX, TargetY, TargetZ;
        public int FromAreaId;
        public int ToAreaId;
        public bool HasTarget;
    }

    private void ClearPortalPreaimState()
    {
        _portalLook.Clear();
        _portalPickCount = 0;
        _portalMissCount = 0;
        _portalStealCount = 0;
    }

    private bool IsOurPortalLook(int botIndex, CCSBot bot, float now)
        => IsDirectedLook(botIndex, bot, now, LookOwner.Portal);

    private void UpdatePortalPreaim(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        CCSBot bot,
        int botIndex,
        float now)
    {
        // Wait for a deferred/command build — never kick off FindSignature mid-tick.
        if (!_navGraphReady)
            return;

        if (_isFreezeTime || !player.PawnIsAlive)
        {
            _portalLook.Remove(botIndex);
            return;
        }

        if (ShouldYieldPortalPreaim(player, bot, botIndex, now))
        {
            if (_portalLook.TryGetValue(botIndex, out var yielded) && yielded.HasTarget)
            {
                yielded.HasTarget = false;
                yielded.HoldUntil = 0f;
                _portalLook[botIndex] = yielded;
            }
            return;
        }

        if (!_portalLook.TryGetValue(botIndex, out var state))
            state = default;

        // Hold: do not refresh LookAtSpot every tick — let UpdateLookAngles chase.
        // Only rewrite if native stole the spot, and not faster than StealReassertMin.
        if (state.HasTarget && now < state.HoldUntil)
        {
            if (!IsOurPortalLook(botIndex, bot, now) && now >= state.NextReassertAt)
            {
                _portalStealCount++;
                state.NextReassertAt = now + PortalStealReassertMin;
                ApplyPortalLook(bot, state, now);
            }
            _portalLook[botIndex] = state;
            return;
        }

        if (((_navWorkFrame + botIndex) & 3) != 0)
            return;

        if (now < state.NextScanAt)
            return;

        var origin = pawn.AbsOrigin;
        if (origin == null) return;

        float vx = pawn.AbsVelocity.X;
        float vy = pawn.AbsVelocity.Y;
        float speed2 = vx * vx + vy * vy;
        if (speed2 < PortalMinSpeed * PortalMinSpeed)
        {
            state.HasTarget = false;
            state.NextScanAt = now + PortalRescanMin;
            _portalLook[botIndex] = state;
            return;
        }

        float speed = MathF.Sqrt(speed2);
        float hx = vx / speed;
        float hy = vy / speed;

        // Prefer goal heading when the goal is meaningfully ahead.
        try
        {
            var goal = bot.GoalPosition;
            if (goal != null)
            {
                float gdx = goal.X - origin.X;
                float gdy = goal.Y - origin.Y;
                float g2 = gdx * gdx + gdy * gdy;
                if (g2 > 80f * 80f)
                {
                    float glen = MathF.Sqrt(g2);
                    hx = gdx / glen;
                    hy = gdy / glen;
                }
            }
        }
        catch { /* GoalPosition schema */ }

        float eyeZ = origin.Z + Math.Clamp(pawn.ViewOffset?.Z ?? 64f, 40f, 72f);
        if (!TryGetNavAreaIndex(origin.X, origin.Y, origin.Z, out int areaIdx))
        {
            _portalMissCount++;
            state.HasTarget = false;
            state.NextScanAt = now + PortalRescanMin;
            _portalLook[botIndex] = state;
            return;
        }

        if (!TryPickForwardPortal(areaIdx, hx, hy, eyeZ,
                out float lookX, out float lookY, out float lookZ,
                out int fromId, out int toId))
        {
            _portalMissCount++;
            state.HasTarget = false;
            state.NextScanAt = now + PortalRescanMin;
            _portalLook[botIndex] = state;
            return;
        }

        float hold = PortalLookHoldMin
            + (float)_random.NextDouble() * (PortalLookHoldMax - PortalLookHoldMin);
        // Sprint: shorter hold, still pre-aim one doorway.
        if (speed > 200f)
            hold *= 0.75f;

        state.HasTarget = true;
        state.TargetX = lookX;
        state.TargetY = lookY;
        state.TargetZ = lookZ;
        state.FromAreaId = fromId;
        state.ToAreaId = toId;
        state.HoldUntil = now + hold;
        state.NextScanAt = state.HoldUntil + PortalRescanMin;
        state.NextReassertAt = now + PortalStealReassertMin;
        _portalLook[botIndex] = state;
        _portalPickCount++;

        ApplyPortalLook(bot, state, now);
    }

    private bool ShouldYieldPortalPreaim(
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

        if (TryGetBestBelief(botIndex, now, out var threat, out _)
            && now - threat.CreatedAt < 1.5f
            && (threat.Kind == BeliefKind.Heard || threat.Kind == BeliefKind.Damage))
            return true;

        // P5d: do NOT yield to native approach LookAtSpot while walking.
        // We take the eyes + InhibitLookAround; combat/flash/belief already returned.
        return false;
    }

    private bool TryPickForwardPortal(
        int areaIdx, float hx, float hy, float eyeZ,
        out float lookX, out float lookY, out float lookZ,
        out int fromId, out int toId)
        => TryPickForwardPortal(areaIdx, hx, hy, eyeZ, PortalMinDot,
            out lookX, out lookY, out lookZ, out fromId, out toId);

    private bool TryPickForwardPortal(
        int areaIdx, float hx, float hy, float eyeZ, float minDot,
        out float lookX, out float lookY, out float lookZ,
        out int fromId, out int toId)
    {
        lookX = lookY = lookZ = 0f;
        fromId = toId = -1;

        if (areaIdx < 0 || areaIdx >= _navPortalsByArea.Length)
            return false;

        var list = _navPortalsByArea[areaIdx];
        if (list == null || list.Count == 0)
            return false;

        ref readonly var here = ref _navBoxes[areaIdx];
        float bestDot = minDot;
        int bestP = -1;
        bool bestLeavingToB = true;

        for (int n = 0; n < list.Count; n++)
        {
            int pi = list[n];
            ref readonly var p = ref _navPortals[pi];

            bool weAreA = p.AreaIndexA == areaIdx;
            float dirX = weAreA ? p.DirAtoBX : -p.DirAtoBX;
            float dirY = weAreA ? p.DirAtoBY : -p.DirAtoBY;
            float dot = dirX * hx + dirY * hy;
            if (dot < bestDot) continue;

            // Prefer portals that are still ahead of our feet (not behind shoulder).
            float toMidX = p.MidX - here.Cx;
            float toMidY = p.MidY - here.Cy;
            float midLen = MathF.Sqrt(toMidX * toMidX + toMidY * toMidY);
            if (midLen > 1f)
            {
                float midDot = (toMidX / midLen) * hx + (toMidY / midLen) * hy;
                if (midDot < -0.15f) continue;
            }

            bestDot = dot;
            bestP = pi;
            bestLeavingToB = weAreA;
        }

        if (bestP < 0) return false;

        ref readonly var chosen = ref _navPortals[bestP];
        int otherIdx = bestLeavingToB ? chosen.AreaIndexB : chosen.AreaIndexA;
        float pushX = bestLeavingToB ? chosen.DirAtoBX : -chosen.DirAtoBX;
        float pushY = bestLeavingToB ? chosen.DirAtoBY : -chosen.DirAtoBY;
        float push = PortalPushMin
            + (float)_random.NextDouble() * (PortalPushMax - PortalPushMin);

        lookX = chosen.MidX + pushX * push;
        lookY = chosen.MidY + pushY * push;
        lookZ = eyeZ;
        fromId = (int)_navBoxes[areaIdx].Id;
        toId = (int)_navBoxes[otherIdx].Id;
        return true;
    }

    // P5c — doorway toward a belief XY (occupy angle), not the blob center.
    // Returns false when too close / no nav → caller keeps blob look.
    private bool TryPickBeliefPortal(
        float botX, float botY, float botZ, float eyeZ,
        float threatX, float threatY,
        out float lookX, out float lookY, out float lookZ,
        out int fromId, out int toId)
    {
        lookX = lookY = lookZ = 0f;
        fromId = toId = -1;
        if (!_navGraphReady) return false;

        float dx = threatX - botX;
        float dy = threatY - botY;
        float dist2 = dx * dx + dy * dy;
        // Same-room / point-blank: staring the estimated spot is fine.
        if (dist2 < 100f * 100f)
            return false;

        if (!TryGetNavAreaIndex(botX, botY, botZ, out int areaIdx))
            return false;

        float dist = MathF.Sqrt(dist2);
        float hx = dx / dist;
        float hy = dy / dist;

        if (TryPickForwardPortal(areaIdx, hx, hy, eyeZ,
                out lookX, out lookY, out lookZ, out fromId, out toId))
        {
            TryAdvanceBeliefPortalHop(threatX, threatY, hx, hy, eyeZ, dist2,
                ref lookX, ref lookY, ref lookZ, ref fromId, ref toId);
            return true;
        }

        // Relaxed: any portal from this area with non-negative alignment.
        if (!TryPickForwardPortal(areaIdx, hx, hy, eyeZ, minDot: 0f,
                out lookX, out lookY, out lookZ, out fromId, out toId))
            return false;

        TryAdvanceBeliefPortalHop(threatX, threatY, hx, hy, eyeZ, dist2,
            ref lookX, ref lookY, ref lookZ, ref fromId, ref toId);
        return true;
    }

    // One extra hop toward the belief if it gets the look point closer to the sound.
    private void TryAdvanceBeliefPortalHop(
        float threatX, float threatY, float hx, float hy, float eyeZ, float dist2,
        ref float lookX, ref float lookY, ref float lookZ,
        ref int fromId, ref int toId)
    {
        if (dist2 < 220f * 220f) return;
        if (!_navIdToIndex.TryGetValue((uint)toId, out int nextIdx)) return;

        if (!TryPickForwardPortal(nextIdx, hx, hy, eyeZ,
                out float x2, out float y2, out float z2, out int f2, out int t2))
            return;

        float d1x = lookX - threatX, d1y = lookY - threatY;
        float d2x = x2 - threatX, d2y = y2 - threatY;
        if (d2x * d2x + d2y * d2y >= d1x * d1x + d1y * d1y)
            return;

        lookX = x2;
        lookY = y2;
        lookZ = z2;
        fromId = f2;
        toId = t2;
    }

    private void ApplyPortalLook(CCSBot bot, in PortalLookState state, float now)
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
        lookClear = false; // walking into the doorway used to wipe the spot (steal thrash)
        ref float lookTol = ref bot.LookAtSpotAngleTolerance;
        lookTol = PortalLookAngleTolerance;

        try
        {
            ref bool pathControl = ref bot.EyeAnglesUnderPathFinderControl;
            pathControl = false;
        }
        catch { /* schema */ }

        // P5d: pin LookAtSpot against AlwaysWatchApproachPoints for this hold.
        InhibitNativeLookAround(bot, state.HoldUntil + PortalInhibitPad);
    }

    // Raise InhibitLookAroundTimestamp so native look-around won't replace our spot.
    private static void InhibitNativeLookAround(CCSBot bot, float until)
    {
        try
        {
            ref float inhibit = ref bot.InhibitLookAroundTimestamp;
            if (inhibit < until)
                inhibit = until;
        }
        catch
        {
            // Schema field missing after an update.
        }
    }
}
