using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;

namespace BotState;

// P5a/P5b — diagnose who owns bot eyes.
// Summary includes portal vs clear so we can measure P5b effect from logs.
public partial class BotState
{
    private enum LookOwner : byte
    {
        Idle = 0,
        Combat = 1,
        Flash = 2,
        Belief = 3, // director bucket; lookdebug maps to Dmg/Gun/Step/Seen
        Clear = 4,
        Native = 5,
        Portal = 6,
        Dmg = 7,
        Gun = 8,
        Step = 9,
        Seen = 10,
    }

    private struct LookSamplePrev
    {
        public float SpotX, SpotY, SpotZ;
        public bool HasSpot;
        public LookOwner Owner;
    }

    private bool _debugLook;
    private float _lookDebugNextSummaryAt;
    private int _lookDebugEventBudget;
    private int _lookSampleTick;
    private float _lookHeartbeatAt;
    private int _lookTickBotsSeen;
    private int _lookTickBotsSampled;
    private int _lookTickExceptions;
    private string _lookLastError = "";

    // Rolling owner histogram between summaries (must match LookOwner count).
    private readonly int[] _lookOwnerHits = new int[11];
    private int _lookSpotJumps;
    private int _lookSamples;
    private int _lookPortalWindowPicks;
    private int _lookPortalWindowMisses;
    private int _lookPortalWindowSteals;
    private int _lookIntelWindowSteals;

    private readonly Dictionary<int, LookSamplePrev> _lookPrev = new();

    private const float LookDebugSummaryInterval = 2.0f;
    private const int LookDebugEventsPerWindow = 3;
    private const float LookDebugActiveMinRem = 0.05f;
    private const float LookDebugSpotJumpMin = 120f;

    private void ClearLookDebugState()
    {
        _lookPrev.Clear();
        Array.Clear(_lookOwnerHits, 0, _lookOwnerHits.Length);
        _lookSpotJumps = 0;
        _lookSamples = 0;
        _lookDebugEventBudget = 0;
        _lookDebugNextSummaryAt = 0f;
        _lookHeartbeatAt = 0f;
        _lookTickBotsSeen = 0;
        _lookTickBotsSampled = 0;
        _lookTickExceptions = 0;
        _lookLastError = "";
        _lookPortalWindowPicks = 0;
        _lookPortalWindowMisses = 0;
        _lookPortalWindowSteals = 0;
        _lookIntelWindowSteals = 0;
    }

    private void TickLookDebugHeartbeat(float now)
    {
        if (!_debugLook) return;
        if (now < _lookHeartbeatAt) return;
        _lookHeartbeatAt = now + LookDebugSummaryInterval;

        string err = string.IsNullOrEmpty(_lookLastError) ? "-" : _lookLastError;
        // Heartbeat: logger only (every 2s). Avoid PrintToConsole spam.
        string msg =
            $"[look] heartbeat botsSeen={_lookTickBotsSeen} sampled={_lookTickBotsSampled} " +
            $"ex={_lookTickExceptions} humanlike={HumanlikeMode.Enabled} " +
            $"navReady={_navGraphReady} areas={_navBoxes.Length} portals={_navPortals.Length} " +
            $"status={_navLastStatus} err={err}";
        Logger.LogInformation("{Msg}", msg);
        Console.WriteLine(msg);

        _lookTickBotsSeen = 0;
        _lookTickBotsSampled = 0;
        _lookTickExceptions = 0;
        _lookLastError = "";
    }

    private void SampleLookOwnership(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        CCSBot bot,
        int botIndex,
        float now)
    {
        if (!_debugLook) return;

        _lookTickBotsSeen++;

        try
        {
            SampleLookOwnershipCore(player, pawn, bot, botIndex, now);
        }
        catch (Exception ex)
        {
            _lookTickExceptions++;
            _lookLastError = TruncateLookHint(ex.GetType().Name + ":" + ex.Message, 48);
        }
    }

    private void SampleLookOwnershipCore(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        CCSBot bot,
        int botIndex,
        float now)
    {
        _lookSampleTick++;
        if ((_lookSampleTick + botIndex) % 6 != 0)
            return;

        _lookTickBotsSampled++;

        LookOwner owner = ClassifyLookOwner(player, bot, botIndex, now);
        _lookOwnerHits[(int)owner]++;
        _lookSamples++;

        float rem = bot.LookAtSpotTimestamp + bot.LookAtSpotDuration - now;
        if (rem < 0f) rem = 0f;

        var spot = bot.LookAtSpot;
        bool hasSpot = spot != null;
        float sx = hasSpot ? spot!.X : 0f;
        float sy = hasSpot ? spot!.Y : 0f;
        float sz = hasSpot ? spot!.Z : 0f;

        bool jumped = false;
        bool hadPrev = _lookPrev.TryGetValue(botIndex, out var prev);
        if (hadPrev && prev.HasSpot && hasSpot && rem > LookDebugActiveMinRem)
        {
            float jdx = sx - prev.SpotX;
            float jdy = sy - prev.SpotY;
            float jdz = sz - prev.SpotZ;
            float j2 = jdx * jdx + jdy * jdy + jdz * jdz;
            if (j2 >= LookDebugSpotJumpMin * LookDebugSpotJumpMin)
            {
                _lookSpotJumps++;
                jumped = true;
            }
        }

        _lookPrev[botIndex] = new LookSamplePrev
        {
            SpotX = sx,
            SpotY = sy,
            SpotZ = sz,
            HasSpot = hasSpot,
            Owner = owner,
        };

        if (now >= _lookDebugNextSummaryAt)
            WriteLookSummary(now);

        bool interesting = jumped
            || (hadPrev && prev.Owner != owner
                && (owner == LookOwner.Native
                    || owner == LookOwner.Portal
                    || owner == LookOwner.Dmg
                    || owner == LookOwner.Gun
                    || owner == LookOwner.Step
                    || owner == LookOwner.Seen));
        if (!interesting)
            return;

        if (_lookDebugEventBudget <= 0)
        {
            if (now < _lookDebugNextSummaryAt) return;
            _lookDebugEventBudget = LookDebugEventsPerWindow;
        }

        _lookDebugEventBudget--;
        EmitLookBotLine(player, pawn, bot, botIndex, owner, rem, jumped, now);
    }

    private LookOwner ClassifyLookOwner(
        CCSPlayerController player, CCSBot bot, int botIndex, float now)
    {
        if (bot.IsEnemyVisible)
            return LookOwner.Combat;

        if (_flashPlantedUntil.TryGetValue(botIndex, out float until) && now < until)
            return LookOwner.Flash;

        if (_directedLook.TryGetValue(botIndex, out var dir)
            && dir.HasTarget && now < dir.HoldUntil)
        {
            if (dir.Owner == LookOwner.Belief)
                return MapBeliefKindToOwner(dir.BeliefKind);
            return dir.Owner;
        }

        float rem = bot.LookAtSpotTimestamp + bot.LookAtSpotDuration - now;
        if (rem > LookDebugActiveMinRem)
            return LookOwner.Native;

        return LookOwner.Idle;
    }

    private void EmitLookBotLine(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        CCSBot bot,
        int botIndex,
        LookOwner owner,
        float rem,
        bool jumped,
        float now)
    {
        var origin = pawn.AbsOrigin;
        if (origin == null) return;

        // Prefer cached ids — NEVER call GetClosestNavArea here.
        // CSS GetClosestNavArea → GetAllNavAreas → FindSignature every call,
        // which hitchs the server when lookdebug is on.
        int areaId = -1;
        string nameHint = "-";
        if (_directedLook.TryGetValue(botIndex, out var dir)
            && dir.HasTarget && now < dir.HoldUntil)
        {
            areaId = dir.FromAreaId;
            if (owner == LookOwner.Portal)
                nameHint = $"p{dir.FromAreaId}>{dir.ToAreaId}";
            else if (owner == LookOwner.Dmg || owner == LookOwner.Gun
                     || owner == LookOwner.Step || owner == LookOwner.Seen)
            {
                string gate = dir.ViaPortal ? $"{dir.FromAreaId}>{dir.ToAreaId}" : "blob";
                nameHint = $"{owner.ToString().ToLowerInvariant()}:{gate}";
            }
        }

        float eyeYaw = pawn.EyeAngles.Y;
        float lookYawDelta = 0f;
        var spot = bot.LookAtSpot;
        if (spot != null && rem > LookDebugActiveMinRem)
        {
            float dx = spot.X - origin.X;
            float dy = spot.Y - origin.Y;
            float wantYaw = MathF.Atan2(dy, dx) * (180f / MathF.PI);
            lookYawDelta = (float)Math.Abs(NormalizeAngleDeg(wantYaw - eyeYaw));
        }

        float goalYawDelta = 0f;
        try
        {
            var goal = bot.GoalPosition;
            if (goal != null)
            {
                float gdx = goal.X - origin.X;
                float gdy = goal.Y - origin.Y;
                if (gdx * gdx + gdy * gdy > 40f * 40f)
                {
                    float goalYaw = MathF.Atan2(gdy, gdx) * (180f / MathF.PI);
                    goalYawDelta = (float)Math.Abs(NormalizeAngleDeg(goalYaw - eyeYaw));
                }
            }
        }
        catch { /* schema missing */ }

        float vx = pawn.AbsVelocity.X;
        float vy = pawn.AbsVelocity.Y;
        float spd = MathF.Sqrt(vx * vx + vy * vy);

        string tag = jumped ? "jump" : "own";
        // Logger + server console only. PrintToConsole to humans was hitching view.
        string msg =
            $"[look] {tag} bot#{botIndex} {owner} rem={rem:F2}s area={areaId} " +
            $"yawD={lookYawDelta:F0} goalD={goalYawDelta:F0} spd={spd:F0} hint={nameHint}";
        Logger.LogInformation("{Msg}", msg);
        Console.WriteLine(msg);
    }

    private void WriteLookSummary(float now)
    {
        _lookDebugNextSummaryAt = now + LookDebugSummaryInterval;
        _lookDebugEventBudget = LookDebugEventsPerWindow;

        int total = Math.Max(1, _lookSamples);
        string Pct(LookOwner o)
        {
            int hits = _lookOwnerHits[(int)o];
            return $"{(100f * hits / total):F0}%";
        }

        int picks = _portalPickCount - _lookPortalWindowPicks;
        int misses = _portalMissCount - _lookPortalWindowMisses;
        int steals = _portalStealCount - _lookPortalWindowSteals;
        int isteals = _intelStealCount - _lookIntelWindowSteals;
        _lookPortalWindowPicks = _portalPickCount;
        _lookPortalWindowMisses = _portalMissCount;
        _lookPortalWindowSteals = _portalStealCount;
        _lookIntelWindowSteals = _intelStealCount;
        if (picks < 0) picks = 0;
        if (misses < 0) misses = 0;
        if (steals < 0) steals = 0;
        if (isteals < 0) isteals = 0;

        string msg =
            $"[look] summary n={_lookSamples} jumps={_lookSpotJumps} " +
            $"portal={Pct(LookOwner.Portal)} " +
            $"dmg={Pct(LookOwner.Dmg)} gun={Pct(LookOwner.Gun)} " +
            $"step={Pct(LookOwner.Step)} seen={Pct(LookOwner.Seen)} " +
            $"native={Pct(LookOwner.Native)} clear={Pct(LookOwner.Clear)} " +
            $"combat={Pct(LookOwner.Combat)} flash={Pct(LookOwner.Flash)} " +
            $"idle={Pct(LookOwner.Idle)} ppick={picks} pmiss={misses} " +
            $"psteal={steals} isteal={isteals}";
        // Summary stays on BroadcastDebug (rare, every 2s).
        BroadcastDebug(msg);

        Array.Clear(_lookOwnerHits, 0, _lookOwnerHits.Length);
        _lookSpotJumps = 0;
        _lookSamples = 0;
    }

    private static LookOwner MapBeliefKindToOwner(BeliefKind kind)
        => kind switch
        {
            BeliefKind.Damage => LookOwner.Dmg,
            BeliefKind.Gun => LookOwner.Gun,
            BeliefKind.Heard => LookOwner.Step,
            BeliefKind.Seen => LookOwner.Seen,
            _ => LookOwner.Step,
        };

    private static string TruncateLookHint(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "-";
        s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (s.Length <= max) return s;
        return s.Substring(0, max);
    }

    [ConsoleCommand("css_botstate_lookdebug", "Toggle look director debug (portal/dmg/gun/step/seen)")]
    [CommandHelper(minArgs: 0, usage: "[0|1]", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnLookDebugCmd(CCSPlayerController? caller, CommandInfo cmd)
    {
        if (cmd.ArgCount > 1)
        {
            string arg = cmd.GetArg(1);
            _debugLook = arg == "1"
                || arg.Equals("true", StringComparison.OrdinalIgnoreCase)
                || arg.Equals("on", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            _debugLook = !_debugLook;
        }

        if (_debugLook)
        {
            ClearLookDebugState();
            _lookDebugEventBudget = LookDebugEventsPerWindow;
            _lookDebugNextSummaryAt = 0f;
            _lookPortalWindowPicks = _portalPickCount;
            _lookPortalWindowMisses = _portalMissCount;
            _lookPortalWindowSteals = _portalStealCount;
            _lookIntelWindowSteals = _intelStealCount;
            // Force rebuild only on explicit debug arm — not during Load.
            try { EnsureNavGraph(force: true); }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "[Smarter-Bot] lookdebug nav ensure failed");
            }
        }

        string msg =
            $"[Smarter-Bot] look debug = {_debugLook} (summary every {LookDebugSummaryInterval:0}s; " +
            "owners: portal/dmg/gun/step/seen/native/clear/combat/flash/idle)";
        cmd.ReplyToCommand(msg);
        Logger.LogInformation("{Msg}", msg);
        Console.WriteLine(msg);
        if (_debugLook)
        {
            BroadcastDebug("[look] armed — waiting for OnTick samples");
            BroadcastDebug(
                $"[look] navReady={_navGraphReady} map={_navGraphMap} " +
                $"areas={_navBoxes.Length} portals={_navPortals.Length} buildMs={_navGraphBuildMs} " +
                $"status={_navLastStatus} attempts={_navEnsureAttempts}");
        }
    }

    [ConsoleCommand("css_botstate_navinfo", "Print nav portal graph stats (P5b)")]
    [CommandHelper(minArgs: 0, whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnNavInfoCmd(CCSPlayerController? caller, CommandInfo cmd)
    {
        if (!_navGraphReady)
            EnsureNavGraph(force: true);
        string msg =
            $"[Smarter-Bot] navReady={_navGraphReady} map={_navGraphMap} " +
            $"areas={_navBoxes.Length} portals={_navPortals.Length} buildMs={_navGraphBuildMs} " +
            $"picks={_portalPickCount} misses={_portalMissCount} " +
            $"status={_navLastStatus} attempts={_navEnsureAttempts}";
        cmd.ReplyToCommand(msg);
        Logger.LogInformation("{Msg}", msg);
        Console.WriteLine(msg);
    }

    [ConsoleCommand("css_botstate_navrebuild", "Rebuild nav portal graph for current map")]
    [CommandHelper(minArgs: 0, whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnNavRebuildCmd(CCSPlayerController? caller, CommandInfo cmd)
    {
        EnsureNavGraph(force: true);
        string msg =
            $"[Smarter-Bot] nav rebuild done ready={_navGraphReady} areas={_navBoxes.Length} " +
            $"portals={_navPortals.Length} buildMs={_navGraphBuildMs} status={_navLastStatus}";
        cmd.ReplyToCommand(msg);
        Logger.LogInformation("{Msg}", msg);
        Console.WriteLine(msg);
    }
}
