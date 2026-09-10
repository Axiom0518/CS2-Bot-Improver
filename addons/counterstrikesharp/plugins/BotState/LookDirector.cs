using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using System;
using System.Collections.Generic;

namespace BotState;

// Single look writer while the enemy is NOT visible.
// Portal / belief / clear are inputs. One LookAtSpot comes out.
// Combat / flash / AimImprover take over only when those systems should own the eyes.
public partial class BotState
{
    private const float DirectorHoldMin = 0.70f;
    private const float DirectorHoldMax = 0.95f;
    private const float DirectorStealReassertMin = 0.22f;
    // After LOS drops, AimImprover still writes for a beat. Do not yank LookAtSpot
    // back until that window ends — only keep native look-around inhibited.
    private const float DirectorAfterYieldGrace = 0.58f;
    private const float DirectorAfterYieldNoSteal = 0.95f;
    private const float DirectorInterruptStickySec = 0.22f;
    private const float DirectorSpotOurs = 80f;
    private const float DirectorSpotStolen = 170f;
    private const float DirectorRetargetHeard = 140f;
    private const float DirectorRetargetSeen = 180f;
    private const float DirectorRetargetGun = 240f;
    private const float DirectorRetargetHeardSec = 0.28f;
    private const float DirectorRetargetSeenSec = 0.40f;
    private const float DirectorRetargetGunSec = 0.50f;
    private const float DirectorProgressStep = 70f;
    private const int DirectorScoreClear = 25;
    private const int DirectorScorePortal = 50;
    private const int DirectorScoreSeen = 70;
    private const float DirectorSeenHoldMax = 8.0f;
    private const int DirectorScoreHeard = 80;
    private const int DirectorScoreGun = 90;
    private const int DirectorScoreDamage = 100;

    private readonly Dictionary<int, DirectedLookState> _directedLook = new();

    private struct DirectedLookState
    {
        public LookOwner Owner;
        public float HoldUntil;
        public float NextReassertAt;
        public float TargetX, TargetY, TargetZ;
        public float WriteX, WriteY, WriteZ;
        public int FromAreaId, ToAreaId;
        public bool HasTarget;
        public bool ViaPortal;
        public BeliefKind BeliefKind;
        public int Score;
        public float LastYieldAt;
        public float LastRetargetAt;
        public LookOwner PendingOwner;
        public BeliefKind PendingKind;
        public float PendingSince;
    }

    private void ClearDirectedLookState() => _directedLook.Clear();

    private bool IsDirectedLook(
        int botIndex, CCSBot bot, float now, LookOwner owner)
    {
        if (!_directedLook.TryGetValue(botIndex, out var s) || !s.HasTarget)
            return false;
        if (s.Owner != owner || now >= s.HoldUntil)
            return false;
        var spot = bot.LookAtSpot;
        if (spot == null) return false;
        float vsWrite = LookDist2(spot.X, spot.Y, spot.Z, s.WriteX, s.WriteY, s.WriteZ);
        float vsIntent = LookDist2(spot.X, spot.Y, spot.Z, s.TargetX, s.TargetY, s.TargetZ);
        float ours = DirectorSpotOurs * DirectorSpotOurs;
        return vsWrite < ours || vsIntent < ours;
    }

    private static float LookDist2(
        float ax, float ay, float az, float bx, float by, float bz)
    {
        float dx = ax - bx;
        float dy = ay - by;
        float dz = az - bz;
        return dx * dx + dy * dy + dz * dz;
    }

    private void TickLookDirector(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        CCSBot bot,
        int botIndex,
        float now)
    {
        if (_isFreezeTime || !player.PawnIsAlive)
        {
            _directedLook.Remove(botIndex);
            return;
        }

        if (ShouldYieldLookDirector(player, bot, botIndex, now))
        {
            // Keep the hold. Wiping here made peek flicker slam a new doorway
            // the instant LOS dropped. Combat just owns the write this tick.
            if (_directedLook.TryGetValue(botIndex, out var yielded) && yielded.HasTarget)
            {
                yielded.LastYieldAt = now;
                yielded.NextReassertAt = now + DirectorAfterYieldGrace;
                _directedLook[botIndex] = yielded;
            }
            return;
        }

        if (!_directedLook.TryGetValue(botIndex, out var state))
            state = default;

        bool inGrace = state.HasTarget
            && state.LastYieldAt > 0f
            && now < state.LastYieldAt + DirectorAfterYieldGrace;
        if (inGrace)
        {
            if (now < state.HoldUntil)
                RefreshBeliefHold(botIndex, ref state, now);
            SustainDirectedLook(bot, ref state, now);
            _directedLook[botIndex] = state;
            return;
        }

        int bestScore = ScoreLookSources(botIndex, pawn, now, out LookOwner bestOwner);
        TryGetBestBelief(botIndex, now, out var bestThreat, out _);
        BeliefKind bestKind = bestOwner == LookOwner.Belief ? bestThreat.Kind : default;

        bool holding = state.HasTarget && now < state.HoldUntil;
        bool interrupt = holding
            && ShouldInterruptHold(ref state, bestOwner, bestKind, now);

        if (holding && !interrupt)
        {
            RefreshBeliefHold(botIndex, ref state, now);
            SustainDirectedLook(bot, ref state, now);
            TrySoftRetargetBelief(pawn, bot, botIndex, now, ref state);
            ProgressDirectedLook(bot, pawn, ref state, now);
            _directedLook[botIndex] = state;
            return;
        }

        // Portal scans stay staggered. Belief (lost-vis Seen) must apply the
        // same tick combat yield drops, or native path-look wins the gap.
        if (!interrupt && bestOwner != LookOwner.Belief
            && ((_navWorkFrame + botIndex) & 3) != 0)
            return;

        if (bestScore <= 0 || bestOwner == LookOwner.Idle)
            return;

        if (!TryBuildDirectedTarget(pawn, bot, botIndex, now, bestOwner,
                out float tx, out float ty, out float tz,
                out bool via, out int fromId, out int toId, out BeliefKind kind))
            return;

        if (holding
            && state.Owner == bestOwner
            && fromId == state.FromAreaId && toId == state.ToAreaId)
        {
            RefreshBeliefHold(botIndex, ref state, now);
            SustainDirectedLook(bot, ref state, now);
            _directedLook[botIndex] = state;
            return;
        }

        float hold = DirectorHoldMin
            + (float)_random.NextDouble() * (DirectorHoldMax - DirectorHoldMin);
        if (bestOwner == LookOwner.Belief)
        {
            float holdMax = kind == BeliefKind.Seen ? DirectorSeenHoldMax : 4.2f;
            hold = Math.Clamp(bestThreat.ExpiresAt - now, 0.40f, holdMax);
        }

        state.HasTarget = true;
        state.Owner = bestOwner;
        state.Score = bestScore;
        state.TargetX = tx;
        state.TargetY = ty;
        state.TargetZ = tz;
        state.ViaPortal = via;
        state.FromAreaId = fromId;
        state.ToAreaId = toId;
        state.BeliefKind = kind;
        state.HoldUntil = now + hold;
        state.NextReassertAt = now + DirectorStealReassertMin;
        state.LastRetargetAt = now;
        state.PendingSince = 0f;

        ApplyDirectedLook(bot, pawn, ref state, now,
            soften: bestOwner == LookOwner.Belief, BeliefMaxYawStep, extendHold: true);
        if (bestOwner == LookOwner.Portal)
            _portalPickCount++;
        _directedLook[botIndex] = state;
    }

    private bool ShouldYieldLookDirector(
        CCSPlayerController player, CCSBot bot, int botIndex, float now)
    {
        // Yield only for a live visible fight. IsAttacking / IsAimingAtEnemy
        // stay true after the enemy hides (and OnTick even forces Attack
        // while aiming) — that used to delete the 10s contact hold.
        if (bot.IsEnemyVisible)
            return true;
        if (_flashPlantedUntil.TryGetValue(botIndex, out float until) && now < until)
            return true;
        if (_fakeDefuseSearchingBots.Contains(player.Slot))
            return true;
        if (_botController != null
            && BotControllerBridge.IsReplaying(_botController, player.Slot))
            return true;
        return false;
    }

    // Damage always wins. A one-rung climb (gun↔step, step↔seen, seen↔walk)
    // must stay best for a few ticks so score flicker does not whip the head.
    private static bool ShouldInterruptHold(
        ref DirectedLookState state, LookOwner bestOwner, BeliefKind bestKind, float now)
    {
        int gap = LookPriority(bestOwner, bestKind) - LookPriority(state.Owner, state.BeliefKind);
        if (gap <= 0)
        {
            state.PendingSince = 0f;
            return false;
        }

        if (bestOwner == LookOwner.Belief && bestKind == BeliefKind.Damage)
            return true;
        if (gap >= 2)
            return true;

        if (state.PendingSince > 0f
            && state.PendingOwner == bestOwner
            && state.PendingKind == bestKind
            && now - state.PendingSince >= DirectorInterruptStickySec)
            return true;

        if (state.PendingOwner != bestOwner || state.PendingKind != bestKind || state.PendingSince <= 0f)
            state.PendingSince = now;
        state.PendingOwner = bestOwner;
        state.PendingKind = bestKind;
        return false;
    }

    private void RefreshBeliefHold(int botIndex, ref DirectedLookState state, float now)
    {
        if (state.Owner != LookOwner.Belief) return;
        if (!TryGetBelief(botIndex, state.BeliefKind, now, out var live)) return;
        float holdMax = state.BeliefKind == BeliefKind.Seen ? DirectorSeenHoldMax : 4.2f;
        float want = Math.Clamp(live.ExpiresAt - now, 0.40f, holdMax);
        if (want > state.HoldUntil - now)
            state.HoldUntil = now + want;
        if (state.HoldUntil > live.ExpiresAt)
            state.HoldUntil = live.ExpiresAt;
    }

    private static void RetargetLimits(BeliefKind kind, out float minDist, out float minSec)
    {
        if (kind == BeliefKind.Gun || kind == BeliefKind.Damage)
        {
            minDist = DirectorRetargetGun;
            minSec = DirectorRetargetGunSec;
            return;
        }
        if (kind == BeliefKind.Seen)
        {
            minDist = DirectorRetargetSeen;
            minSec = DirectorRetargetSeenSec;
            return;
        }
        minDist = DirectorRetargetHeard;
        minSec = DirectorRetargetHeardSec;
    }

    private void TrySoftRetargetBelief(
        CCSPlayerPawn pawn, CCSBot bot, int botIndex, float now, ref DirectedLookState state)
    {
        if (state.Owner != LookOwner.Belief) return;
        if (((_navWorkFrame + botIndex) & 3) != 0) return;
        RetargetLimits(state.BeliefKind, out float minDist, out float minSec);
        if (state.LastRetargetAt > 0f && now < state.LastRetargetAt + minSec)
            return;
        if (!TryBuildBeliefKindTarget(pawn, botIndex, now, state.BeliefKind,
                out float tx, out float ty, out float tz,
                out bool via, out int fromId, out int toId))
            return;
        // Blob looks share from/to = -1; still follow the threat if it moved.
        bool sameGate = fromId >= 0
            && fromId == state.FromAreaId && toId == state.ToAreaId;
        if (sameGate) return;
        if (LookDist2(tx, ty, tz, state.TargetX, state.TargetY, state.TargetZ)
            < minDist * minDist)
            return;

        state.TargetX = tx;
        state.TargetY = ty;
        state.TargetZ = tz;
        state.ViaPortal = via;
        state.FromAreaId = fromId;
        state.ToAreaId = toId;
        state.LastRetargetAt = now;
        state.NextReassertAt = now + DirectorStealReassertMin;
        NudgeDirectedSpot(bot, ref state, now);
    }

    private void ProgressDirectedLook(
        CCSBot bot, CCSPlayerPawn pawn, ref DirectedLookState state, float now)
    {
        if (now < state.NextReassertAt) return;
        if (now < state.LastYieldAt + DirectorAfterYieldGrace)
            return;

        var spot = bot.LookAtSpot;
        if (spot == null) return;

        float vsIntent = LookDist2(
            spot.X, spot.Y, spot.Z, state.TargetX, state.TargetY, state.TargetZ);
        float vsWrite = LookDist2(
            spot.X, spot.Y, spot.Z, state.WriteX, state.WriteY, state.WriteZ);
        bool stolen = vsWrite > DirectorSpotStolen * DirectorSpotStolen;
        bool needProgress = vsIntent > DirectorSpotOurs * DirectorSpotOurs;
        if (!stolen && !needProgress)
            return;

        bool expectReclaim = state.LastYieldAt > 0f
            && now < state.LastYieldAt + DirectorAfterYieldNoSteal;
        if (stolen && !expectReclaim)
        {
            if (state.Owner == LookOwner.Belief) _intelStealCount++;
            else _portalStealCount++;
        }

        state.NextReassertAt = now + DirectorStealReassertMin;
        NudgeDirectedSpot(bot, ref state, now);
    }

    private int ScoreLookSources(
        int botIndex, CCSPlayerPawn pawn, float now, out LookOwner owner)
    {
        owner = LookOwner.Idle;
        int best = 0;

        if (TryGetBestBelief(botIndex, now, out var threat, out _))
        {
            int s = threat.Kind switch
            {
                BeliefKind.Damage => DirectorScoreDamage,
                BeliefKind.Gun => DirectorScoreGun,
                BeliefKind.Heard => DirectorScoreHeard,
                BeliefKind.Seen => DirectorScoreSeen,
                _ => DirectorScoreHeard,
            };
            if (s > best)
            {
                best = s;
                owner = LookOwner.Belief;
            }
        }

        float vx = pawn.AbsVelocity.X;
        float vy = pawn.AbsVelocity.Y;
        bool moving = vx * vx + vy * vy >= PortalMinSpeed * PortalMinSpeed;
        if (moving)
        {
            int walk = _navGraphReady ? DirectorScorePortal : DirectorScoreClear;
            if (walk > best)
            {
                best = walk;
                owner = _navGraphReady ? LookOwner.Portal : LookOwner.Clear;
            }
        }

        return best;
    }

    // Higher number may interrupt a hold. Matches Axel's ladder: damage > gun > step > seen > portal.
    private static int LookPriority(LookOwner owner, BeliefKind kind)
    {
        if (owner == LookOwner.Portal || owner == LookOwner.Clear)
            return 1;
        if (owner != LookOwner.Belief)
            return 0;
        return kind switch
        {
            BeliefKind.Damage => 5,
            BeliefKind.Gun => 4,
            BeliefKind.Heard => 3,
            BeliefKind.Seen => 2,
            _ => 2,
        };
    }

    private bool TryBuildDirectedTarget(
        CCSPlayerPawn pawn, CCSBot bot, int botIndex, float now, LookOwner owner,
        out float tx, out float ty, out float tz,
        out bool via, out int fromId, out int toId, out BeliefKind kind)
    {
        tx = ty = tz = 0f;
        via = false;
        fromId = toId = -1;
        kind = BeliefKind.Heard;

        var origin = pawn.AbsOrigin;
        if (origin == null) return false;
        float eyeZ = origin.Z + Math.Clamp(pawn.ViewOffset?.Z ?? 64f, 40f, 72f);

        if (owner == LookOwner.Belief)
        {
            if (!TryGetBestBelief(botIndex, now, out var threat, out _))
                return false;
            kind = threat.Kind;
            return FillBeliefLookPoint(origin.X, origin.Y, origin.Z, eyeZ, threat,
                out tx, out ty, out tz, out via, out fromId, out toId);
        }

        if (owner == LookOwner.Portal)
        {
            float vx = pawn.AbsVelocity.X;
            float vy = pawn.AbsVelocity.Y;
            float speed = MathF.Sqrt(vx * vx + vy * vy);
            if (speed < 1f) return false;
            float hx = vx / speed;
            float hy = vy / speed;
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
            catch { /* schema */ }

            if (!TryGetNavAreaIndex(origin.X, origin.Y, origin.Z, out int areaIdx))
            {
                _portalMissCount++;
                return false;
            }
            if (!TryPickForwardPortal(areaIdx, hx, hy, eyeZ,
                    out tx, out ty, out tz, out fromId, out toId))
            {
                _portalMissCount++;
                return false;
            }
            via = true;
            return true;
        }

        if (owner == LookOwner.Clear)
            return TryComputeClearLook(pawn, out tx, out ty, out tz);

        return false;
    }

    private bool TryBuildBeliefKindTarget(
        CCSPlayerPawn pawn, int botIndex, float now, BeliefKind kind,
        out float tx, out float ty, out float tz,
        out bool via, out int fromId, out int toId)
    {
        tx = ty = tz = 0f;
        via = false;
        fromId = toId = -1;
        var origin = pawn.AbsOrigin;
        if (origin == null) return false;
        if (!TryGetBelief(botIndex, kind, now, out var threat))
            return false;
        float eyeZ = origin.Z + Math.Clamp(pawn.ViewOffset?.Z ?? 64f, 40f, 72f);
        return FillBeliefLookPoint(origin.X, origin.Y, origin.Z, eyeZ, threat,
            out tx, out ty, out tz, out via, out fromId, out toId);
    }

    private bool FillBeliefLookPoint(
        float ox, float oy, float oz, float eyeZ, in BeliefSpot threat,
        out float tx, out float ty, out float tz,
        out bool via, out int fromId, out int toId)
    {
        tx = threat.X;
        ty = threat.Y;
        tz = Math.Clamp(threat.Z, eyeZ - 48f, eyeZ + 24f);
        via = false;
        fromId = toId = -1;
        if (TryPickBeliefPortal(ox, oy, oz, eyeZ, threat.X, threat.Y,
                out float px, out float py, out float pz,
                out fromId, out toId))
        {
            tx = px;
            ty = py;
            tz = pz;
            via = true;
        }
        return true;
    }

    private void ApplyDirectedLook(
        CCSBot bot, CCSPlayerPawn pawn, ref DirectedLookState state, float now,
        bool soften, float yawStep, bool extendHold = false)
    {
        float tx = state.TargetX;
        float ty = state.TargetY;
        float tz = state.TargetZ;
        if (soften)
        {
            var origin = pawn.AbsOrigin;
            if (origin != null)
            {
                SoftenBeliefLookXy(pawn, origin.X, origin.Y, ref tx, ref ty,
                    out float holdMul, yawStep);
                if (extendHold)
                {
                    float extra = (holdMul - 1f) * (state.HoldUntil - now);
                    if (extra > 0f)
                        state.HoldUntil += extra;
                }
            }
        }

        if (!float.IsFinite(tx) || !float.IsFinite(ty) || !float.IsFinite(tz))
            return;

        state.WriteX = tx;
        state.WriteY = ty;
        state.WriteZ = tz;

        var spot = bot.LookAtSpot;
        if (spot != null)
        {
            spot.X = tx;
            spot.Y = ty;
            spot.Z = tz;
        }

        ref float lookDur = ref bot.LookAtSpotDuration;
        lookDur = Math.Max(0.12f, state.HoldUntil - now);
        ref float lookTs = ref bot.LookAtSpotTimestamp;
        lookTs = now;
        ref bool lookAttack = ref bot.LookAtSpotAttack;
        lookAttack = false;
        ref bool lookClear = ref bot.LookAtSpotClearIfClose;
        lookClear = false;
        ref float lookTol = ref bot.LookAtSpotAngleTolerance;
        lookTol = 14f;

        try
        {
            ref bool pathControl = ref bot.EyeAnglesUnderPathFinderControl;
            pathControl = false;
        }
        catch { /* schema */ }

        InhibitNativeLookAround(bot, state.HoldUntil + 0.18f);
    }

    // Move the existing LookAtSpot toward the true door in world units.
    // A 28° ray rewrite at 300–500u is a 120u+ jump every pulse (blob hear).
    private void NudgeDirectedSpot(CCSBot bot, ref DirectedLookState state, float now)
    {
        if (!float.IsFinite(state.TargetX) || !float.IsFinite(state.TargetY)
            || !float.IsFinite(state.TargetZ))
            return;

        float x = state.WriteX;
        float y = state.WriteY;
        float z = state.WriteZ;
        var spot = bot.LookAtSpot;
        if (spot != null && float.IsFinite(spot.X) && float.IsFinite(spot.Y) && float.IsFinite(spot.Z))
        {
            x = spot.X;
            y = spot.Y;
            z = spot.Z;
        }

        float dx = state.TargetX - x;
        float dy = state.TargetY - y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len > DirectorProgressStep && len > 1f)
        {
            float s = DirectorProgressStep / len;
            x += dx * s;
            y += dy * s;
        }
        else
        {
            x = state.TargetX;
            y = state.TargetY;
        }

        float dz = state.TargetZ - z;
        if (MathF.Abs(dz) > 24f)
            z += MathF.CopySign(24f, dz);
        else
            z = state.TargetZ;

        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
            return;

        state.WriteX = x;
        state.WriteY = y;
        state.WriteZ = z;
        if (spot != null)
        {
            spot.X = x;
            spot.Y = y;
            spot.Z = z;
        }

        ref float lookDur = ref bot.LookAtSpotDuration;
        lookDur = Math.Max(0.12f, state.HoldUntil - now);
        ref float lookTs = ref bot.LookAtSpotTimestamp;
        if (lookTs <= 0f)
            lookTs = now;
        ref bool lookAttack = ref bot.LookAtSpotAttack;
        lookAttack = false;
        ref bool lookClear = ref bot.LookAtSpotClearIfClose;
        lookClear = false;
        ref float lookTol = ref bot.LookAtSpotAngleTolerance;
        lookTol = 14f;

        try
        {
            ref bool pathControl = ref bot.EyeAnglesUnderPathFinderControl;
            pathControl = false;
        }
        catch { /* schema */ }

        InhibitNativeLookAround(bot, state.HoldUntil + 0.18f);
    }

    // Refresh duration / inhibit only. Combat leftover coords stay until grace ends.
    private void SustainDirectedLook(CCSBot bot, ref DirectedLookState state, float now)
    {
        float until = Math.Max(state.HoldUntil, now + 0.12f);
        if (state.LastYieldAt > 0f)
            until = Math.Max(until, state.LastYieldAt + DirectorAfterYieldGrace);

        ref float lookDur = ref bot.LookAtSpotDuration;
        ref float lookTs = ref bot.LookAtSpotTimestamp;
        float rem = lookTs + lookDur - now;
        float want = until - now;
        if (rem < want - 0.12f)
        {
            // Do not restart the look (lookTs = now); native treats that as a new flick.
            if (lookTs > 0f)
                lookDur = Math.Max(lookDur, until - lookTs);
            else
            {
                lookTs = now;
                lookDur = Math.Max(0.12f, want);
            }
        }

        InhibitNativeLookAround(bot, until + 0.18f);
    }
}
