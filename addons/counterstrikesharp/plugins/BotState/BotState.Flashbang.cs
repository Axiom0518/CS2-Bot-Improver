using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace BotState;

public partial class BotState
{
    private const string BotBlindWindowsSignature =
        "40 53 48 81 EC ? ? ? ? 0F 29 B4 24 ? ? ? ? 48 8D 15";
    private const string BotBlindLinuxSignature =
        "55 48 8D 35 ? ? ? ? B8 ? ? ? ? F3 0F 5A D2";

    private bool _smokeConfigLoaded;
    private Vector? _scratchEye;

    private MemoryFunctionVoid<nint, float, float, float>? _botBlindFunction;

    private const float FlashFuseSeconds = 1.5f;        // CS2 flashbang fuse
    private const float FlashFovHorizDeg = 110f;        // bot horizontal cone (full angle)
    private const float FlashFovVertDeg = 90f;         // bot vertical cone (full angle)
    private const float FlashMatchSlackSeconds = 0.25f;
    // Human: perceive/decide ~200–230ms (bots slightly slower so not sweaty).
    private const float FlashPerceiveDelayMin = 0.20f;
    private const float FlashPerceiveDelayMax = 0.26f;
    // Physical flick window before boom ~120–150ms (slightly wider).
    private const float FlashTurnLeadMin = 0.13f;
    private const float FlashTurnLeadMax = 0.17f;
    // Late panic: still flick if notice leaves ≥ this much before boom.
    private const float FlashPanicMinWindow = 0.08f;
    private const float FlashPanicLeadMin = 0.08f;
    private const float FlashPanicLeadMax = 0.12f;
    // Stay looking away through the boom, then spend a clear window looking back.
    // (Old hold 50–90ms felt like "already returning at pop".)
    private const float FlashHoldAfterBoomMin = 0.10f;
    private const float FlashHoldAfterBoomMax = 0.16f;
    private const float FlashLookBackMin = 0.12f;
    private const float FlashLookBackMax = 0.18f;
    // Panic flick: only ±45° off the pre-turn yaw (not a full 180° whip).
    private const float FlashPanicYawDeg = 45f;
    // ~90–150° in the flick window — often partial, not god-tier 180°.
    private const float FlashTurnYawDegPerSecMin = 750f;
    private const float FlashTurnYawDegPerSecMax = 950f;
    private const float FlashTurnPitchDegPerSec = 450f;
    // Align with BotVision config defaults / ball-smoke (native density call disabled —
    // CSS MemoryFunction float/VectorWS ABI was returning garbage ~3e9).
    private const float FlashSmokeDefaultDensityThreshold = 0.23f;
    private const float FlashSmokeBallRadius = 150f;
    // Require a thick chord through smoke to hide a projectile (grazing edges still visible).
    private const float FlashSmokeMinBlockChord = 100f;

    private float _smokeDensityThreshold = FlashSmokeDefaultDensityThreshold;
    private readonly List<(float X, float Y, float Z)> _activeSmokeCenters = new();

    private readonly Dictionary<uint, float> _flashThrownAt = new();
    // Track sight + late/panic turn. Blindness at boom = facing geometry (no dice).
    private struct FlashDecision
    {
        public float FirstSeen;
        public float LastSeen;
        public float DetonateAt;
        public float TurnStartAt;
        public float LookBackStart;
        public float LookBackEnd;
        public float PerceiveDelay;
        public float TurnLead;
        public float YawSpeed;
        public bool CanAvoid;
        public bool IsPanic;
        public bool BlindApplied; // boom seen once (debug); keep decision for look-back
        public bool Turning;
        public bool SavedCrouching;
        public bool HasSavedPose;
        public float ReturnYaw;
        public float ReturnPitch;
        public float PanicYawOffset; // ±FlashPanicYawDeg when IsPanic
        public float FlashX;
        public float FlashY;
        public float FlashZ;
        public float RemainingAtSight;   // detonateAt - FirstSeen (for debug)
        public float DebugHoldRaw;       // native Blind hold (engine)
        public float DebugFadeRaw;       // native Blind fade (engine)
        public bool DebugHoldSet;
    }

    private readonly Dictionary<(int bot, uint flash), FlashDecision> _flashDecisions = new();
    private readonly HashSet<(int bot, uint flash)> _flashLookReleased = new();
    // Bot index → hold still through flash flick / look-back (skip crouch/move hacks).
    private readonly Dictionary<int, float> _flashPlantedUntil = new();

    // Debug: one-line blind report only (default off). Toggle: css_botstate_flashdebug
    private bool _debugFlash = false;

    [ConsoleCommand("css_botstate_flashdebug", "Toggle Smarter-Bot flashbang debug log")]
    [CommandHelper(minArgs: 0, usage: "[0|1]", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnFlashDebugCmd(CCSPlayerController? caller, CommandInfo cmd)
    {
        if (cmd.ArgCount > 1)
        {
            string arg = cmd.GetArg(1);
            _debugFlash = arg == "1"
                       || arg.Equals("true", StringComparison.OrdinalIgnoreCase)
                       || arg.Equals("on", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            _debugFlash = !_debugFlash;
        }

        cmd.ReplyToCommand($"[Smarter-Bot] flash debug = {_debugFlash}");
        Console.WriteLine($"[Smarter-Bot] flash debug = {_debugFlash}");
    }

    // Plugin log + server stdout + every connected human's console.
    private void BroadcastDebug(string msg)
    {
        Logger.LogInformation("{Msg}", msg);
        Console.WriteLine(msg);
        foreach (var p in Utilities.GetPlayers())
        {
            if (p == null || !p.IsValid || p.IsBot || p.IsHLTV) continue;
            p.PrintToConsole(msg);
        }
    }

    private HookResult OnPlayerBlind(EventPlayerBlind @event, GameEventInfo info)
    {
        var player = @event.Userid;

        if (player is null || !player.IsValid || !player.IsBot)
            return HookResult.Continue;
        if (player.HasBeenControlledByPlayerThisRound)
            return HookResult.Continue;

        int bidx = (int)player.Index;
        float engDur = @event.BlindDuration;

        bool hasMatchedDecision = TryMatchFlashDecision(
            bidx,
            Server.CurrentTime,
            out (int bot, uint flash) matchedKey,
            out FlashDecision matched);

        if (hasMatchedDecision && !matched.BlindApplied)
        {
            matched.BlindApplied = true;
            _flashDecisions[matchedKey] = matched;
        }

        // Blindness is 100% engine — no duration edits.
        // Debug duration = player_blind BlindDuration only.
        // Do NOT use Blind(hold)+fade: param2 is not a reliable independent
        // "fade seconds" to add (hold+that ≈ doubles full flashes to ~8–9s).
        if (_debugFlash)
        {
            string attackerName = "未知";
            var attacker = @event.Attacker;
            if (attacker != null && attacker.IsValid && !string.IsNullOrEmpty(attacker.PlayerName))
                attackerName = attacker.PlayerName;

            string victimName = string.IsNullOrEmpty(player.PlayerName) ? $"bot#{bidx}" : player.PlayerName;
            BroadcastDebug(
                $"[Flash] {attackerName} 对 {victimName} 造成了 {engDur:F2}s 闪光眩晕");
        }

        return HookResult.Continue;
    }

    // Finds the tracked flash whose predicted detonation is closest to the blind call
    private bool TryMatchFlashDecision(
        int botIndex,
        float now,
        out (int bot, uint flash) matchedKey,
        out FlashDecision matched)
    {
        matchedKey = default;
        matched = default;
        float bestDelta = float.MaxValue;
        bool found = false;

        foreach (var kvp in _flashDecisions)
        {
            if (kvp.Key.bot != botIndex) continue;

            float delta = Math.Abs(kvp.Value.DetonateAt - now);
            if (delta >= bestDelta || delta >= FlashMatchSlackSeconds) continue;

            bestDelta = delta;
            matchedKey = kvp.Key;
            matched = kvp.Value;
            found = true;
        }

        return found;
    }

    // Installs the platform-specific CCSBot::Blind Pre Hook
    private void InstallBotBlindHook()
    {
        string? signature = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? BotBlindWindowsSignature
            : RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? BotBlindLinuxSignature
                : null;

        if (signature == null)
        {
            Logger.LogWarning(
                "[Smarter-Bot] CCSBot::Blind hook is unavailable on this platform; using event fallback");
            return;
        }

        try
        {
            _botBlindFunction =
                new MemoryFunctionVoid<nint, float, float, float>(signature);
            _botBlindFunction.Hook(OnBotBlindPre, HookMode.Pre);
        }
        catch (Exception ex)
        {
            _botBlindFunction = null;
            Logger.LogError(ex,
                "[Smarter-Bot] CCSBot::Blind hook unavailable; using event fallback");
        }
    }

    // Removes the CCSBot::Blind Pre Hook during plugin unload
    private void UninstallBotBlindHook()
    {
        if (_botBlindFunction == null) return;

        try
        {
            _botBlindFunction.Unhook(OnBotBlindPre, HookMode.Pre);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex,
                "[Smarter-Bot] Failed to remove CCSBot::Blind hook cleanly");
        }
        _botBlindFunction = null;
    }

    // Records engine Blind(hold, fade, alpha) for debug only — never edits duration.
    private HookResult OnBotBlindPre(DynamicHook hook)
    {
        try
        {
            nint botAddress = hook.GetParam<nint>(0);
            if (botAddress == nint.Zero) return HookResult.Continue;

            CCSPlayerController? player = FindBotControllerByAddress(botAddress);
            if (player == null || player.HasBeenControlledByPlayerThisRound)
                return HookResult.Continue;

            int botIndex = (int)player.Index;
            if (!TryMatchFlashDecision(
                    botIndex,
                    Server.CurrentTime,
                    out (int bot, uint flash) matchedKey,
                    out FlashDecision matched))
            {
                return HookResult.Continue;
            }

            matched.BlindApplied = true;
            matched.DebugHoldRaw = hook.GetParam<float>(1);
            matched.DebugFadeRaw = hook.GetParam<float>(2);
            matched.DebugHoldSet = true;
            _flashDecisions[matchedKey] = matched;
            return HookResult.Continue;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex,
                "[Smarter-Bot] CCSBot::Blind hook failed open");
            return HookResult.Continue;
        }
    }

    // Resolves a native CCSBot pointer back to its player controller
    private static CCSPlayerController? FindBotControllerByAddress(nint botAddress)
    {
        foreach (var player in Utilities.GetPlayers())
        {
            if (!player.IsValid || !player.IsBot) continue;

            var pawn = player.PlayerPawn?.Value;
            if (pawn == null || !pawn.IsValid) continue;

            var bot = pawn.Bot;
            if (bot != null && bot.Handle == botAddress) return player;
        }

        return null;
    }

    // Player-like flash (realism over sweat):
    //   perceive 200–260ms → fight normally → flick away 130–170ms before boom →
    //   HOLD away 100–160ms after boom → look back 120–180ms → release.
    //   Panic: ±45° late flick; blindness is 100% CS2 engine (no facingScale). Ball-smoke hides 藏烟闪.
    private void ProcessFlashbangAvoidance()
    {
        if (_scratchEye == null) return;
        if (!_smokeConfigLoaded)
        {
            LoadBotVisionSmokeConfig();
            _smokeConfigLoaded = true;
        }

        float now = Server.CurrentTime;
        float dt = Server.TickInterval > 0f ? Server.TickInterval : 0.015625f;
        bool humanlike = true;

        var live = new Dictionary<uint, (Vector pos, float detonateAt)>();
        foreach (var ent in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("flashbang_projectile"))
        {
            if (!ent.IsValid) continue;
            var pos = ent.AbsOrigin;
            if (pos == null) continue;

            uint eidx = ent.Index;
            if (!_flashThrownAt.ContainsKey(eidx))
                _flashThrownAt[eidx] = now;
            live[eidx] = (pos, _flashThrownAt[eidx] + FlashFuseSeconds);
        }

        if (_flashThrownAt.Count > live.Count)
        {
            var stale = _flashThrownAt.Keys.Where(k => !live.ContainsKey(k)).ToList();
            foreach (var k in stale)
                _flashThrownAt.Remove(k);
        }

        if (_flashDecisions.Count > 0)
        {
            var expired = _flashDecisions.Where(kvp => now - kvp.Value.LookBackEnd > 2f)
                                          .Select(kvp => kvp.Key)
                                          .ToList();
            foreach (var k in expired)
            {
                _flashDecisions.Remove(k);
                _flashLookReleased.Remove(k);
            }
        }

        // Only rebuild smoke cache while flashes are in flight.
        if (live.Count > 0)
            RefreshActiveSmokeCenters();
        else
            _activeSmokeCenters.Clear();

        // Refresh planted windows; cleared bots drop out below.
        var plantedActive = new HashSet<int>();

        foreach (var bot in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
        {
            if (!bot.IsValid || !bot.IsBot) continue;
            if (bot.HasBeenControlledByPlayerThisRound) continue;

            var pawn = bot.PlayerPawn?.Value;
            if (pawn == null || !pawn.IsValid || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE) continue;

            var ccsBot = pawn.Bot;
            if (ccsBot == null) continue;

            int bidx = (int)bot.Index;

            foreach (var (fidx, entry) in live)
            {
                var fpos = entry.pos;
                float detonateAt = entry.detonateAt;
                if (now > detonateAt + 0.5f) continue;

                var key = (bidx, fidx);
                bool inFov = IsInFov(pawn, fpos, FlashFovHorizDeg, FlashFovVertDeg,
                                     out _, out _);
                bool canSee = false;
                if (inFov)
                {
                    if (BotCanSee(pawn, fpos)
                        && !IsFlashOccludedBySmoke(pawn, fpos, out _))
                        canSee = true;
                }

                if (!_flashDecisions.TryGetValue(key, out var decision))
                {
                    if (!canSee)
                        continue;

                    float perceive = humanlike
                        ? FlashPerceiveDelayMin + (float)_random.NextDouble() * (FlashPerceiveDelayMax - FlashPerceiveDelayMin)
                        : 0.12f + (float)_random.NextDouble() * 0.06f;
                    float turnLead = humanlike
                        ? FlashTurnLeadMin + (float)_random.NextDouble() * (FlashTurnLeadMax - FlashTurnLeadMin)
                        : 0.12f + (float)_random.NextDouble() * 0.04f;
                    float panicLead = humanlike
                        ? FlashPanicLeadMin + (float)_random.NextDouble() * (FlashPanicLeadMax - FlashPanicLeadMin)
                        : FlashPanicMinWindow;
                    float holdAfter = humanlike
                        ? FlashHoldAfterBoomMin + (float)_random.NextDouble() * (FlashHoldAfterBoomMax - FlashHoldAfterBoomMin)
                        : 0.05f;
                    float lookBack = humanlike
                        ? FlashLookBackMin + (float)_random.NextDouble() * (FlashLookBackMax - FlashLookBackMin)
                        : 0.10f;
                    float yawSpeed = FlashTurnYawDegPerSecMin
                        + (float)_random.NextDouble() * (FlashTurnYawDegPerSecMax - FlashTurnYawDegPerSecMin);

                    float perceiveEnd = now + perceive;
                    float turnStartAt = detonateAt - turnLead;
                    bool canAvoid = perceiveEnd <= turnStartAt;
                    bool isPanic = false;

                    // Late panic: notice completes with a short window left → still flick (partial facing).
                    if (!canAvoid)
                    {
                        float remainingAfterNotice = detonateAt - perceiveEnd;
                        if (remainingAfterNotice >= FlashPanicMinWindow)
                        {
                            isPanic = true;
                            turnStartAt = Math.Max(perceiveEnd, detonateAt - panicLead);
                            if (turnStartAt >= detonateAt)
                                turnStartAt = detonateAt - FlashPanicMinWindow;
                        }
                    }

                    float remainingAtSight = detonateAt - now;
                    decision = new FlashDecision
                    {
                        FirstSeen = now,
                        LastSeen = now,
                        DetonateAt = detonateAt,
                        TurnStartAt = turnStartAt,
                        LookBackStart = detonateAt + holdAfter,
                        LookBackEnd = detonateAt + holdAfter + lookBack,
                        PerceiveDelay = perceive,
                        TurnLead = canAvoid ? turnLead : panicLead,
                        YawSpeed = yawSpeed,
                        CanAvoid = canAvoid,
                        IsPanic = isPanic,
                        BlindApplied = false,
                        Turning = false,
                        SavedCrouching = false,
                        HasSavedPose = false,
                        ReturnYaw = pawn.EyeAngles.Y,
                        ReturnPitch = pawn.EyeAngles.X,
                        FlashX = fpos.X,
                        FlashY = fpos.Y,
                        FlashZ = fpos.Z,
                        RemainingAtSight = remainingAtSight,
                    };
                    _flashDecisions[key] = decision;
                    continue;
                }

                float holdSpan = Math.Clamp(decision.LookBackStart - decision.DetonateAt, FlashHoldAfterBoomMin, FlashHoldAfterBoomMax);
                float backSpan = Math.Clamp(decision.LookBackEnd - decision.LookBackStart, FlashLookBackMin, FlashLookBackMax);
                decision.DetonateAt = detonateAt;
                decision.LookBackStart = detonateAt + holdSpan;
                decision.LookBackEnd = decision.LookBackStart + backSpan;

                if (decision.IsPanic)
                {
                    decision.TurnStartAt = decision.FirstSeen + decision.PerceiveDelay;
                    float latest = detonateAt - FlashPanicMinWindow;
                    if (decision.TurnStartAt > latest)
                        decision.TurnStartAt = Math.Max(decision.FirstSeen, latest);
                }
                else
                {
                    float lead = Math.Clamp(decision.TurnLead, FlashTurnLeadMin, FlashTurnLeadMax);
                    decision.TurnStartAt = detonateAt - lead;
                }

                if (canSee)
                {
                    decision.LastSeen = now;
                    decision.FlashX = fpos.X;
                    decision.FlashY = fpos.Y;
                    decision.FlashZ = fpos.Z;
                }

                _flashDecisions[key] = decision;
            }

            foreach (var kvp in _flashDecisions.Where(k => k.Key.bot == bidx).ToList())
            {
                var key = kvp.Key;
                var decision = kvp.Value;

                if (!decision.CanAvoid && !decision.IsPanic)
                    continue;

                bool inAway = now >= decision.TurnStartAt && now < decision.LookBackStart;
                bool inBack = now >= decision.LookBackStart && now <= decision.LookBackEnd;

                if (inAway || inBack)
                {
                    if (!decision.Turning)
                    {
                        decision.Turning = true;
                        decision.SavedCrouching = ccsBot.IsCrouching;
                        decision.HasSavedPose = true;
                        decision.ReturnYaw = pawn.EyeAngles.Y;
                        decision.ReturnPitch = pawn.EyeAngles.X;
                        if (decision.IsPanic)
                            decision.PanicYawOffset = PickPanicYawOffset(pawn, decision);
                    }

                    if (inAway)
                    {
                        ApplyFlashViewTurn(ccsBot, pawn, decision, dt, away: true);
                    }
                    else
                    {
                        ApplyFlashViewTurn(ccsBot, pawn, decision, dt, away: false);
                    }

                    PlantFlashFeet(ccsBot, pawn, decision);
                    _flashPlantedUntil[bidx] = decision.LookBackEnd;
                    plantedActive.Add(bidx);
                    _flashDecisions[key] = decision;
                }
                else if (decision.Turning && now > decision.LookBackEnd
                         && _flashLookReleased.Add(key))
                {
                    ref float inhibit = ref ccsBot.InhibitLookAroundTimestamp;
                    inhibit = 0f;
                    ref bool pathControl = ref ccsBot.EyeAnglesUnderPathFinderControl;
                    pathControl = false;
                    // After flash: re-clear corners like a real player recovering vision.
                    ref int checkedHiding = ref ccsBot.CheckedHidingSpotCount;
                    checkedHiding = 0;
                    ref float lookAroundTs = ref ccsBot.LookAroundStateTimestamp;
                    lookAroundTs = 0f;
                }
            }
        }

        // Drop stale planted flags for bots no longer in a flash window.
        foreach (var idx in _flashPlantedUntil.Keys.ToList())
        {
            if (!plantedActive.Contains(idx) && now >= _flashPlantedUntil[idx])
                _flashPlantedUntil.Remove(idx);
        }
    }

    // View flick only; preserve crouch; kill horizontal slide for the short window.
    // Full: face away from flash (~180°). Panic: ReturnYaw ± 45° (side that leaves flash).
    private void ApplyFlashViewTurn(
        CCSBot bot, CCSPlayerPawn pawn, FlashDecision decision, float dt, bool away)
    {
        float targetYaw;
        float targetPitch;

        if (away)
        {
            if (decision.IsPanic)
            {
                float off = decision.PanicYawOffset;
                if (MathF.Abs(off) < 1f) off = FlashPanicYawDeg;
                targetYaw = decision.ReturnYaw + off;
                targetPitch = Math.Clamp(decision.ReturnPitch * 0.2f, -8f, 8f);
            }
            else
            {
                var origin = pawn.AbsOrigin;
                if (origin == null) return;
                float eyeX = origin.X;
                float eyeY = origin.Y;
                float dx = decision.FlashX - eyeX;
                float dy = decision.FlashY - eyeY;
                float len = MathF.Sqrt(dx * dx + dy * dy);
                if (len < 1f) len = 1f;
                float awayX = eyeX - dx / len * 200f;
                float awayY = eyeY - dy / len * 200f;
                targetYaw = MathF.Atan2(awayY - eyeY, awayX - eyeX) * (180f / MathF.PI);
                targetPitch = Math.Clamp(decision.ReturnPitch * 0.2f, -8f, 8f);
            }
        }
        else
        {
            targetYaw = decision.ReturnYaw;
            targetPitch = decision.ReturnPitch;
        }

        float curYaw = pawn.EyeAngles.Y;
        float curPitch = pawn.EyeAngles.X;
        float yawSpeed = decision.YawSpeed > 1f ? decision.YawSpeed : FlashTurnYawDegPerSecMin;
        float newYaw = MoveAngleToward(curYaw, targetYaw, yawSpeed * dt);
        float newPitch = MoveAngleToward(curPitch, targetPitch, FlashTurnPitchDegPerSec * dt);

        ref float lookYaw = ref bot.LookYaw;
        lookYaw = newYaw;
        ref float lookPitch = ref bot.LookPitch;
        lookPitch = newPitch;
        ref float lookYawVel = ref bot.LookYawVel;
        lookYawVel = 0f;
        ref float lookPitchVel = ref bot.LookPitchVel;
        lookPitchVel = 0f;

        ref float lookDur = ref bot.LookAtSpotDuration;
        lookDur = 0f;
        ref float lookTs = ref bot.LookAtSpotTimestamp;
        lookTs = 0f;

        ref bool pathControl = ref bot.EyeAnglesUnderPathFinderControl;
        pathControl = false;

        // Yaw-only Teleport. Pitch on AbsRotation tilts the whole pawn model until
        // a jump/land resets it — that was the lingering "leaning bot" after flashes.
        // Vertical aim stays on LookPitch above.
        pawn.Teleport(null, new QAngle(0f, newYaw, 0f), null);
    }

    // Pick ±45° so the flash ends up more to the side/behind relative to new facing.
    private static float PickPanicYawOffset(CCSPlayerPawn pawn, in FlashDecision decision)
    {
        var origin = pawn.AbsOrigin;
        if (origin == null) return FlashPanicYawDeg;

        float eyeX = origin.X;
        float eyeY = origin.Y;
        float dx = decision.FlashX - eyeX;
        float dy = decision.FlashY - eyeY;

        float yawRad = decision.ReturnYaw * (MathF.PI / 180f);
        float fx = MathF.Cos(yawRad);
        float fy = MathF.Sin(yawRad);
        // 2D cross: flash left of view => positive => turn right (−45°)
        float cross = fx * dy - fy * dx;
        return cross >= 0f ? -FlashPanicYawDeg : FlashPanicYawDeg;
    }

    private static void PlantFlashFeet(CCSBot bot, CCSPlayerPawn pawn, in FlashDecision decision)
    {
        if (decision.HasSavedPose)
        {
            ref bool crouch = ref bot.IsCrouching;
            crouch = decision.SavedCrouching;
        }

        // Stop walk/strafe during the flick window — view only.
        pawn.AbsVelocity.X = 0f;
        pawn.AbsVelocity.Y = 0f;
    }

    private static float MoveAngleToward(float current, float target, float maxDelta)
    {
        float delta = (float)NormalizeAngleDeg(target - current);
        if (MathF.Abs(delta) <= maxDelta) return target;
        return current + MathF.Sign(delta) * maxDelta;
    }

    // Decoupled horizontal/vertical FOV check. Source 2 QAngle convention:
    //   EyeAngles.Y = yaw   (0 deg => +X axis, 90 deg => +Y axis)
    //   EyeAngles.X = pitch (positive => looking DOWN; this is the Quake/Source convention)
    // Returns true when target is inside both cones; outDeltaYaw/outDeltaPitch are
    // signed angle deltas (target relative to bot view) for debug logging.
    private static bool IsInFov(CCSPlayerPawn pawn, Vector target,
                                float horizDeg, float vertDeg,
                                out float outDeltaYaw, out float outDeltaPitch)
    {
        outDeltaYaw = 0f;
        outDeltaPitch = 0f;

        var origin = pawn.AbsOrigin;
        if (origin == null) return false;

        float eyeZ = origin.Z + pawn.ViewOffset.Z;

        double dx = target.X - origin.X;
        double dy = target.Y - origin.Y;
        double dz = target.Z - eyeZ;

        double horizDist = Math.Sqrt(dx * dx + dy * dy);
        if (horizDist < 1e-3 && Math.Abs(dz) < 1e-3) return true;

        double yawToTarget = Math.Atan2(dy, dx) * 180.0 / Math.PI;
        double pitchToTarget = -Math.Atan2(dz, horizDist) * 180.0 / Math.PI;

        double yawDelta = NormalizeAngleDeg(yawToTarget - pawn.EyeAngles.Y);
        double pitchDelta = NormalizeAngleDeg(pitchToTarget - pawn.EyeAngles.X);

        outDeltaYaw = (float)yawDelta;
        outDeltaPitch = (float)pitchDelta;

        return Math.Abs(yawDelta) <= horizDeg * 0.5
            && Math.Abs(pitchDelta) <= vertDeg * 0.5;
    }

    private static double NormalizeAngleDeg(double a)
    {
        a %= 360.0;
        if (a > 180.0) a -= 360.0;
        if (a < -180.0) a += 360.0;
        return a;
    }

    private bool BotCanSee(CCSPlayerPawn pawn, Vector target)
    {
        if (_scratchEye == null) return false;

        var origin = pawn.AbsOrigin;
        if (origin == null) return false;

        _scratchEye.X = origin.X;
        _scratchEye.Y = origin.Y;
        _scratchEye.Z = origin.Z + pawn.ViewOffset.Z;

        var opts = new TraceOptions { InteractsWith = Masks.SolidBrushOnly };
        var result = Trace.TraceEndShape(_scratchEye, target, pawn, opts);
        return result.Fraction >= 0.999f;
    }

    // Reads density_threshold from addons/BotVision/config.json (chord thickness).
    private void LoadBotVisionSmokeConfig()
    {
        _smokeDensityThreshold = FlashSmokeDefaultDensityThreshold;
        try
        {
            string[] candidates =
            {
                Path.GetFullPath(Path.Combine(ModuleDirectory, "..", "..", "..", "BotVision", "config.json")),
                Path.GetFullPath(Path.Combine(ModuleDirectory, "..", "..", "..", "..", "BotVision", "config.json")),
            };

            foreach (string path in candidates)
            {
                if (!File.Exists(path)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("smoke", out var smoke)
                    && smoke.TryGetProperty("density_threshold", out var thr)
                    && thr.TryGetSingle(out float value)
                    && value >= 0f)
                {
                    _smokeDensityThreshold = value;
                    Logger.LogInformation(
                        "[Smarter-Bot] BotVision smoke density_threshold={0:F2} ({1})",
                        _smokeDensityThreshold, path);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Smarter-Bot] failed reading BotVision config; using default density_threshold");
        }
    }

    private void RefreshActiveSmokeCenters()
    {
        _activeSmokeCenters.Clear();
        foreach (var ent in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("smokegrenade_projectile"))
        {
            if (!ent.IsValid) continue;
            var pos = ent.AbsOrigin;
            if (pos == null) continue;
            if (!IsSmokeProjectileActive(ent)) continue;
            _activeSmokeCenters.Add((pos.X, pos.Y, pos.Z));
        }
    }

    private static bool IsSmokeProjectileActive(CBaseEntity ent)
    {
        try
        {
            if (Schema.GetRef<bool>(ent.Handle, "CSmokeGrenadeProjectile", "m_bDidSmokeEffect"))
                return true;
        }
        catch { }

        try
        {
            int tickBegin = Schema.GetRef<int>(ent.Handle, "CSmokeGrenadeProjectile", "m_nSmokeEffectTickBegin");
            if (tickBegin > 0) return true;
        }
        catch { }

        return false;
    }

    // Resolves smoke occlusion for flash *tracking* (not final blind):
    // ball-smoke only. Native GetSmokeDensityInLine via CSS MemoryFunction returned
    // garbage dens (~3e9) and falsely REJECT-SMOKE'd clear LOS — disabled for now.
    // Threshold still read from BotVision config.json for chord thickness.
    private bool IsFlashOccludedBySmoke(CCSPlayerPawn pawn, Vector flashPos, out string reason)
    {
        reason = "";
        var origin = pawn.AbsOrigin;
        if (origin == null) return false;
        if (_activeSmokeCenters.Count == 0) return false;

        float eyeX = origin.X;
        float eyeY = origin.Y;
        float eyeZ = origin.Z + pawn.ViewOffset.Z;

        return IsOccludedBySmokeBalls(
            eyeX, eyeY, eyeZ,
            flashPos.X, flashPos.Y, flashPos.Z,
            out reason);
    }

    // 藏烟闪: flash inside cloud, or LOS punches a thick chord through smoke.
    private bool IsOccludedBySmokeBalls(
        float x0, float y0, float z0,
        float x1, float y1, float z1,
        out string reason)
    {
        reason = "";
        if (_activeSmokeCenters.Count == 0) return false;

        float r = FlashSmokeBallRadius;
        float r2 = r * r;
        float blockChord = Math.Max(
            FlashSmokeMinBlockChord,
            2f * r * _smokeDensityThreshold);

        float dx = x1 - x0;
        float dy = y1 - y0;
        float dz = z1 - z0;
        float len2 = dx * dx + dy * dy + dz * dz;
        if (len2 < 1f) len2 = 1f;
        float len = MathF.Sqrt(len2);

        for (int i = 0; i < _activeSmokeCenters.Count; i++)
        {
            var smoke = _activeSmokeCenters[i];
            float fx = x1 - smoke.X;
            float fy = y1 - smoke.Y;
            float fz = z1 - smoke.Z;
            if (fx * fx + fy * fy + fz * fz <= r2)
            {
                reason = "flash-in-cloud";
                return true;
            }

            float ox = x0 - smoke.X;
            float oy = y0 - smoke.Y;
            float oz = z0 - smoke.Z;
            float a = len2;
            float b = 2f * (ox * dx + oy * dy + oz * dz);
            float c = ox * ox + oy * oy + oz * oz - r2;
            float disc = b * b - 4f * a * c;
            if (disc <= 0f) continue;

            float sqrtDisc = MathF.Sqrt(disc);
            float t0 = (-b - sqrtDisc) / (2f * a);
            float t1 = (-b + sqrtDisc) / (2f * a);
            float enter = Math.Clamp(Math.Min(t0, t1), 0f, 1f);
            float leave = Math.Clamp(Math.Max(t0, t1), 0f, 1f);
            float chord = (leave - enter) * len;
            if (chord >= blockChord)
            {
                reason = $"chord={chord:F0}";
                return true;
            }
        }

        return false;
    }

    // Clears flashbang state owned by the completed round.
    private void ResetFlashbangRoundState()
    {
        _flashThrownAt.Clear();
        _flashDecisions.Clear();
        _flashLookReleased.Clear();
        _flashPlantedUntil.Clear();
        _activeSmokeCenters.Clear();
    }
}
