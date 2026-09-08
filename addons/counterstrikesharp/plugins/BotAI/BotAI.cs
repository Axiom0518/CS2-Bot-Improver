using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;
using Common;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;

namespace BotAI;

public record PatchInfo(string Name, nint Address, List<byte> OriginalBytes);

public static class BotOffsets
{
    // Differs by platform: Windows = 0x5128, Linux  = 0x5100.
    public static readonly int m_gameState =
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? 0x5100 : 0x5128;
    // Offsets inside CSGameState
    public const int m_isRoundOver = 0x08;
    public const int m_bombState = 0x0C;
    public const int m_plantedBombsite = 0x68;

}

[MinimumApiVersion(304)]
public class BotAI : BasePlugin
{
    public override string ModuleName => "Patches - Bot AI";
    public override string ModuleVersion => "1.9.1";
    public override string ModuleAuthor => "K4ryuu & Austin (updated by ed0ard & Misaka17032 & XBribo & AmagiReina)";
    public override string ModuleDescription =>
        "Improve and fix bots' behavior comprehensively";

    private readonly List<PatchInfo> _appliedPatches = [];
    private readonly bool _isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    // Only the worst wallhack / no-turn-flash patches stay deferred in humanlike.
    // Combat helpers (aim drift zero, bomb hear) stay applied so bots are not "vanilla weak".
    private static readonly HashSet<string> HumanlikeSkippedPatches = new(StringComparer.OrdinalIgnoreCase)
    {
        "IsNoticable_AlwaysTrue",
        "InViewCone_RemoveOuterFOV",
        "InViewCone_RemoveInnerFOV",
        "OnAudibleEvent_GlobalHearRange",
        "FlashbangAvoidance_Disable",
    };

    // Resolved but not written while humanlike; can be applied when switching to hard mode.
    private readonly Dictionary<string, (nint Addr, List<byte> Bytes, string Expected)> _deferredHardPatches = new();

    public FakeConVar<int> BotHumanlike = new(
        "bot_humanlike",
        "1=humanlike awareness/flash/aim drift (default), 0=hard wallhack-style patches",
        1);

    public override void Load(bool hotReload)
    {
        Logger.LogInformation("Bot AI Patches loading...");
        RegisterFakeConVars(this);

        HumanlikeMode.Enabled = BotHumanlike.Value != 0;
        BotHumanlike.ValueChanged += OnHumanlikeChanged;

        var patchDefinitions = _isLinux ? LinuxPatchDefinitions.All : WindowsPatchDefinitions.All;

        // "<name>_Cave" entries build a code cave that their "<name>" partner then
        // jumps into. The pair must be applied atomically: if the cave is missing
        // (e.g. its signature no longer matches after a game update), the partner's
        // jump lands in unpatched bytes and the server segfaults as soon as a bot
        // takes that code path.
        var caveNames = patchDefinitions.Keys.Where(n => n.EndsWith("_Cave")).ToHashSet();
        var appliedCaves = new HashSet<string>();

        // Phase 1 — resolve every signature BEFORE any write. A cave's signature is
        // the CC padding the cave patch itself overwrites, so it cannot be re-resolved
        // once written, and the displacement math below needs both pair addresses.
        var sites = new Dictionary<string, nint>();
        foreach (var (name, def) in patchDefinitions)
        {
            nint sigAddr = NativeAPI.FindSignature(GameUtils.GetModulePath("server"), def.signature);
            if (sigAddr == 0) { Logger.LogError($"'{name}': signature not found."); continue; }
            sites[name] = sigAddr + def.patchOffset;
        }

        // Phase 2 — compute rel32 fields from the resolved addresses (Linux cave
        // pairs; no-op elsewhere). A failed fixup marks the entry unappliable so the
        // pair dies atomically instead of writing a jump with a wrong displacement.
        var patchBytes = new Dictionary<string, List<byte>>();
        foreach (var (name, def) in patchDefinitions)
        {
            if (!sites.ContainsKey(name)) continue;
            var bytes = ParseHex(def.patch);
            if (_isLinux && !LinuxDisplacementFixups.Apply(name, bytes, sites, Logger))
            {
                sites.Remove(name);
                continue;
            }
            patchBytes[name] = bytes;
        }

        // Phase 3 — write caves first, then partners (1.8.8 atomic ordering).
        foreach (var name in caveNames)
        {
            if (sites.TryGetValue(name, out nint addr) && WritePatch(name, addr, patchBytes[name], patchDefinitions[name].expectedOriginal))
            { appliedCaves.Add(name); Logger.LogInformation($"{name}: applied."); }
            else Logger.LogError($"{name}: FAILED.");
        }

        bool humanlike = HumanlikeMode.Enabled;

        foreach (var name in patchDefinitions.Keys)
        {
            if (caveNames.Contains(name)) continue;

            string caveName = $"{name}_Cave";
            if (caveNames.Contains(caveName) && !appliedCaves.Contains(caveName))
            {
                Logger.LogWarning($"{name}: skipped, its cave partner '{caveName}' did not apply.");
                continue;
            }

            if (!sites.TryGetValue(name, out nint addr) || !patchBytes.TryGetValue(name, out var bytes))
            {
                Logger.LogError($"{name}: FAILED.");
                continue;
            }

            // Defer wallhack / instant-lock / no-turn-flash patches in humanlike mode.
            if (humanlike && HumanlikeSkippedPatches.Contains(name))
            {
                _deferredHardPatches[name] = (addr, bytes, patchDefinitions[name].expectedOriginal);
                Logger.LogInformation($"{name}: deferred (bot_humanlike 1).");
                continue;
            }

            bool ok = WritePatch(name, addr, bytes, patchDefinitions[name].expectedOriginal);
            if (ok) Logger.LogInformation($"{name}: applied.");
            else
            {
                Logger.LogError($"{name}: FAILED.");
                // Roll back the now-orphaned cave so we never leave half a pair behind.
                if (appliedCaves.Contains(caveName))
                {
                    var cave = _appliedPatches.FirstOrDefault(p => p.Name == caveName);
                    if (cave != null)
                    {
                        RestorePatch(cave);
                        _appliedPatches.Remove(cave);
                        appliedCaves.Remove(caveName);
                        Logger.LogWarning($"{caveName}: rolled back, its partner '{name}' did not apply.");
                    }
                }
            }
        }

        RegisterEventHandler<EventPlayerSpawn>((@event, info) =>
        {
            var player = @event.Userid;
            if (player?.IsValid != true || !player.IsBot) return HookResult.Continue;

            var pawn = player.PlayerPawn.Value;
            if (pawn?.IsValid != true
                || player.Team <= CsTeam.Spectator
                || !pawn.BotAllowActive)
                return HookResult.Continue;

            var gameRules = Utilities
                .FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
                .FirstOrDefault()?.GameRules;

            if (gameRules == null || gameRules.BombPlanted) return HookResult.Continue;

            UpdateBotBombState(pawn, player.PlayerName);
            return HookResult.Continue;
        });

        Logger.LogInformation(
            $"Applied {_appliedPatches.Count}/{patchDefinitions.Count} patches (humanlike={humanlike}, deferred={_deferredHardPatches.Count}).");
    }

    public override void Unload(bool hotReload)
    {
        Logger.LogInformation("Bot AI Patches unloading...");
        BotHumanlike.ValueChanged -= OnHumanlikeChanged;
        foreach (var patch in _appliedPatches) RestorePatch(patch);
        _appliedPatches.Clear();
        _deferredHardPatches.Clear();
        Logger.LogInformation("All patches restored.");
    }

    private void OnHumanlikeChanged(object? sender, int value)
    {
        bool wantHumanlike = value != 0;
        HumanlikeMode.Enabled = wantHumanlike;

        if (wantHumanlike)
            DeferHardPatchesFromApplied();
        else
            ApplyDeferredHardPatches();

        Logger.LogInformation($"bot_humanlike -> {value} (deferred hard patches={_deferredHardPatches.Count})");
        Server.PrintToConsole($"[BotAI] bot_humanlike = {value}");
    }

    private void DeferHardPatchesFromApplied()
    {
        var toRemove = _appliedPatches
            .Where(p => HumanlikeSkippedPatches.Contains(p.Name))
            .ToList();

        foreach (var patch in toRemove)
        {
            // Keep bytes so hard mode can re-apply without re-resolving signatures.
            var hardBytes = new List<byte>(patch.OriginalBytes.Count);
            for (int i = 0; i < patch.OriginalBytes.Count; i++)
                hardBytes.Add(Marshal.ReadByte(patch.Address, i));

            RestorePatch(patch);
            _appliedPatches.Remove(patch);
            _deferredHardPatches[patch.Name] = (patch.Address, hardBytes, string.Empty);
            Logger.LogInformation($"{patch.Name}: restored (humanlike).");
        }
    }

    private void ApplyDeferredHardPatches()
    {
        foreach (var (name, site) in _deferredHardPatches.ToList())
        {
            if (_appliedPatches.Any(p => p.Name == name)) continue;

            // expectedOriginal empty means we already validated once; skip re-check.
            if (string.IsNullOrEmpty(site.Expected))
            {
                if (!WritePatchUnchecked(name, site.Addr, site.Bytes))
                    Logger.LogError($"{name}: re-apply FAILED.");
                else
                    Logger.LogInformation($"{name}: applied (hard mode).");
            }
            else if (WritePatch(name, site.Addr, site.Bytes, site.Expected))
            {
                Logger.LogInformation($"{name}: applied (hard mode).");
            }
            else
            {
                Logger.LogError($"{name}: apply FAILED.");
            }
        }
    }

    // ── Patch machinery ───────────────────────────────────────────────────────

    // Write-only: the address is resolved (and displacement fields computed) before
    // any patch is written — see the phased Load above.
    private bool WritePatch(string name, nint addr, List<byte> patchBytes, string expectedOriginal)
    {
        try
        {
            if (patchBytes.Count == 0 || !IsValid(addr)) return false;

            var origBytes = new List<byte>();
            for (int i = 0; i < patchBytes.Count; i++)
                origBytes.Add(Marshal.ReadByte(addr, i));

            if (!string.IsNullOrEmpty(expectedOriginal) && !ValidateOrig(name, origBytes, expectedOriginal))
            {
                Logger.LogError($"'{name}': byte mismatch. Expected [{expectedOriginal}] " +
                                $"got [{string.Join(" ", origBytes.Select(b => $"{b:X2}"))}].");
                return false;
            }

            if (!MemoryPatch.SetMemAccess(addr, patchBytes.Count)) return false;
            for (int i = 0; i < patchBytes.Count; i++) Marshal.WriteByte(addr, i, patchBytes[i]);

            _appliedPatches.Add(new PatchInfo(name, addr, origBytes));
            Logger.LogInformation($"'{name}' patched at 0x{addr:X} ({patchBytes.Count} bytes).");
            return true;
        }
        catch (Exception ex) { Logger.LogError($"'{name}': {ex.Message}"); return false; }
    }

    private bool WritePatchUnchecked(string name, nint addr, List<byte> patchBytes)
    {
        try
        {
            if (patchBytes.Count == 0 || !IsValid(addr)) return false;

            var origBytes = new List<byte>();
            for (int i = 0; i < patchBytes.Count; i++)
                origBytes.Add(Marshal.ReadByte(addr, i));

            if (!MemoryPatch.SetMemAccess(addr, patchBytes.Count)) return false;
            for (int i = 0; i < patchBytes.Count; i++) Marshal.WriteByte(addr, i, patchBytes[i]);

            _appliedPatches.Add(new PatchInfo(name, addr, origBytes));
            return true;
        }
        catch (Exception ex) { Logger.LogError($"'{name}': {ex.Message}"); return false; }
    }

    private void RestorePatch(PatchInfo p)
    {
        try
        {
            if (!IsValid(p.Address)) return;
            if (!MemoryPatch.SetMemAccess(p.Address, p.OriginalBytes.Count)) return;
            for (int i = 0; i < p.OriginalBytes.Count; i++)
                Marshal.WriteByte(p.Address, i, p.OriginalBytes[i]);
        }
        catch (Exception ex) { Logger.LogError($"Restore '{p.Name}': {ex.Message}"); }
    }

    private bool ValidateOrig(string name, List<byte> actual, string expectedHex)
    {
        try
        {
            var tokens = expectedHex.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (actual.Count != tokens.Length) return false;
            for (int i = 0; i < tokens.Length; i++)
            {
                if (tokens[i] == "?") continue;
                if (actual[i] != Convert.ToByte(tokens[i], 16)) return false;
            }
            return true;
        }
        catch { return false; }
    }

    private static bool IsValid(nint addr)
    {
        if (addr == nint.Zero) return false;
        try { Marshal.ReadByte(addr); return true; }
        catch { return false; }
    }

    private static List<byte> ParseHex(string hex) =>
        [.. hex.Split(' ', StringSplitOptions.RemoveEmptyEntries)
               .Where(t => t != "?")
               .Select(t => Convert.ToByte(t, 16))];

    private bool UpdateBotBombState(CCSPlayerPawn pawn, string playerName)
    {
        try
        {
            if (pawn?.Bot?.Handle is not { } handle || handle == nint.Zero) return false;
            if (!IsValid(handle)) return false;

            nint gsPtr = handle + BotOffsets.m_gameState;
            if (!IsValid(gsPtr)) return false;
            if (Marshal.ReadByte(gsPtr + BotOffsets.m_isRoundOver) != 0) return true;

            nint bombAddr = gsPtr + BotOffsets.m_bombState;
            if (!IsValid(bombAddr)) return false;
            if (!MemoryPatch.SetMemAccess(bombAddr, sizeof(int))) return false;
            if (Marshal.ReadInt32(bombAddr) != 0) Marshal.WriteInt32(bombAddr, 0);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError($"UpdateBotBombState({playerName}): {ex.Message}");
            return false;
        }
    }
}
