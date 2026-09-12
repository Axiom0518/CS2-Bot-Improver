using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Core.Capabilities;
using BotControllerApi;
using Microsoft.Extensions.Logging;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace BotState;

public partial class BotState : BasePlugin
{
    public override string ModuleName => "Smarter-Bot";
    public override string ModuleVersion => "1.14.4";
    public override string ModuleAuthor => "ed0ard & XBribo & unicbm";
    public override string ModuleDescription => "Make bots smarter";

    private const float HurtRevealSeconds = 0.8f;
    private const float DefuseRevealSeconds = 1.5f;
    private const float DefuseHiddenSeconds = 3.5f;
    private const int KnifeDefinitionIndex = 9001;
    private const float ReloadInterruptCooldown = 0.75f;
    private const ulong InspectButtonMask = (ulong)PlayerButtons.Inspect;
    private const ulong UseButtonMask = (ulong)PlayerButtons.Use;
    private const float FakeDefuseHoldMinSeconds = 0.1f;
    private const float FakeDefuseHoldMaxSeconds = 0.8f;
    private const float FakeDefuseSearchMinSeconds = 2.0f;
    private const float FakeDefuseSearchMaxSeconds = 4.0f;
    private const string DefuseBombWindowsSignature =
        "48 8D 91 08 04 00 00 E9 ? ? ? ?";
    private const string DefuseBombLinuxSignature =
        "48 8D B7 00 04 00 00 E9 ? ? ? ?";
    private const string BotBlindWindowsSignature =
        "40 53 48 81 EC ? ? ? ? 0F 29 B4 24 ? ? ? ? 48 8D 15";
    private const string BotBlindLinuxSignature =
        "55 48 8D 35 ? ? ? ? B8 ? ? ? ? F3 0F 5A D2";

    // 360 FOV patch constants for fake defuse search phase
    private const uint PageExecuteReadWrite = 0x40;

    private bool _isBombBeingDefused = false;
    private int _defuserSlot = -1;
    private float _defuseRevealUntil = 0f;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _defuseRevealTimer = null;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _gunReequipTimer = null;

    private readonly Dictionary<int, float> _revealUntil = new();

    private readonly Random _random = new Random();

    private readonly Dictionary<int, bool> _prevInAir = new();
    private readonly Dictionary<int, float> _lastForwardDir = new();
    private readonly Dictionary<int, float> _ladderExitTime = new();
    private readonly Dictionary<int, float> _lastLateralDir = new();
    private readonly Dictionary<int, float> _doorEventCooldown = new();

    private readonly Dictionary<int, float> _stuckStartTime = new();
    private readonly Dictionary<int, Vector> _stuckStartPos = new();
    private readonly Dictionary<int, bool> _stuckJumpDone = new();
    private readonly Dictionary<int, int> _stuckJumpCount = new();
    private readonly Dictionary<int, float> _stuckMaxSpeed = new();
    private readonly Dictionary<int, float> _idleStartTime = new();
    private readonly Dictionary<int, float> _lastRepathTime = new();
    private readonly Dictionary<int, float> _reloadInterruptCooldown = new();
    // Last non-trivial horizontal move dir (unit) — used to keep walking while Valve
    // ClearMovement freezes bots during reload when no enemy is visible.
    private readonly Dictionary<int, (float X, float Y)> _lastMoveDir = new();
    private readonly Dictionary<int, float> _fakeDefuseCooldown = new();
    private readonly Dictionary<nint, float> _fakeDefuseGuardUntil = new();
    private readonly Dictionary<nint, int> _fakeDefuseCounts = new();
    private readonly HashSet<int> _fakeDefuseSearchingBots = new();
    private readonly HashSet<int> _pendingDefuseRestore = new();
    private readonly Dictionary<int, long> _fakeDefuseSuppressionIds = new();
    private bool _isFreezeTime = false;

    // 360 FOV patch tracking for fake defuse search
    private sealed record FovPatchDefinition(
        string Name,
        string Signature,
        int Offset,
        byte[] Expected,
        byte[] Replacement);

    private sealed record AppliedFovPatch(
        string Name,
        nint Address,
        byte[] Original);

    private static readonly FovPatchDefinition[] FovPatches =
    [
        new(
            "IsVisiblePos_IgnoreFOV",
            "48 8D 05 ? ? ? ? 48 C7 45 98 1F 01 00 00 48 89 45 90 45 0F B6 E8 0F 10 45 90",
            19,
            [0x45, 0x0F, 0xB6, 0xE8],       // movzx r13d, r8b
            [0x45, 0x33, 0xED, 0x90]),      // xor r13d, r13d; nop

        new(
            "IsVisiblePlayer_IgnoreFOV",
            "48 8D 05 ? ? ? ? 48 C7 45 CF 4D 01 00 00 48 89 45 C7 41 0F B6 D8 0F 10 45 C7",
            19,
            [0x41, 0x0F, 0xB6, 0xD8],       // movzx ebx, r8b
            [0x33, 0xDB, 0x90, 0x90]),      // xor ebx, ebx; nop; nop
    ];

    private static readonly FovPatchDefinition[] LinuxFovPatches =
    [
        new(
            "IsVisiblePos_IgnoreFOV",
            "80 BD ? ? ? ? 00 74 ? 48 8B 7B 18 48 8B B5 ? ? ? ? 48 8B 07 FF 90 A0 09 00 00 84 C0 74 ?",
            7,
            [0x74],                         // je no-FOV path
            [0xEB]),                        // jmp no-FOV path

        new(
            "IsVisiblePlayer_IgnoreFOV",
            "45 84 F6 74 ? 49 8B 54 24 18 48 89 DF 48 8B 0A 48 89 55 98 48 8B 89 A0 09 00 00 48 89 4D A0 FF 90 B8 02 00 00",
            3,
            [0x74],                         // je no-FOV path
            [0xEB]),                        // jmp no-FOV path
    ];

    private const int LinuxPageRead = 0x1;
    private const int LinuxPageWrite = 0x2;
    private const int LinuxPageExecute = 0x4;
    private const int LinuxPageExecuteReadWrite =
        LinuxPageRead | LinuxPageWrite | LinuxPageExecute;
    private const int LinuxPageExecuteRead = LinuxPageRead | LinuxPageExecute;

    private readonly List<AppliedFovPatch> _appliedFovPatches = [];
    private bool _fovPatchesAvailable = false;

    private readonly HashSet<int> _hasFiredThisAttack = new();
    private readonly Dictionary<int, bool> _prevIsAttacking = new();

    private readonly Dictionary<int, bool> _cachedInAir = new();
    private readonly Dictionary<int, bool> _cachedNearLadder = new();

    // Flashbang avoidance
    private Vector? _scratchEye;

    private readonly HashSet<int> _knifeLockedBotSlots = new();
    private object? _botController;
    private MemoryFunctionVoid<nint>? _defuseBombFunction;
    private MemoryFunctionVoid<nint, float, float, float>? _botBlindFunction;
    private bool _eliminationHandled;

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
    //---------------------------------------------------------------------------------------
    // Registers game events and the per-tick bot behavior listener
    public override void Load(bool hotReload)
    {
        InstallDefuseBombHook();
        InstallBotBlindHook();
        LoadBotVisionSmokeConfig();
        InitializeFovPatches();
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterEventHandler<EventRoundFreezeEnd>(OnRoundFreezeEnd);
        RegisterEventHandler<EventPlayerBlind>(OnPlayerBlind);
        RegisterEventHandler<EventBombPlanted>(OnBombPlanted);
        RegisterEventHandler<EventBombBegindefuse>(OnBombBeginDefuse);
        RegisterEventHandler<EventBombAbortdefuse>(OnBombAbortDefuse);
        RegisterEventHandler<EventBombDefused>(OnBombDefused);
        RegisterEventHandler<EventBombExploded>(OnBombExploded);
        RegisterEventHandler<EventDoorOpen>(OnDoorOpen);
        RegisterEventHandler<EventDoorClose>(OnDoorClose);
        RegisterEventHandler<EventWeaponFire>(OnWeaponFire);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        RegisterEventHandler<EventPlayerSound>(OnPlayerSoundBelief);
        RegisterListener<Listeners.OnTick>(OnTick);
        // Prevent bots from holding their knives when there're enemies alive
        _gunReequipTimer = AddTimer(
            1.0f,
            ReequipGunForActiveBots,
            CounterStrikeSharp.API.Modules.Timers.TimerFlags.REPEAT);

        Console.WriteLine(
            $"[Smarter-Bot] v{ModuleVersion} ready (intel + decisions + flash)");
        Logger.LogInformation(
            "[Smarter-Bot] v{Version} ready (intel + decisions + flash)", ModuleVersion);
    }

    // Resolves capabilities supplied by plugins after every plugin has loaded
    public override void OnAllPluginsLoaded(bool hotReload)
    {
        _scratchEye = new Vector();
        try { _botController = BotControllerBridge.TryGet(); } catch { _botController = null; }
        if (_botController == null)
            Console.WriteLine("[Smarter-Bot] BotController API not available");
    }

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
    //---------------------------------------------------------------------------------------
    // Reveals whoever hurt a Bot to every Bot through smoke for 1 second.
    // Further damage from the same source only pushes the window out. Windows never stack.
    private HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo _)
    {
        try
        {
            var victim = @event.Userid;
            if (victim == null || !victim.IsValid || !victim.IsBot) return HookResult.Continue;

            // World damage has no attacker, and self damage is nobody hurting
            // anyone else.
            var attacker = @event.Attacker;
            if (attacker == null || !attacker.IsValid || attacker.Slot == victim.Slot)
                return HookResult.Continue;

            RevealThroughSmoke(attacker.Slot, HurtRevealSeconds);

            // Humanlike: brief alert bump on taking damage (instead of perpetual alert wipe).
            if (HumanlikeMode.Enabled)
            {
                var pawn = victim.PlayerPawn?.Value;
                var bot = pawn?.Bot;
                if (bot != null)
                {
                    float now = Server.CurrentTime;
                    CountdownTimer alertTimer = bot.AlertTimer;
                    ref float alertduration = ref alertTimer.Duration;
                    alertduration = 3.0f;
                    ref float alerttimestamp = ref alertTimer.Timestamp;
                    alerttimestamp = now + alertduration;
                    ref float alerttimescale = ref alertTimer.Timescale;
                    alerttimescale = 1.0f;
                }

                // Directional hit cue — look along the shot, not at live attacker XYZ.
                RecordDamageBelief(victim, attacker);
            }
        }
        catch { }
        return HookResult.Continue;
    }
    //---------------------------------------------------------------------------------------
    // Hands one player slot to BotVision and extends its reveal window.
    private void RevealThroughSmoke(int slot, float seconds)
    {
        if (slot < 0) return;

        float until = Server.CurrentTime + seconds;
        if (_revealUntil.TryGetValue(slot, out float current))
        {
            if (until > current) _revealUntil[slot] = until;
            return;
        }

        _revealUntil[slot] = until;
        Server.ExecuteCommand($"bv_reveal add {slot}");
    }

    // Drops one reveal in BotVision and locally
    private void EndReveal(int slot)
    {
        if (!_revealUntil.Remove(slot)) return;
        Server.ExecuteCommand($"bv_reveal remove {slot}");
    }

    // Releases every reveal whose window has run out
    private void ExpireReveals(float now)
    {
        if (_revealUntil.Count == 0) return;

        List<int>? expired = null;
        foreach (var kvp in _revealUntil)
        {
            if (now < kvp.Value) continue;
            (expired ??= new List<int>()).Add(kvp.Key);
        }
        if (expired == null) return;

        foreach (int slot in expired) EndReveal(slot);
    }

    // Drops every reveal, including any BotVision still holds for a slot this
    // plugin has stopped tracking
    private void ClearReveals()
    {
        _revealUntil.Clear();
        Server.ExecuteCommand("bv_reveal clear");
    }

    // Restores plugin-owned state before the plugin unloads
    public override void Unload(bool hotReload)
    {
        CancelAllFakeDefuseSuppressions();
        UninstallBotBlindHook();
        UninstallDefuseBombHook();
        RestoreAllFovPatches();
        ReleaseKnifeLocks();
        ClearReveals();
        _defuseRevealTimer?.Kill();
        _gunReequipTimer?.Kill();
    }

    private HookResult OnBombAbortDefuse(EventBombAbortdefuse @event, GameEventInfo info)
    {
        StopDefuseReveal();
        return HookResult.Continue;
    }

    private HookResult OnBombDefused(EventBombDefused @event, GameEventInfo info)
    {
        StopDefuseReveal();
        return HookResult.Continue;
    }

    private HookResult OnBombExploded(EventBombExploded @event, GameEventInfo info)
    {
        StopDefuseReveal();
        return HookResult.Continue;
    }

    private void StopDefuseReveal()
    {
        _isBombBeingDefused = false;
        _defuseRevealTimer?.Kill();
        _defuseRevealTimer = null;

        // Keep the reveal only when damage extended it past the defuse window
        if (_defuserSlot >= 0 &&
            _revealUntil.TryGetValue(_defuserSlot, out float until) &&
            until <= _defuseRevealUntil)
        {
            EndReveal(_defuserSlot);
        }
        _defuserSlot = -1;
        _defuseRevealUntil = 0f;
    }

    // Reveals the bomb-defuser for 1.5s out of every 5s of defusing
    private void StartDefuseRevealCycle()
    {
        if (_defuseRevealTimer != null) return;

        _defuseRevealTimer = AddTimer(DefuseHiddenSeconds, () =>
        {
            _defuseRevealTimer = null;
            if (!_isBombBeingDefused) return;

            _defuseRevealUntil = Server.CurrentTime + DefuseRevealSeconds;
            RevealThroughSmoke(_defuserSlot, DefuseRevealSeconds);

            AddTimer(DefuseRevealSeconds, () =>
            {
                if (_isBombBeingDefused) StartDefuseRevealCycle();
            });
        });
    }
    //---------------------------------------------------------------------------------------
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
    //---------------------------------------------------------------------------------------
    [GameEventHandler]
    public HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid || !player.IsBot)
            return HookResult.Continue;

        Server.NextFrame(() =>
        {
            if (player == null || !player.IsValid) return;
            ApplyBotState(player);
        });

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnRoundFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
    {
        _isFreezeTime = false;
        foreach (var player in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
        {
            if (!player.IsValid || !player.IsBot) continue;
            ApplyBotState(player);
        }
        return HookResult.Continue;
    }
    //---------------------------------------------------------------------------------------
    private void OnTick()
    {
        ProcessFlashbangAvoidance();
        ExpireReveals(Server.CurrentTime);

        foreach (var player in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
        {
            if (!player.IsValid || !player.IsBot)
                continue;

            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid)
                continue;

            var bot = pawn.Bot;
            if (bot == null)
                continue;
            // In case the bot has been taken over
            bool isTakenOver = player.HasBeenControlledByPlayerThisRound;
            if (isTakenOver) continue;

            int idx = (int)player.Index;
            float now = Server.CurrentTime;
            InterruptReload(player, pawn, bot, now);
            KeepWalkingWhileReload(player, pawn, bot, idx);
            // Door Stuck Fix
            bool inDoorCooldown = _doorEventCooldown.TryGetValue(idx, out float doorCooldownEnd) && now < doorCooldownEnd;

            ref bool isSleeping = ref bot.IsSleeping;
            isSleeping = false;

            ref bool allowActive = ref bot.AllowActive;
            allowActive = true;

            // Soft humanlike: keep combat readiness, but leave peripheral/panic/surprise
            // so bots still have a reaction window (no instant lock).
            if (HumanlikeMode.Enabled)
            {
                ref bool isRapidFiring = ref bot.IsRapidFiring;
                isRapidFiring = true;

                ref float fireWeaponTimestamp = ref bot.FireWeaponTimestamp;
                fireWeaponTimestamp = 0.0f;

                CountdownTimer ignoreEnemiesTimer = bot.IgnoreEnemiesTimer;
                ref float ignoreEnemiesduration = ref ignoreEnemiesTimer.Duration;
                ignoreEnemiesduration = 0.0f;
                ref float ignoreEnemiestimestamp = ref ignoreEnemiesTimer.Timestamp;
                ignoreEnemiestimestamp = 0.0f;
                ref float ignoreEnemiestimescale = ref ignoreEnemiesTimer.Timescale;
                ignoreEnemiestimescale = 1.0f;

                CountdownTimer alertTimer = bot.AlertTimer;
                ref float alerttimestamp = ref alertTimer.Timestamp;
                if (alerttimestamp < now)
                {
                    ref float alertduration = ref alertTimer.Duration;
                    alertduration = 8.0f;
                    alerttimestamp = now + 8.0f;
                    ref float alerttimescale = ref alertTimer.Timescale;
                    alerttimescale = 1.0f;
                }

                // P3c: perceive → publish intel → decide (alert + sustain look).
                TickBeliefPerception(player, pawn, bot, now);
                TickBeliefDecisions(player, pawn, bot, idx, now);
                // P3b: geometric clear only when intel/native are idle.
                UpdateAngleClearing(player, pawn, bot, idx, now);
            }
            else
            {
                ref bool isRapidFiring = ref bot.IsRapidFiring;
                isRapidFiring = true;

                ref float peripheralTimestamp = ref bot.PeripheralTimestamp;
                peripheralTimestamp = 0.0f;

                ref float fireWeaponTimestamp = ref bot.FireWeaponTimestamp;
                fireWeaponTimestamp = 0.0f;
                // Alert
                CountdownTimer alertTimer = bot.AlertTimer;
                ref float alertduration = ref alertTimer.Duration;
                alertduration = 600.0f;

                ref float alerttimestamp = ref alertTimer.Timestamp;
                alerttimestamp = now + alertduration;

                ref float alerttimescale = ref alertTimer.Timescale;
                alerttimescale = 1.0f;
                // Never ignore enemies
                CountdownTimer ignoreEnemiesTimer = bot.IgnoreEnemiesTimer;

                ref float ignoreEnemiesduration = ref ignoreEnemiesTimer.Duration;
                ignoreEnemiesduration = 0.0f;

                ref float ignoreEnemiestimestamp = ref ignoreEnemiesTimer.Timestamp;
                ignoreEnemiestimestamp = 0.0f;

                ref float ignoreEnemiestimescale = ref ignoreEnemiesTimer.Timescale;
                ignoreEnemiestimescale = 1.0f;

                // Never lookat (panic)
                CountdownTimer panicTimer = bot.PanicTimer;

                ref float panicduration = ref panicTimer.Duration;
                panicduration = 0.0f;

                ref float panictimestamp = ref panicTimer.Timestamp;
                panictimestamp = 0.0f;

                ref float panictimescale = ref panicTimer.Timescale;
                panictimescale = 1.0f;
                // Never be surprised
                CountdownTimer surpriseTimer = bot.SurpriseTimer;

                ref float surpriseDuration = ref surpriseTimer.Duration;
                surpriseDuration = 0.0f;

                ref float surpriseTimestamp = ref surpriseTimer.Timestamp;
                surpriseTimestamp = 0.0f;

                ref float surpriseTimescale = ref surpriseTimer.Timescale;
                surpriseTimescale = 1.0f;
            }
            // Always dodge
            ref bool isEnemySniperVisible = ref bot.IsEnemySniperVisible;
            isEnemySniperVisible = true;

            CountdownTimer sawEnemySniperTimer = bot.SawEnemySniperTimer;

            ref float sawEnemySniperduration = ref sawEnemySniperTimer.Duration;
            sawEnemySniperduration = 600.0f;

            ref float sawEnemySniperTimestamp = ref sawEnemySniperTimer.Timestamp;
            sawEnemySniperTimestamp = now + sawEnemySniperduration;

            ref float sawEnemySniperTimescale = ref sawEnemySniperTimer.Timescale;
            sawEnemySniperTimescale = 1.0f;
            // Teammate Stuck Fix
            ref bool IsWaitingBehindFriend = ref bot.IsWaitingBehindFriend;
            IsWaitingBehindFriend = false;

            CountdownTimer politeTimer = bot.PoliteTimer;

            ref float politeTimerDuration = ref politeTimer.Duration;
            politeTimerDuration = 0.0f;

            ref float politeTimerTimestamp = ref politeTimer.Timestamp;
            politeTimerTimestamp = 0.0f;

            ref float politeTimerTimescale = ref politeTimer.Timescale;
            politeTimerTimescale = 1.0f;

            // Sniper Peek
            bool curIsAttacking = bot.IsAttacking;

            if (curIsAttacking && _hasFiredThisAttack.Remove(idx))
            {
                string? wpn = pawn.WeaponServices?.ActiveWeapon?.Value?.DesignerName;
                if (wpn == "weapon_awp" || wpn == "weapon_ssg08")
                {
                    _lastLateralDir.TryGetValue(idx, out float lastDir);
                    if (lastDir != 0f)
                    {
                        float yawS = pawn.EyeAngles.Y * MathF.PI / 180f;
                        float rx = -MathF.Sin(yawS), ry = MathF.Cos(yawS);
                        float injX = rx * (-lastDir) * 250f;
                        float injY = ry * (-lastDir) * 250f;
                        pawn.AbsVelocity.X += injX;
                        pawn.AbsVelocity.Y += injY;

                        ResetLookAroundForBot(player, resetHidingSpots: true);
                    }
                }
            }
            // Avoid Confusion
            if (curIsAttacking)
            {
                ref bool eyeAnglesUnderPathFinderControl = ref bot.EyeAnglesUnderPathFinderControl;
                eyeAnglesUnderPathFinderControl = false;

                ref float inhibitLookAroundTimestamp = ref bot.InhibitLookAroundTimestamp;
                inhibitLookAroundTimestamp = 0f;
            }
            //Test Alert! Can cause crash when bot_debug 1 !
            ref bool isAimingAtEnemy = ref bot.IsAimingAtEnemy;
            if (isAimingAtEnemy && !curIsAttacking)
            {
                bot.IsAttacking = true;
            }
            // Cancel Crouch After Attack
            if (_prevIsAttacking.TryGetValue(idx, out bool prevAttack))
            {
                if (prevAttack == true && curIsAttacking == false)
                {
                    ref bool isCrouching = ref bot.IsCrouching;
                    isCrouching = false;
                }
            }
            _prevIsAttacking[idx] = curIsAttacking;

            if (!curIsAttacking)
            {
                _hasFiredThisAttack.Remove(idx);
                float yawL2 = pawn.EyeAngles.Y * MathF.PI / 180f;
                float latX = -MathF.Sin(yawL2), latY = MathF.Cos(yawL2);
                float latSpd = pawn.AbsVelocity.X * latX + pawn.AbsVelocity.Y * latY;
                if (MathF.Abs(latSpd) > 10f)
                {
                    float newDir = latSpd > 0f ? 1f : -1f;
                    float prevDir = _lastLateralDir.GetValueOrDefault(idx);
                    _lastLateralDir[idx] = newDir;
                }
            }

            // Flash flick: feet planted — skip jump/crouch/stuck movement overrides.
            if (_flashPlantedUntil.TryGetValue(idx, out float flashPlantUntil) && now < flashPlantUntil)
            {
                _prevIsAttacking[idx] = curIsAttacking;
                _prevInAir.TryGetValue(idx, out bool prevAirFlash);
                bool nearLadderFlash = pawn.MoveType == MoveType_t.MOVETYPE_LADDER;
                bool inAirFlash = !nearLadderFlash
                    && (pawn.GroundEntity == null || !pawn.GroundEntity.IsValid);
                _prevInAir[idx] = inAirFlash;
                continue;
            }

            // Ladder Stuck Issue Fix
            var moveServices = pawn.MovementServices as CCSPlayer_MovementServices;
            var ladderNormal = moveServices?.LadderNormal;

            bool nearLadder = pawn.MoveType == MoveType_t.MOVETYPE_LADDER
                        || (ladderNormal != null
                            && (ladderNormal.X != 0f || ladderNormal.Y != 0f || ladderNormal.Z != 0f));

            if (nearLadder) _ladderExitTime[idx] = Server.CurrentTime;

            bool inLadderCooldown = nearLadder
                || (_ladderExitTime.TryGetValue(idx, out float exitTime)
                    && Server.CurrentTime - exitTime < 5.0f);

            bool inAir = !inLadderCooldown
                    && (pawn.GroundEntity == null || !pawn.GroundEntity.IsValid);

            _prevInAir.TryGetValue(idx, out bool prevInAir);
            // Door Stuck Issue Fix
            if (inDoorCooldown)
            {
                _prevInAir[idx] = inAir;
                continue;
            }
            // Jump Crouch Forward/Backward
            var angles = pawn.EyeAngles;
            float yawDir = angles.Y * MathF.PI / 180f;
            float fwdX = MathF.Cos(yawDir);
            float fwdY = MathF.Sin(yawDir);
            float currentFwd = pawn.AbsVelocity.X * fwdX + pawn.AbsVelocity.Y * fwdY;

            if (currentFwd >= 20f || currentFwd <= -20f)
            {
                _lastForwardDir[idx] = currentFwd > 0f ? 1f : -1f;
            }

            if (inAir)
            {
                if (!pawn.IsDefusing)
                {
                    ref bool isCrouching = ref bot.IsCrouching;
                    isCrouching = true;
                }
                if (!curIsAttacking)// Avoid Jump and Gun
                {
                    float targetSpeed;
                    if (currentFwd <= -20f)
                    {
                        targetSpeed = -215f;
                    }
                    else if (currentFwd >= 20f)
                    {
                        targetSpeed = 215f;
                    }
                    else
                    {
                        float lastDir = _lastForwardDir.TryGetValue(idx, out float dir) ? dir : 1f;
                        targetSpeed = lastDir > 0f ? 215f : -215f;
                    }
                    const float accel = 12f;
                    const float tickInterval = 0.015625f;
                    float delta = targetSpeed - currentFwd;
                    if (targetSpeed > 0)
                    {
                        if (delta > 0)
                        {
                            float addSpeed = delta * accel * tickInterval;

                            pawn.AbsVelocity.X += fwdX * addSpeed;
                            pawn.AbsVelocity.Y += fwdY * addSpeed;
                        }
                    }
                    else
                    {
                        if (delta < 0)
                        {
                            float addSpeed = delta * accel * tickInterval;

                            pawn.AbsVelocity.X += fwdX * addSpeed;
                            pawn.AbsVelocity.Y += fwdY * addSpeed;
                        }
                    }
                }
            }
            // Cancel Crouch
            if (prevInAir && !inAir)
            {
                ref bool isCrouching = ref bot.IsCrouching;
                isCrouching = false;
            }
            _prevInAir[idx] = inAir;
            // cache the parameters for counter-strafe
            _cachedInAir[idx] = inAir;
            _cachedNearLadder[idx] = nearLadder;
            // Normal Un-Stuck Process
            ref bool isStuck = ref bot.IsStuck;
            if (isStuck)
            {
                ref bool isRunning = ref bot.IsRunning;
                isRunning = true;

                ref float jumpTimestamp = ref bot.JumpTimestamp;
                jumpTimestamp = 0.0f;

                CountdownTimer stuckJumpTimer = bot.StuckJumpTimer;

                ref float stuckduration = ref stuckJumpTimer.Duration;
                stuckduration = 0.0f;

                ref float stucktimestamp = ref stuckJumpTimer.Timestamp;
                stucktimestamp = Server.CurrentTime;

                ref float stucktimescale = ref stuckJumpTimer.Timescale;
                stucktimescale = 1.0f;

                // Manual Stuck State
                float speed2D = MathF.Sqrt(
                    pawn.AbsVelocity.X * pawn.AbsVelocity.X +
                    pawn.AbsVelocity.Y * pawn.AbsVelocity.Y);

                var curPos = pawn.AbsOrigin!;

                if (!_stuckStartTime.ContainsKey(idx))
                {
                    _stuckStartTime[idx] = now;
                    _stuckStartPos[idx] = new Vector(curPos.X, curPos.Y, curPos.Z);
                    _stuckJumpDone[idx] = false;
                    _stuckMaxSpeed[idx] = 0f;
                }

                if (speed2D > _stuckMaxSpeed.GetValueOrDefault(idx))
                    _stuckMaxSpeed[idx] = speed2D;

                float elapsed = now - _stuckStartTime.GetValueOrDefault(idx);
                var sp = _stuckStartPos.GetValueOrDefault(idx, new Vector(curPos.X, curPos.Y, curPos.Z));
                float dist2D = MathF.Sqrt(
                    MathF.Pow(curPos.X - sp.X, 2) +
                    MathF.Pow(curPos.Y - sp.Y, 2));
                float maxSpd = _stuckMaxSpeed.GetValueOrDefault(idx);

                bool condA = elapsed >= 1.0f && maxSpd <= 10f;
                bool condB = elapsed >= 3.0f && maxSpd > 10f && dist2D < 75f;

                if ((condA || condB) && !_stuckJumpDone.GetValueOrDefault(idx))
                {
                    ref bool isCrouching = ref bot.IsCrouching;
                    isCrouching = false;

                    _stuckJumpDone[idx] = true;

                    int jumpCount = _stuckJumpCount.GetValueOrDefault(idx);
                    _stuckJumpCount[idx] = jumpCount + 1;

                    float sideSign = (jumpCount % 2 == 0) ? 1f : -1f;
                    float offsetRad = 30f * MathF.PI / 180f * sideSign;
                    float baseYaw = pawn.EyeAngles.Y * MathF.PI / 180f;
                    float backYaw = baseYaw + MathF.PI + offsetRad;

                    pawn.AbsVelocity.X = MathF.Cos(backYaw) * 100f;
                    pawn.AbsVelocity.Y = MathF.Sin(backYaw) * 100f;

                    CountdownTimer repathTimer = bot.RepathTimer;

                    ref float repathduration = ref repathTimer.Duration;
                    repathduration = 0.0f;

                    ref float repathtimestamp = ref repathTimer.Timestamp;
                    repathtimestamp = Server.CurrentTime;

                    ref float repathtimescale = ref repathTimer.Timescale;
                    repathtimescale = 1.0f;

                    // Reset
                    _stuckStartTime[idx] = now;
                    _stuckStartPos[idx] = new Vector(curPos.X, curPos.Y, curPos.Z);
                    _stuckMaxSpeed[idx] = 0f;
                }
            }
            else
            {
                // Clear
                _stuckStartTime.Remove(idx);
                _stuckStartPos.Remove(idx);
                _stuckJumpDone.Remove(idx);
                _stuckMaxSpeed.Remove(idx);

                // Idle repath: if speed < 5 for 5s, force a repath
                float speed2DIdle = MathF.Sqrt(
                    pawn.AbsVelocity.X * pawn.AbsVelocity.X +
                    pawn.AbsVelocity.Y * pawn.AbsVelocity.Y);

                if (speed2DIdle < 5f)
                {
                    if (!_idleStartTime.ContainsKey(idx))
                        _idleStartTime[idx] = now;

                    float idleElapsed = now - _idleStartTime[idx];
                    float lastRepath = _lastRepathTime.GetValueOrDefault(idx, -999f);

                    if (idleElapsed >= 5f && now - lastRepath >= 5f && !curIsAttacking && !pawn.IsDefusing)
                    {
                        ref bool isCrouching = ref bot.IsCrouching;
                        isCrouching = false;

                        _lastRepathTime[idx] = now;

                        CountdownTimer repathTimer = bot.RepathTimer;

                        ref float repathduration = ref repathTimer.Duration;
                        repathduration = 0.0f;

                        ref float repathtimestamp = ref repathTimer.Timestamp;
                        repathtimestamp = Server.CurrentTime;

                        ref float repathtimescale = ref repathTimer.Timescale;
                        repathtimescale = 1.0f;

                        ResetLookAroundForBot(player, resetHidingSpots: true);
                    }
                }
                else
                {
                    _idleStartTime.Remove(idx);
                }
            }

            //Inferno Sewer Stuck Fix
            if (pawn.AbsOrigin != null)
            {
                Vector pos = pawn.AbsOrigin;
                bool isInferno = string.Equals(Server.MapName, "de_inferno", StringComparison.OrdinalIgnoreCase);
                float dx = pos.X - 285f;
                float dy = pos.Y - 450f;
                float dist = MathF.Sqrt(dx * dx + dy * dy);

                if (isInferno && dist < 50f)
                {
                    CountdownTimer repathTimer = bot.RepathTimer;

                    ref float repathduration = ref repathTimer.Duration;
                    repathduration = 0.0f;

                    ref float repathtimestamp = ref repathTimer.Timestamp;
                    repathtimestamp = Server.CurrentTime;

                    ref float repathtimescale = ref repathTimer.Timescale;
                    repathtimescale = 1.0f;
                }
            }
        }
    }
    //---------------------------------------------------------------------------------------
    // Clears per-round state and releases elimination knife locks
    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        ReleaseKnifeLocks();
        StopDefuseReveal();
        ClearReveals();
        _eliminationHandled = false;
        _isFreezeTime = true;

        // Per-round transient state keyed by player index. Indices are reused by
        // later connections, so stale entries would leak last round's movement /
        // stuck / door state onto a different bot.
        _prevInAir.Clear();
        _lastForwardDir.Clear();
        _ladderExitTime.Clear();
        _lastLateralDir.Clear();
        _doorEventCooldown.Clear();
        _stuckStartTime.Clear();
        _stuckStartPos.Clear();
        _stuckJumpDone.Clear();
        _stuckJumpCount.Clear();
        _stuckMaxSpeed.Clear();
        _idleStartTime.Clear();
        _lastRepathTime.Clear();
        _reloadInterruptCooldown.Clear();
        _lastMoveDir.Clear();
        _fakeDefuseCooldown.Clear();
        _fakeDefuseGuardUntil.Clear();
        _fakeDefuseCounts.Clear();
        CancelAllFakeDefuseSuppressions();
        _fakeDefuseSearchingBots.Clear();
        _pendingDefuseRestore.Clear();
        RestoreAllFovPatches();
        _hasFiredThisAttack.Clear();
        _prevIsAttacking.Clear();
        _cachedInAir.Clear();
        _cachedNearLadder.Clear();

        // Flash projectiles never survive a round transition; drop their tracking
        // so entity indices reused next round don't match stale decisions.
        _flashThrownAt.Clear();
        _flashDecisions.Clear();
        _flashLookReleased.Clear();
        _flashPlantedUntil.Clear();
        ClearAngleClearingState();
        ClearBeliefState();
        ClearDecisionState();
        return HookResult.Continue;
    }

    // Detects elimination while explicitly excluding the current death victim
    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var victim = @event.Userid;
        if (victim != null && victim.IsValid)
            RecordTeammateDeathBelief(victim);

        // Deathmatch is free-for-all even though the engine still reports
        // temporary T/CT team numbers. Do not treat the last bot on one side
        // as a round elimination and lock the other bots to their knives.
        if (IsDeathmatch())
            return HookResult.Continue;

        if (_botController == null)
            return HookResult.Continue;

        if (victim == null || !victim.IsValid)
            return HookResult.Continue;

        CsTeam victimTeam = (CsTeam)(int)victim.TeamNum;
        if (victimTeam != CsTeam.Terrorist &&
            victimTeam != CsTeam.CounterTerrorist)
            return HookResult.Continue;

        bool alreadyHandled = _eliminationHandled;
        HandleTeamElimination(victim.Slot, victimTeam);
        RestoreDefuseAfterElimination(victim.Slot, victimTeam);

        // 10% chance the killer Bot inspects its current weapon. Skip when this
        // exact kill just triggered the elimination switching, since that path
        // already inspects the knife.
        if (alreadyHandled || !_eliminationHandled)
            MaybeInspectOnKill(@event.Attacker);

        return HookResult.Continue;
    }

    private static bool IsDeathmatch()
    {
        var gameType = ConVar.Find("game_type");
        var gameMode = ConVar.Find("game_mode");
        return gameType?.GetPrimitiveValue<int>() == 1
            && gameMode?.GetPrimitiveValue<int>() == 2;
    }

    // Rolls a 10% inspect for the Bot credited with a kill
    private void MaybeInspectOnKill(CCSPlayerController? attacker)
    {
        if (_botController == null ||
            attacker == null || !attacker.IsValid || !attacker.IsBot ||
            !attacker.PawnIsAlive || attacker.HasBeenControlledByPlayerThisRound)
            return;

        if (_random.NextDouble() >= 0.10)
            return;

        QueueInspectInjection(attacker.Slot);
    }

    // Locks every surviving Bot on the winning team to its knife slot
    private void HandleTeamElimination(int victimSlot, CsTeam victimTeam)
    {
        if (_eliminationHandled || _botController == null) return;

        var activePlayers = Utilities.GetPlayers()
            .Where(player => player.IsValid
                && !player.IsHLTV
                && ((int)player.TeamNum == (int)CsTeam.Terrorist
                    || (int)player.TeamNum == (int)CsTeam.CounterTerrorist))
            .ToList();

        bool victimTeamHasSurvivor = activePlayers.Any(player =>
            player.Slot != victimSlot
            && (int)player.TeamNum == (int)victimTeam
            && player.PawnIsAlive);
        if (victimTeamHasSurvivor) return;

        CsTeam winningTeam = victimTeam == CsTeam.Terrorist
            ? CsTeam.CounterTerrorist
            : CsTeam.Terrorist;
        var winningBots = activePlayers.Where(player =>
                player.IsBot
                && player.PawnIsAlive
                && !player.HasBeenControlledByPlayerThisRound
                && (int)player.TeamNum == (int)winningTeam)
            .ToList();

        bool winningTeamAlive = activePlayers.Any(player =>
            (int)player.TeamNum == (int)winningTeam && player.PawnIsAlive);
        if (!winningTeamAlive) return;

        _eliminationHandled = true;

        foreach (var bot in winningBots)
        {
            bool switched = BotControllerBridge.SwitchBotWeapon(
                _botController,
                bot.Slot, KnifeDefinitionIndex);
            bool locked = BotControllerBridge.LockKnife(
                _botController, bot.Slot);
            if (switched)
                QueueInspectInjection(bot.Slot);
            if (locked)
                _knifeLockedBotSlots.Add(bot.Slot);

            if (!switched || !locked)
            {
                Console.WriteLine(
                    $"[Smarter-Bot] Knife action failed for slot {bot.Slot}: switch={switched}, lock={locked}");
            }
        }

    }

    // Queues a one-command inspect injection after the knife becomes active
    private void QueueInspectInjection(int slot)
    {
        Server.NextFrame(() =>
        {
            if (_botController == null) return;

            var player = Utilities.GetPlayerFromSlot(slot);
            if (player == null || !player.IsValid || !player.IsBot ||
                !player.PawnIsAlive || player.HasBeenControlledByPlayerThisRound)
                return;

            if (BotControllerBridge.InjectUsercmd(
                    _botController, slot, InspectButtonMask) <= 0)
            {
                Console.WriteLine(
                    $"[Smarter-Bot] Inspect injection failed for slot {slot}");
            }
        });
    }

    // Releases only Slot3 locks successfully applied by this plugin
    private void ReleaseKnifeLocks()
    {
        if (_botController != null)
        {
            foreach (int slot in _knifeLockedBotSlots)
            {
                if (BotControllerBridge.IsKnifeLocked(_botController, slot))
                    BotControllerBridge.UnlockWeapon(_botController, slot);
            }
        }

        _knifeLockedBotSlots.Clear();
    }

    // Isolates optional BotControllerApi types from the main plugin type
    private static class BotControllerBridge
    {
        // Resolves the optional BotController capability at runtime
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static object? TryGet()
        {
            var capability =
                new PluginCapability<BotControllerApi.IBotControllerApi>(
                    "botcontroller:api");
            return capability.Get();
        }

        // Switches one Bot to its knife definition
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool SwitchBotWeapon(object api, int slot, int defIndex)
        {
            return ((BotControllerApi.IBotControllerApi)api)
                .SwitchBotWeapon(slot, defIndex);
        }

        // Creates an independently cancellable usercmd injection on a Bot
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static long InjectUsercmd(
            object api, int slot, ulong buttonMask, int durationMs = 0)
        {
            return ((BotControllerApi.IBotControllerApi)api)
                .InjectUsercmd(slot, buttonMask, durationMs);
        }

        // Starts a cancellable persistent usercmd button suppression
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static long StartUsercmdSuppression(
            object api, int slot, ulong buttonMask)
        {
            var method = api.GetType().GetMethod("StartUsercmdSuppression");
            if (method == null) return 0L;
            return (long)(method.Invoke(api, [slot, buttonMask]) ?? 0L);
        }

        // Cancels one persistent usercmd suppression by its token
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool CancelUsercmdSuppression(
            object api, int slot, long suppressionId)
        {
            var method = api.GetType().GetMethod("CancelUsercmdSuppression");
            if (method == null) return false;
            return (bool)(method.Invoke(api, [slot, suppressionId]) ?? false);
        }

        // Applies the knife-slot weapon lock to one Bot
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool LockKnife(object api, int slot)
        {
            return ((BotControllerApi.IBotControllerApi)api)
                .Lock(slot, BotControllerApi.LockTarget.Slot3);
        }

        // Checks whether one Bot still has the knife-slot lock
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool IsKnifeLocked(object api, int slot)
        {
            return ((BotControllerApi.IBotControllerApi)api)
                .GetWeaponLock(slot) == BotControllerApi.LockTarget.Slot3;
        }

        // Releases the weapon lock from one Bot
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool UnlockWeapon(object api, int slot)
        {
            return ((BotControllerApi.IBotControllerApi)api)
                .Unlock(slot, BotControllerApi.LockKind.Weapon);
        }

        // True while NadeSystem (or any controller) is replaying motion on this slot.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool IsReplaying(object api, int slot)
        {
            return ((BotControllerApi.IBotControllerApi)api).IsReplaying(slot);
        }
    }

    private HookResult OnDoorOpen(EventDoorOpen @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid || !player.IsBot) return HookResult.Continue;

        int idx = (int)player.Index;
        _doorEventCooldown[idx] = Server.CurrentTime + 1.0f;
        return HookResult.Continue;
    }

    private HookResult OnDoorClose(EventDoorClose @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid || !player.IsBot) return HookResult.Continue;

        int idx = (int)player.Index;
        _doorEventCooldown[idx] = Server.CurrentTime + 1.0f;
        return HookResult.Continue;
    }

    private HookResult OnWeaponFire(EventWeaponFire @event, GameEventInfo info)
    {
        var shooter = @event.Userid;
        if (shooter == null || !shooter.IsValid) return HookResult.Continue;

        // Gunshots: do NOT invent a hear radius here. Valve emits player_sound with
        // the real audible Radius; OnPlayerSoundBelief handles that (same as players).

        if (!shooter.IsBot) return HookResult.Continue;

        int idx = (int)shooter.Index;
        var pawn = shooter.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid) return HookResult.Continue;

        var bot = pawn.Bot;
        if (bot == null) return HookResult.Continue;
        // Sniper Peek
        _hasFiredThisAttack.Add(idx);

        // Counter-strafe on fire
        bool cachedInAir = _cachedInAir.GetValueOrDefault(idx, false);
        bool cachedNearLadder = _cachedNearLadder.GetValueOrDefault(idx, false);
        if (!cachedInAir && !cachedNearLadder)
        {
            string? wpnFire = pawn.WeaponServices?.ActiveWeapon?.Value?.DesignerName;
            if (wpnFire != null)
            {
                float vx = pawn.AbsVelocity.X;
                float vy = pawn.AbsVelocity.Y;
                float speed2D = MathF.Sqrt(vx * vx + vy * vy);

                if (wpnFire is "weapon_glock" or "weapon_hkp2000" or "weapon_p250"
                            or "weapon_fiveseven" or "weapon_cz75a" or "weapon_tec9"
                            or "weapon_mac10" or "weapon_mp9")
                {
                    if (speed2D > 70f)
                    {
                        float scale = 70f / speed2D;
                        pawn.AbsVelocity.X = vx * scale;
                        pawn.AbsVelocity.Y = vy * scale;
                    }
                }
                else if (wpnFire is "weapon_usp_silencer" or "weapon_deagle"
                                or "weapon_ssg08" or "weapon_awp"
                                or "weapon_scar20" or "weapon_g3sg1"
                                or "weapon_galilar" or "weapon_ak47" or "weapon_sg556"
                                or "weapon_famas" or "weapon_m4a1" or "weapon_m4a1_silencer"
                                or "weapon_aug" or "weapon_m249" or "weapon_negev")
                {
                    if (speed2D > 0f)
                    {
                        pawn.AbsVelocity.X = 0f;
                        pawn.AbsVelocity.Y = 0f;
                    }
                }
                // Other weapons: no speed change
            }
        }

        if (pawn.IsDefusing || !bot.IsAttacking) return HookResult.Continue;
        // Random combat crouch
        double crouchChance = 0.0;
        string? wpn = pawn.WeaponServices?.ActiveWeapon?.Value?.DesignerName;
        if (wpn != null)
        {
            if (wpn is "weapon_glock" or "weapon_hkp2000" or "weapon_p250" or "weapon_fiveseven")
                crouchChance = 0.20;

            else if (wpn is "weapon_usp_silencer" or "weapon_deagle")
                crouchChance = 0.30;

            else if (wpn is "weapon_elite" or "weapon_tec9" or "weapon_cz75a" or "weapon_revolver"
                    or "weapon_scar20" or "weapon_g3sg1")
                crouchChance = 0.10;

            else if (wpn is "weapon_mac10" or "weapon_mp9" or "weapon_bizon")
                crouchChance = 0.03;

            else if (wpn is "weapon_mp5sd" or "weapon_ump45" or "weapon_p90"
                    or "weapon_nova" or "weapon_xm1014" or "weapon_sawedoff" or "weapon_mag7"
                    or "weapon_ssg08" or "weapon_awp")
                crouchChance = 0.05;

            else if (wpn is "weapon_galilar" or "weapon_ak47" or "weapon_sg556"
                    or "weapon_famas" or "weapon_m4a1" or "weapon_m4a1_silencer" or "weapon_aug"
                    or "weapon_m249")
                crouchChance = 0.50;

            else if (wpn == "weapon_negev")
                crouchChance = 0.90;
        }

        ref bool isCrouching = ref bot.IsCrouching;
        isCrouching = _random.NextDouble() < crouchChance;

        CountdownTimer sneakTimer = bot.SneakTimer;

        ref float sneakduration = ref sneakTimer.Duration;
        sneakduration = 0.0f;

        ref float sneaktimestamp = ref sneakTimer.Timestamp;
        sneaktimestamp = 0.0f;

        ref float sneaktimescale = ref sneakTimer.Timescale;
        sneaktimescale = 1.0f;

        return HookResult.Continue;
    }

    private HookResult OnBombPlanted(EventBombPlanted @event, GameEventInfo info)
    {
        foreach (var player in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
        {
            if (!player.IsValid || !player.IsBot)
                continue;

            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid)
                continue;

            var bot = pawn.Bot;
            if (bot == null)
                continue;

            bool isTakenOver = player.HasBeenControlledByPlayerThisRound;
            if (isTakenOver) continue;

            CountdownTimer hurryTimer = bot.HurryTimer;

            ref float duration = ref hurryTimer.Duration;
            duration = 40.0f;

            ref float timestamp = ref hurryTimer.Timestamp;
            timestamp = Server.CurrentTime + duration;

            ref float timescale = ref hurryTimer.Timescale;
            timescale = 1.0f;

            ref bool isRunning = ref bot.IsRunning;
            isRunning = true;
        }
        return HookResult.Continue;
    }

    private HookResult OnBombBeginDefuse(EventBombBegindefuse @event, GameEventInfo info)
    {
        ResetLookAroundForBot(@event.Userid, resetHidingSpots: true);

        var player = @event.Userid;
        // The bomb-defuser is revealed
        _defuserSlot = player != null && player.IsValid ? player.Slot : -1;
        _isBombBeingDefused = true;
        StartDefuseRevealCycle();

        if (player == null || !player.IsValid || !player.IsBot) return HookResult.Continue;

        var pawn = player.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid) return HookResult.Continue;

        var bot = pawn.Bot;
        if (bot == null) return HookResult.Continue;

        bool isTakenOver = player.HasBeenControlledByPlayerThisRound;
        if (isTakenOver) return HookResult.Continue;

        bool hasLivingEnemies = Utilities
            .FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
            .Any(p => p.IsValid && p.PawnIsAlive
                && ((int)p.TeamNum == 2 || (int)p.TeamNum == 3)
                && (int)p.TeamNum != (int)player.TeamNum);
        // Fake Defuse
        if (hasLivingEnemies)
        {
            // If we have a defuser, tend to defuse directly
            var itemSvc = pawn.ItemServices?.Handle != nint.Zero
                ? new CCSPlayer_ItemServices(pawn.ItemServices!.Handle)
                : null;
            bool hasDefuser = itemSvc?.HasDefuser ?? false;
            double baseFakeChance = hasDefuser ? 0.20 : 0.66;
            int fakeDefuseCount = _fakeDefuseCounts.GetValueOrDefault(bot.Handle);
            double fakeChance = baseFakeChance * Math.Pow(0.66, fakeDefuseCount);
            int slot = player.Slot;
            bool fakeDefuseCoolingDown = _fakeDefuseCooldown.TryGetValue(
                slot, out float cooldownEnd) && Server.CurrentTime < cooldownEnd;

            if (!fakeDefuseCoolingDown && _random.NextDouble() < fakeChance)
            {
                ScheduleFakeDefuse(player);
            }
        }

        return HookResult.Continue;
    }

    // Keeps the real defuse sound briefly before entering the guard search window
    private void ScheduleFakeDefuse(CCSPlayerController player)
    {
        if (_botController == null || _defuseBombFunction == null) return;

        float holdSeconds = FakeDefuseHoldMinSeconds +
            (float)_random.NextDouble() *
            (FakeDefuseHoldMaxSeconds - FakeDefuseHoldMinSeconds);
        float searchSeconds = FakeDefuseSearchMinSeconds +
            (float)_random.NextDouble() *
            (FakeDefuseSearchMaxSeconds - FakeDefuseSearchMinSeconds);

        float now = Server.CurrentTime;
        int slot = player.Slot;
        _fakeDefuseCooldown[slot] = now + holdSeconds + searchSeconds;

        AddTimer(
            holdSeconds,
            () => FinishFakeDefuse(slot, searchSeconds),
            CounterStrikeSharp.API.Modules.Timers.TimerFlags.STOP_ON_MAPCHANGE);
    }

    // Releases Use and leaves the Bot near the bomb to acquire threats normally
    private void FinishFakeDefuse(int slot, float searchSeconds)
    {
        if (_botController == null) return;

        var player = Utilities.GetPlayerFromSlot(slot);
        if (player == null || !player.IsValid || !player.IsBot ||
            !player.PawnIsAlive || player.HasBeenControlledByPlayerThisRound)
            return;

        var pawn = player.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid || !pawn.IsDefusing) return;

        var bot = pawn.Bot;
        if (bot == null) return;

        long suppressionId = BotControllerBridge.StartUsercmdSuppression(
            _botController, slot, UseButtonMask);
        if (suppressionId <= 0) return;

        _fakeDefuseSuppressionIds[slot] = suppressionId;

        _fakeDefuseGuardUntil[bot.Handle] = Server.CurrentTime + searchSeconds;
        _fakeDefuseCounts[bot.Handle] =
            _fakeDefuseCounts.GetValueOrDefault(bot.Handle) + 1;
        _pendingDefuseRestore.Add(slot);

        ref float stateTimestamp = ref bot.StateTimestamp;
        stateTimestamp = Server.CurrentTime - 2.0f;

        ResetLookAroundForBot(player, resetHidingSpots: true);

        // Enable 360 FOV for this bot during search phase
        _fakeDefuseSearchingBots.Add(slot);
        ApplyFovPatches();

        // Schedule FOV restoration after search completes
        AddTimer(
            searchSeconds,
            () => EndFakeDefuseSearch(slot),
            CounterStrikeSharp.API.Modules.Timers.TimerFlags.STOP_ON_MAPCHANGE);
    }

    // Removes bot from search phase and restores FOV if no other bots are searching
    private void EndFakeDefuseSearch(int slot)
    {
        CancelFakeDefuseSuppression(slot);
        _fakeDefuseSearchingBots.Remove(slot);
        if (_fakeDefuseSearchingBots.Count == 0)
        {
            RestoreAllFovPatches();
        }
    }

    // Releases one Bot's fake-defuse +use suppression, if it still holds one
    private void CancelFakeDefuseSuppression(int slot)
    {
        if (!_fakeDefuseSuppressionIds.Remove(slot, out long suppressionId))
            return;
        if (_botController == null) return;

        BotControllerBridge.CancelUsercmdSuppression(
            _botController, slot, suppressionId);
    }

    // Releases every outstanding fake-defuse +use suppression.
    private void CancelAllFakeDefuseSuppressions()
    {
        foreach (int slot in _fakeDefuseSuppressionIds.Keys.ToList())
            CancelFakeDefuseSuppression(slot);
    }

    // Grants each Bot that faked a defuse this round one DefuseBomb re-entry at
    // the moment its last enemy dies
    private void RestoreDefuseAfterElimination(int victimSlot, CsTeam victimTeam)
    {
        if (_pendingDefuseRestore.Count == 0 || _defuseBombFunction == null)
            return;

        var players = Utilities
            .FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
            .ToList();

        // The death victim can still report alive here, so exclude it explicitly
        bool victimTeamHasSurvivor = players.Any(p =>
            p.IsValid && p.Slot != victimSlot && p.PawnIsAlive
            && (int)p.TeamNum == (int)victimTeam);
        if (victimTeamHasSurvivor) return;

        foreach (int slot in _pendingDefuseRestore.ToList())
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player == null || !player.IsValid || !player.IsBot ||
                !player.PawnIsAlive || player.HasBeenControlledByPlayerThisRound)
                continue;

            // Only Bots opposing the wiped team just lost their last enemy
            if ((int)player.TeamNum == (int)victimTeam) continue;

            _pendingDefuseRestore.Remove(slot);
            ForceDefuseBombState(slot);
        }
    }

    // Pushes the native state machine straight back into DefuseBomb
    private void ForceDefuseBombState(int slot)
    {
        Server.NextFrame(() =>
        {
            if (_defuseBombFunction == null) return;

            var player = Utilities.GetPlayerFromSlot(slot);
            if (player == null || !player.IsValid || !player.IsBot ||
                !player.PawnIsAlive || player.HasBeenControlledByPlayerThisRound)
                return;

            var pawn = player.PlayerPawn?.Value;
            if (pawn == null || !pawn.IsValid) return;

            var bot = pawn.Bot;
            if (bot == null) return;

            // Re-entering the state is useless while Use is still stripped
            CancelFakeDefuseSuppression(slot);

            // Our own guard would otherwise stop this state entry
            _fakeDefuseGuardUntil.Remove(bot.Handle);
            _defuseBombFunction.Invoke(bot.Handle);
        });
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

    // Installs the Smarter-Bot-owned guard for native DefuseBomb state entry
    private void InstallDefuseBombHook()
    {
        try
        {
            string signature = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? DefuseBombWindowsSignature
                : DefuseBombLinuxSignature;
            _defuseBombFunction = new MemoryFunctionVoid<nint>(signature);
            _defuseBombFunction.Hook(OnDefuseBombPre, HookMode.Pre);
        }
        catch (Exception ex)
        {
            _defuseBombFunction = null;
            Logger.LogError(ex,
                "[Smarter-Bot] CCSBot::DefuseBomb hook unavailable; fake defuse disabled");
        }
    }

    // Removes the Smarter-Bot-owned DefuseBomb state-entry guard
    private void UninstallDefuseBombHook()
    {
        if (_defuseBombFunction == null) return;

        try
        {
            _defuseBombFunction.Unhook(OnDefuseBombPre, HookMode.Pre);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex,
                "[Smarter-Bot] Failed to remove CCSBot::DefuseBomb hook cleanly");
        }
        _defuseBombFunction = null;
    }

    // Stops guarded Bots before the engine can enter DefuseBomb again
    private HookResult OnDefuseBombPre(DynamicHook hook)
    {
        try
        {
            nint botAddress = hook.GetParam<nint>(0);
            if (botAddress == nint.Zero) return HookResult.Continue;

            if (_fakeDefuseGuardUntil.TryGetValue(botAddress, out float guardUntil))
            {
                if (Server.CurrentTime < guardUntil) return HookResult.Stop;
                _fakeDefuseGuardUntil.Remove(botAddress);
            }
        }
        catch
        {
            return HookResult.Continue;
        }

        return HookResult.Continue;
    }

    // Resets the Bot's look-around bookkeeping so it can reacquire threats.
    // resetHidingSpots: only after flash / forced re-search — a real player who
    // just got flashed re-clears corners; routine combat shouldn't wipe progress.
    private static void ResetLookAroundForBot(
        CCSPlayerController? player, bool resetHidingSpots = false)
    {
        if (player == null || !player.IsValid || !player.IsBot) return;
        var pawn = player.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid) return;
        var bot = pawn.Bot;
        if (bot == null) return;

        ref float inhibitLookAroundTimestamp = ref bot.InhibitLookAroundTimestamp;
        inhibitLookAroundTimestamp = 0f;

        if (resetHidingSpots)
        {
            ref int checkedHidingSpotCount = ref bot.CheckedHidingSpotCount;
            checkedHidingSpotCount = 0;
        }

        ref float lookAroundStateTimestamp = ref bot.LookAroundStateTimestamp;
        lookAroundStateTimestamp = 0f;
    }
    //---------------------------------------------------------------------------------------
    // Author posture: aggressive early-round (SafeTime=0, skip enemy-spawn revisit).
    // Kept under humanlike — searching is layered on top, not instead of this.
    private static void ApplyBotState(CCSPlayerController player)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid) return;

        var bot = pawn.Bot;
        if (bot == null) return;

        ref float safeTime = ref bot.SafeTime;
        safeTime = 0f;

        ref bool hasVisitedEnemySpawn = ref bot.HasVisitedEnemySpawn;
        hasVisitedEnemySpawn = true;
    }
    //---------------------------------------------------------------------------------------
    // Cancels a reload when Valve reports a visible enemy and a usable firearm exists
    private void InterruptReload(
        CCSPlayerController player, CCSPlayerPawn pawn, CCSBot bot, float now)
    {
        if (_botController == null || !bot.IsEnemyVisible ||
            !IsReloading(player))
            return;

        int slot = player.Slot;
        if (_reloadInterruptCooldown.TryGetValue(slot, out float cooldownEnd) &&
            now < cooldownEnd)
            return;

        if (!GetReloadInterruptWeapon(pawn, out int weaponDefIndex))
            return;

        if (!BotControllerBridge.SwitchBotWeapon(
                _botController, slot, KnifeDefinitionIndex))
            return;

        _reloadInterruptCooldown[slot] = now + ReloadInterruptCooldown;
        Server.NextFrame(() => SwitchBackAfterReloadInterrupt(
            slot, weaponDefIndex));
    }

    // Valve CCSBot::Upkeep: if (IsReloading() && !enemyVisible) ClearMovement().
    // Author never removed that. We only re-apply forward motion along the last
    // travel direction so bots don't plant feet mid-route while topping off.
    private void KeepWalkingWhileReload(
        CCSPlayerController player, CCSPlayerPawn pawn, CCSBot bot, int idx)
    {
        float vx = pawn.AbsVelocity.X;
        float vy = pawn.AbsVelocity.Y;
        float speed2 = vx * vx + vy * vy;
        if (speed2 > 40f * 40f)
        {
            float inv = 1f / MathF.Sqrt(speed2);
            _lastMoveDir[idx] = (vx * inv, vy * inv);
        }

        if (!IsReloading(player) || bot.IsEnemyVisible || pawn.IsDefusing)
            return;

        if (_flashPlantedUntil.TryGetValue(idx, out float flashUntil)
            && Server.CurrentTime < flashUntil)
            return;

        if (!_lastMoveDir.TryGetValue(idx, out var dir))
            return;

        ref bool isRunning = ref bot.IsRunning;
        isRunning = true;

        // Mild cruise — enough to leave the plant, not a sprint exploit.
        const float reloadWalkSpeed = 130f;
        pawn.AbsVelocity.X = dir.X * reloadWalkSpeed;
        pawn.AbsVelocity.Y = dir.Y * reloadWalkSpeed;
    }

    // Selects a loaded primary first, then a loaded secondary weapon
    private static bool GetReloadInterruptWeapon(
        CCSPlayerPawn pawn, out int weaponDefIndex)
    {
        weaponDefIndex = -1;
        int secondaryDefIndex = -1;
        var weapons = pawn.WeaponServices?.MyWeapons;
        if (weapons == null) return false;

        foreach (var weaponHandle in weapons)
        {
            var baseWeapon = weaponHandle.Value;
            if (baseWeapon == null || !baseWeapon.IsValid) continue;

            var weapon = new CCSWeaponBase(baseWeapon.Handle);
            var weaponData = weapon.VData;
            if (weaponData == null) continue;

            int defIndex = weapon.AttributeManager.Item.ItemDefinitionIndex;
            if (defIndex <= 0) continue;

            if (weaponData.GearSlot == gear_slot_t.GEAR_SLOT_RIFLE &&
                weapon.Clip1 > 0)
            {
                weaponDefIndex = defIndex;
                return true;
            }

            if (secondaryDefIndex < 0 &&
                weaponData.GearSlot == gear_slot_t.GEAR_SLOT_PISTOL &&
                weapon.Clip1 > 0)
            {
                secondaryDefIndex = defIndex;
            }
        }

        weaponDefIndex = secondaryDefIndex;
        return weaponDefIndex >= 0;
    }

    // Returns a live Bot to the chosen firearm one frame after selecting its knife
    private void SwitchBackAfterReloadInterrupt(int slot, int weaponDefIndex)
    {
        if (_botController == null) return;

        var player = Utilities.GetPlayerFromSlot(slot);
        if (player == null || !player.IsValid || !player.IsBot ||
            !player.PawnIsAlive || player.HasBeenControlledByPlayerThisRound)
            return;

        if (!BotControllerBridge.SwitchBotWeapon(
                _botController, slot, weaponDefIndex))
        {
            Console.WriteLine(
                $"[Smarter-Bot] Reload interrupt restore failed for slot {slot}");
        }
    }

    // Repeating scan: while the Bot still has living enemies, nudge it back
    // onto its gun exactly once.
    private void ReequipGunForActiveBots()
    {
        if (_botController == null)
            return;

        var players = Utilities
            .FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
            .ToList();

        int aliveT = 0, aliveCT = 0;
        foreach (var p in players)
        {
            if (!p.IsValid || !p.PawnIsAlive) continue;
            int t = (int)p.TeamNum;
            if (t == (int)CsTeam.Terrorist) aliveT++;
            else if (t == (int)CsTeam.CounterTerrorist) aliveCT++;
        }
        if (aliveT == 0 && aliveCT == 0) return;

        foreach (var player in players)
        {
            if (!player.IsValid || !player.IsBot ||
                !player.PawnIsAlive || player.HasBeenControlledByPlayerThisRound)
                continue;

            int team = (int)player.TeamNum;
            int enemiesAlive = team == (int)CsTeam.Terrorist ? aliveCT
                             : team == (int)CsTeam.CounterTerrorist ? aliveT
                             : 0;
            // No living enemies, skip
            if (enemiesAlive == 0) continue;

            var pawn = player.PlayerPawn?.Value;
            if (pawn == null || !pawn.IsValid || pawn.IsDefusing) continue;

            // Only act when the bot is idly holding a knife; the sole purpose is
            // to stop a bot roaming with its knife while enemies live.
            var active = pawn.WeaponServices?.ActiveWeapon?.Value;
            if (active == null || !active.IsValid) continue;

            var activeWeapon = new CCSWeaponBase(active.Handle);
            if (activeWeapon.VData?.GearSlot != gear_slot_t.GEAR_SLOT_KNIFE)
                continue;

            if (!GetReequipWeapon(pawn, out int targetDef)) continue;

            BotControllerBridge.SwitchBotWeapon(_botController, player.Slot, targetDef);
        }
    }

    // Picks the Bot's main gun: a primary if owned, else a secondary. False
    // when the Bot owns neither.
    private static bool GetReequipWeapon(CCSPlayerPawn pawn, out int weaponDefIndex)
    {
        weaponDefIndex = -1;
        int secondaryDefIndex = -1;
        var weapons = pawn.WeaponServices?.MyWeapons;
        if (weapons == null) return false;

        foreach (var weaponHandle in weapons)
        {
            var baseWeapon = weaponHandle.Value;
            if (baseWeapon == null || !baseWeapon.IsValid) continue;

            var weapon = new CCSWeaponBase(baseWeapon.Handle);
            var weaponData = weapon.VData;
            if (weaponData == null) continue;

            int defIndex = weapon.AttributeManager.Item.ItemDefinitionIndex;
            if (defIndex <= 0) continue;

            if (weaponData.GearSlot == gear_slot_t.GEAR_SLOT_RIFLE)
            {
                weaponDefIndex = defIndex;
                return true;
            }

            if (secondaryDefIndex < 0 &&
                weaponData.GearSlot == gear_slot_t.GEAR_SLOT_PISTOL)
            {
                secondaryDefIndex = defIndex;
            }
        }

        weaponDefIndex = secondaryDefIndex;
        return weaponDefIndex >= 0;
    }
    //---------------------------------------------------------------------------------------
    // Reports whether the Bot's active weapon is currently reloading
    private bool IsReloading(CCSPlayerController player)
    {
        if (player == null || !player.IsValid)
            return false;

        var pawn = player.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid)
            return false;

        var activeWeapon = pawn.WeaponServices?.ActiveWeapon?.Value;
        if (activeWeapon == null || !activeWeapon.IsValid)
            return false;

        return Schema.GetRef<bool>(activeWeapon.Handle, "CCSWeaponBase", "m_bInReload");
    }
    //---------------------------------------------------------------------------------------
    // Player-like flash (realism over sweat):
    //   perceive 200–260ms → fight normally → flick away 130–170ms before boom →
    //   HOLD away 100–160ms after boom → look back 120–180ms → release.
    //   Panic: ±45° late flick; blindness is 100% CS2 engine (no facingScale). Ball-smoke hides 藏烟闪.
    private void ProcessFlashbangAvoidance()
    {
        if (_scratchEye == null) return;

        float now = Server.CurrentTime;
        float dt = Server.TickInterval > 0f ? Server.TickInterval : 0.015625f;
        bool humanlike = HumanlikeMode.Enabled;

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

    //---------------------------------------------------------------------------------------
    // 360 FOV patch system for fake defuse search phase
    //---------------------------------------------------------------------------------------

    // Enables FOV patching on supported platforms
    private void InitializeFovPatches()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
            !RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            _fovPatchesAvailable = false;
            return;
        }

        _fovPatchesAvailable = true;
    }

    // Applies the platform-specific FOV branch patches
    private void ApplyFovPatches()
    {
        if (!_fovPatchesAvailable || _appliedFovPatches.Count > 0)
            return;

        FovPatchDefinition[] patches = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ? LinuxFovPatches
            : FovPatches;

        foreach (FovPatchDefinition patch in patches)
        {
            if (!TryApplyFovPatch(patch))
            {
                RestoreAllFovPatches();
                _fovPatchesAvailable = false;
                return;
            }
        }
    }

    // Applies one FOV patch after validating its original bytes
    private bool TryApplyFovPatch(FovPatchDefinition patch)
    {
        try
        {
            nint signatureAddress = NativeAPI.FindSignature(
                Addresses.ServerPath,
                patch.Signature);

            if (signatureAddress == nint.Zero)
                return false;

            nint address = signatureAddress + patch.Offset;
            byte[] original = new byte[patch.Replacement.Length];
            Marshal.Copy(address, original, 0, original.Length);

            if (!original.SequenceEqual(patch.Expected))
                return false;

            if (!WriteExecutableMemory(address, patch.Replacement))
            {
                WriteExecutableMemory(address, original);
                return false;
            }

            _appliedFovPatches.Add(new AppliedFovPatch(patch.Name, address, original));
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Restores all FOV patches applied during the current search phase
    private void RestoreAllFovPatches()
    {
        for (int i = _appliedFovPatches.Count - 1; i >= 0; i--)
        {
            AppliedFovPatch patch = _appliedFovPatches[i];
            WriteExecutableMemory(patch.Address, patch.Original);
        }

        _appliedFovPatches.Clear();
    }

    // Writes bytes into executable text memory on Linux and restores RX permissions
    private static bool WriteLinuxExecutableMemory(nint address, byte[] bytes)
    {
        if (bytes.Length == 0)
            return true;

        long pageSize = Environment.SystemPageSize;
        if (pageSize <= 0 || (pageSize & (pageSize - 1)) != 0)
            return false;

        long addressValue = address.ToInt64();
        long endAddress;
        long pageEnd;
        try
        {
            endAddress = checked(addressValue + bytes.Length);
            pageEnd = checked((endAddress + pageSize - 1) & ~(pageSize - 1));
        }
        catch (OverflowException)
        {
            return false;
        }

        long pageStart = addressValue & ~(pageSize - 1);
        if (pageEnd <= pageStart)
            return false;

        nint pageAddress = (nint)pageStart;
        nuint pageLength = (nuint)(pageEnd - pageStart);
        if (MProtect(pageAddress, pageLength, LinuxPageExecuteReadWrite) != 0)
            return false;

        bool success = false;
        try
        {
            Marshal.Copy(bytes, 0, address, bytes.Length);
            success = true;
        }
        finally
        {
            if (MProtect(pageAddress, pageLength, LinuxPageExecuteRead) != 0)
                success = false;
        }

        return success;
    }

    // Writes bytes into executable memory and restores platform page permissions
    private static bool WriteExecutableMemory(nint address, byte[] bytes)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return WriteLinuxExecutableMemory(address, bytes);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

        if (!VirtualProtect(address, (nuint)bytes.Length, PageExecuteReadWrite, out uint oldProtect))
            return false;

        bool success = false;
        try
        {
            Marshal.Copy(bytes, 0, address, bytes.Length);
            success = FlushInstructionCache(GetCurrentProcess(), address, (nuint)bytes.Length);
        }
        finally
        {
            if (!VirtualProtect(address, (nuint)bytes.Length, oldProtect, out _))
                success = false;
        }

        return success;
    }

    // Changes page protection on Windows
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualProtect(
        nint address,
        nuint size,
        uint newProtect,
        out uint oldProtect);

    // Changes page permissions on Linux
    [DllImport("libc.so.6", EntryPoint = "mprotect", SetLastError = true)]
    private static extern int MProtect(
        nint address,
        nuint size,
        int protection);

    // Returns the current process handle on Windows
    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    // Flushes modified instruction bytes from the Windows instruction cache
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushInstructionCache(
        nint process,
        nint baseAddress,
        nuint size);
}
//---------------------------------------------------------------------------------------
