using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace BotHiderImpl;

public partial class BotHiderImplPlugin
{
    private CounterStrikeSharp.API.Modules.Timers.Timer? _fastApplyTimer;
    private int _fastApplyRemaining;

    // Schedules managed bots to approve a newly started vote
    [GameEventHandler]
    public HookResult OnVoteOptions(EventVoteOptions @event, GameEventInfo info)
    {
        Server.NextFrame(AcceptVoteForManagedBots);
        return HookResult.Continue;
    }

    // Casts a yes vote from every valid managed bot
    private void AcceptVoteForManagedBots()
    {
        if (_client == null) return;

        var voteController = Utilities
            .FindAllEntitiesByDesignerName<CVoteController>("vote_controller")
            .FirstOrDefault(controller => controller.IsValid);
        if (voteController == null)
        {
            Server.PrintToConsole("[BotHider] automatic vote failed: vote controller not found");
            return;
        }

        Span<int> votesCast = voteController.VotesCast;
        Span<int> optionCounts = voteController.VoteOptionCount;
        int onlyTeam = voteController.OnlyTeamToVote;
        int accepted = 0;

        foreach (int slot in _client.GetManagedSlots())
        {
            if ((uint)slot >= (uint)votesCast.Length) continue;

            var player = Utilities.GetPlayerFromSlot(slot);
            if (player == null || !player.IsValid) continue;
            if (onlyTeam >= (int)CsTeam.Terrorist && (int)player.Team != onlyTeam) continue;

            votesCast[slot] = 0;
            accepted++;

            var voteEvent = new EventVoteCast(true)
            {
                Team = onlyTeam,
                Userid = player,
                VoteOption = 0
            };
            voteEvent.FireEvent(false);
        }

        if (accepted == 0) return;
        optionCounts[0] += accepted;
        Utilities.SetStateChanged(
            voteController, "CVoteController", "m_nVoteOptionCount");
        Server.PrintToConsole($"[BotHider] automatic yes votes cast={accepted}");
    }

    // Respawn any managed bot that is not alive
    private void RespawnDeadManagedBots()
    {
        if (_client == null) return;

        // Current team headcount across everyone, for balancing unassigned bots
        int tCount = 0, ctCount = 0;
        foreach (var pl in Utilities.GetPlayers())
        {
            if (pl == null || !pl.IsValid) continue;
            if (pl.Team == CsTeam.Terrorist) ++tCount;
            else if (pl.Team == CsTeam.CounterTerrorist) ++ctCount;
        }

        foreach (int slot in _client.GetManagedSlots())
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player == null || !player.IsValid || player.PawnIsAlive) continue;

            // Dead but unassigned (team=None/Spectator): give it the smaller team first
            if (player.Team != CsTeam.Terrorist && player.Team != CsTeam.CounterTerrorist)
            {
                CsTeam target = (tCount <= ctCount) ? CsTeam.Terrorist : CsTeam.CounterTerrorist;
                try
                {
                    player.SwitchTeam(target);
                    if (target == CsTeam.Terrorist) ++tCount; else ++ctCount;
                }
                catch (Exception e)
                {
                    Server.PrintToConsole($"[BotHider] SwitchTeam failed slot={slot}: {e.Message}");
                    continue;
                }
            }

            try
            {
                player.Respawn();
            }
            catch (Exception e)
            {
                Server.PrintToConsole($"[BotHider] respawn failed slot={slot}: {e.Message}");
            }
        }
    }

    // Opens a short high-frequency apply window for early-round fields
    private void StartFastApplyWindow()
    {
        _fastApplyRemaining = Math.Max(_fastApplyRemaining, 80);
        if (_fastApplyTimer != null) return;
        _fastApplyTimer = AddTimer(0.25f, RunFastApplyTick, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
    }

    // Runs one early apply retry tick
    private void RunFastApplyTick()
    {
        ApplyManagedSlots();
        _fastApplyRemaining--;
        if (_fastApplyRemaining > 0) return;
        _fastApplyTimer?.Kill();
        _fastApplyTimer = null;
    }

}
