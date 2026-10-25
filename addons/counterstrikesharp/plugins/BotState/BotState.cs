using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BotControllerApi;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using OnMapEnd = CounterStrikeSharp.API.Core.Listeners.OnMapEnd;
using OnTick = CounterStrikeSharp.API.Core.Listeners.OnTick;
using Microsoft.Extensions.Logging;

namespace BotState;

public partial class BotState : BasePlugin
{
	private static class BotControllerBridge
	{
		[MethodImpl(MethodImplOptions.NoInlining)]
		public static bool IsViewControlled(object api, int slot)
		{
			//IL_0001: Unknown result type (might be due to invalid IL or missing references)
			//IL_0007: Expected Obj, but got Unknown
			IBotControllerApi val = (IBotControllerApi)api;
			if (!val.IsLocked(slot, (LockKind)0) && !val.IsLocked(slot, (LockKind)1))
			{
				return val.IsReplaying(slot);
			}
			return true;
		}

		[MethodImpl(MethodImplOptions.NoInlining)]
		public static object? TryGet()
		{
			return new PluginCapability<IBotControllerApi>("botcontroller:api").Get();
		}

		[MethodImpl(MethodImplOptions.NoInlining)]
		public static bool SwitchBotWeapon(object api, int slot, int defIndex)
		{
			//IL_0001: Unknown result type (might be due to invalid IL or missing references)
			return ((IBotControllerApi)api).SwitchBotWeapon(slot, defIndex);
		}

		[MethodImpl(MethodImplOptions.NoInlining)]
		public static long InjectUsercmd(object api, int slot, ulong buttonMask, int durationMs = 0)
		{
			//IL_0001: Unknown result type (might be due to invalid IL or missing references)
			return ((IBotControllerApi)api).InjectUsercmd(slot, buttonMask, durationMs);
		}

		[MethodImpl(MethodImplOptions.NoInlining)]
		public static long StartUsercmdSuppression(object api, int slot, ulong buttonMask)
		{
			//IL_0001: Unknown result type (might be due to invalid IL or missing references)
			return ((IBotControllerApi)api).StartUsercmdSuppression(slot, buttonMask);
		}

		[MethodImpl(MethodImplOptions.NoInlining)]
		public static bool CancelUsercmdSuppression(object api, int slot, long suppressionId)
		{
			//IL_0001: Unknown result type (might be due to invalid IL or missing references)
			return ((IBotControllerApi)api).CancelUsercmdSuppression(slot, suppressionId);
		}

		[MethodImpl(MethodImplOptions.NoInlining)]
		public static bool LockKnife(object api, int slot)
		{
			//IL_0001: Unknown result type (might be due to invalid IL or missing references)
			return ((IBotControllerApi)api).Lock(slot, (LockTarget)3);
		}

		[MethodImpl(MethodImplOptions.NoInlining)]
		public static bool IsKnifeLocked(object api, int slot)
		{
			//IL_0001: Unknown result type (might be due to invalid IL or missing references)
			//IL_0007: Unknown result type (might be due to invalid IL or missing references)
			//IL_000d: Invalid comparison between Unknown and I4
			return (int)((IBotControllerApi)api).GetWeaponLock(slot) == 3;
		}

		[MethodImpl(MethodImplOptions.NoInlining)]
		public static bool UnlockWeapon(object api, int slot)
		{
			//IL_0001: Unknown result type (might be due to invalid IL or missing references)
			return ((IBotControllerApi)api).Unlock(slot, (LockKind)2);
		}
	}


	private sealed record FovPatchDefinition(string Name, string Signature, int Offset, byte[] Expected, byte[] Replacement);

	private sealed record AppliedFovPatch(string Name, nint Address, byte[] Original);

	private enum RushAntennaStatus
	{
		Read,
		Unavailable
	}

	private const ulong UseButtonMask = 32uL;

	private const float FakeDefuseHoldMinSeconds = 0.1f;

	private const float FakeDefuseHoldMaxSeconds = 0.8f;

	private const float FakeDefuseSearchMinSeconds = 2f;

	private const float FakeDefuseSearchMaxSeconds = 4f;

	private const string DefuseBombWindowsSignature = "48 8D 91 08 04 00 00 E9 ? ? ? ?";

	private const string DefuseBombLinuxSignature = "48 8D B7 00 04 00 00 E9 ? ? ? ?";

	private readonly Dictionary<int, float> _fakeDefuseCooldown = new Dictionary<int, float>();

	private readonly Dictionary<nint, float> _fakeDefuseGuardUntil = new Dictionary<nint, float>();

	private readonly Dictionary<nint, int> _fakeDefuseCounts = new Dictionary<nint, int>();

	private readonly HashSet<int> _fakeDefuseSearchingBots = new HashSet<int>();

	private readonly HashSet<int> _pendingDefuseRestore = new HashSet<int>();

	private readonly Dictionary<int, long> _fakeDefuseSuppressionIds = new Dictionary<int, long>();

	private MemoryFunctionVoid<nint>? _defuseBombFunction;

	private readonly Dictionary<int, float> _lastLateralDir = new Dictionary<int, float>();

	private readonly HashSet<int> _hasFiredThisAttack = new HashSet<int>();

	private readonly Dictionary<int, bool> _prevIsAttacking = new Dictionary<int, bool>();

	private object? _botController;

	private readonly Random _random = new Random();

	private BotFovOptions _fovOptions = new BotFovOptions();

	private BotFovHooks? _customFov;

	private string _customFovStatus = "native";

	private int _fovRosterTick = -1;

	private readonly Dictionary<nint, CCSPlayerController> _fovObservers = new Dictionary<nint, CCSPlayerController>(64);

	private readonly Dictionary<nint, (uint Pawn, System.Numerics.Vector3 Eye, System.Numerics.Vector3 Angles, BotViewFrustum Frame)> _fovFrames = new Dictionary<nint, (uint, System.Numerics.Vector3, System.Numerics.Vector3, BotViewFrustum)>(64);

	private const string FovUsage = "css_bot_fov status | native | <horizontal> [vertical]";


	private const uint PageExecuteReadWrite = 64u;

	private static readonly FovPatchDefinition[] FovPatches = new FovPatchDefinition[2]
	{
		new FovPatchDefinition("IsVisiblePos_IgnoreFOV", "48 8D 05 ? ? ? ? 48 C7 45 ? ? ? ? ? 48 89 45 ? 45 0F B6 E8", 19, new byte[4] { 69, 15, 182, 232 }, new byte[4] { 69, 51, 237, 144 }),
		new FovPatchDefinition("IsVisiblePlayer_IgnoreFOV", "48 8D 05 ? ? ? ? 48 C7 45 ? ? ? ? ? 48 89 45 ? 41 0F B6 D8", 19, new byte[4] { 65, 15, 182, 216 }, new byte[4] { 51, 219, 144, 144 })
	};

	private static readonly FovPatchDefinition[] LinuxFovPatches = new FovPatchDefinition[2]
	{
		new FovPatchDefinition("IsVisiblePos_IgnoreFOV", "80 BD ? ? ? ? ? 74 ? 48 8B 7B ? 48 8B B5", 7, new byte[1] { 116 }, new byte[1] { 235 }),
		new FovPatchDefinition("IsVisiblePlayer_IgnoreFOV", "45 84 F6 0F 85 ? ? ? ? 48 89 DF FF 90", 3, new byte[6] { 15, 133, 182, 0, 0, 0 }, new byte[6] { 144, 144, 144, 144, 144, 144 })
	};

	private const int LinuxPageRead = 1;

	private const int LinuxPageWrite = 2;

	private const int LinuxPageExecute = 4;

	private const int LinuxPageExecuteReadWrite = 7;

	private const int LinuxPageExecuteRead = 5;

	private readonly List<AppliedFovPatch> _appliedFovPatches = new List<AppliedFovPatch>();

	private bool _fovPatchesAvailable;

	private bool _isFreezeTime;

	private readonly Dictionary<int, bool> _prevInAir = new Dictionary<int, bool>();

	private readonly Dictionary<int, float> _lastForwardDir = new Dictionary<int, float>();

	private readonly Dictionary<int, float> _ladderExitTime = new Dictionary<int, float>();

	private readonly Dictionary<int, float> _doorEventCooldown = new Dictionary<int, float>();

	private readonly Dictionary<int, float> _stuckStartTime = new Dictionary<int, float>();

	private readonly Dictionary<int, Vector> _stuckStartPos = new Dictionary<int, Vector>();

	private readonly Dictionary<int, bool> _stuckJumpDone = new Dictionary<int, bool>();

	private readonly Dictionary<int, int> _stuckJumpCount = new Dictionary<int, int>();

	private readonly Dictionary<int, float> _stuckMaxSpeed = new Dictionary<int, float>();

	private readonly Dictionary<int, float> _idleStartTime = new Dictionary<int, float>();

	private readonly Dictionary<int, float> _lastRepathTime = new Dictionary<int, float>();

	private readonly Dictionary<int, bool> _cachedInAir = new Dictionary<int, bool>();

	private readonly Dictionary<int, bool> _cachedNearLadder = new Dictionary<int, bool>();

	private const string RushAntennaMarkerClass = "info_target";

	private const float RushAntennaPublishInterval = 0.1f;

	private const float RushRoundTime = 0.75f;

	private Timer? _rushAntennaTimer;

	private const float HurtRevealSeconds = 0.8f;

	private const float DefuseRevealSeconds = 1.5f;

	private const float DefuseHiddenSeconds = 3.5f;

	private bool _isBombBeingDefused;

	private int _defuserSlot = -1;

	private float _defuseRevealUntil;

	private Timer? _defuseRevealTimer;

	private readonly Dictionary<int, float> _revealUntil = new Dictionary<int, float>();

	private const int KnifeDefinitionIndex = 9001;

	private const float ReloadInterruptCooldown = 0.75f;

	private const ulong InspectButtonMask = 34359738368uL;

	private Timer? _gunReequipTimer;

	private readonly Dictionary<int, float> _reloadInterruptCooldown = new Dictionary<int, float>();

	private readonly HashSet<int> _knifeLockedBotSlots = new HashSet<int>();

	private bool _eliminationHandled;

	public override string ModuleName => "Smarter-Bot";

	public override string ModuleVersion => "1.10.0";

	public override string ModuleAuthor => "ed0ard & XBribo & unicbm";

	public override string ModuleDescription => "Make bots smarter";

	private bool CustomFovActive
	{
		get
		{
			if (_fovOptions.Enabled)
			{
				BotFovHooks customFov = _customFov;
				if (customFov != null)
				{
					return !customFov.Failed;
				}
				return false;
			}
			return false;
		}
	}

	private static void ResetLookAroundForBot(CCSPlayerController? player)
	{
		if ((CEntityInstance)(object)player == (CEntityInstance)null || !((CEntityInstance)player).IsValid || !player.IsBot)
		{
			return;
		}
		CCSPlayerPawn val = player.PlayerPawn?.Value;
		if (!((CEntityInstance)(object)val == (CEntityInstance)null) && ((CEntityInstance)val).IsValid)
		{
			CCSBot bot = val.Bot;
			if (bot != null)
			{
				bot.InhibitLookAroundTimestamp = 0f;
				bot.CheckedHidingSpotCount = 0;
				bot.LookAroundStateTimestamp = 0f;
			}
		}
	}

	private static void ApplyBotState(CCSPlayerController player)
	{
		CCSPlayerPawn value = player.PlayerPawn.Value;
		if (!((CEntityInstance)(object)value == (CEntityInstance)null) && ((CEntityInstance)value).IsValid)
		{
			CCSBot bot = value.Bot;
			if (bot != null)
			{
				bot.SafeTime = 0f;
				bot.HasVisitedEnemySpawn = true;
			}
		}
	}

	private static void UpdateBotBehavior(CCSBot bot, float now)
	{
		bot.IsSleeping = false;
		bot.AllowActive = true;
		bot.IsRapidFiring = true;
		bot.PeripheralTimestamp = 0f;
		bot.FireWeaponTimestamp = 0f;
		CountdownTimer alertTimer = bot.AlertTimer;
		ref float duration = ref alertTimer.Duration;
		duration = 600f;
		alertTimer.Timestamp = now + duration;
		alertTimer.Timescale = 1f;
		CountdownTimer ignoreEnemiesTimer = bot.IgnoreEnemiesTimer;
		ignoreEnemiesTimer.Duration = 0f;
		ignoreEnemiesTimer.Timestamp = 0f;
		ignoreEnemiesTimer.Timescale = 1f;
		CountdownTimer panicTimer = bot.PanicTimer;
		panicTimer.Duration = 0f;
		panicTimer.Timestamp = 0f;
		panicTimer.Timescale = 1f;
		CountdownTimer surpriseTimer = bot.SurpriseTimer;
		surpriseTimer.Duration = 0f;
		surpriseTimer.Timestamp = 0f;
		surpriseTimer.Timescale = 1f;
		bot.IsEnemySniperVisible = true;
		CountdownTimer sawEnemySniperTimer = bot.SawEnemySniperTimer;
		ref float duration2 = ref sawEnemySniperTimer.Duration;
		duration2 = 600f;
		sawEnemySniperTimer.Timestamp = now + duration2;
		sawEnemySniperTimer.Timescale = 1f;
		bot.IsWaitingBehindFriend = false;
		CountdownTimer politeTimer = bot.PoliteTimer;
		politeTimer.Duration = 0f;
		politeTimer.Timestamp = 0f;
		politeTimer.Timescale = 1f;
	}

	private HookResult OnBombAbortDefuse(EventBombAbortdefuse @event, GameEventInfo info)
	{
		StopDefuseReveal();
		return (HookResult)0;
	}

	private HookResult OnBombDefused(EventBombDefused @event, GameEventInfo info)
	{
		StopDefuseReveal();
		return (HookResult)0;
	}

	private HookResult OnBombExploded(EventBombExploded @event, GameEventInfo info)
	{
		StopDefuseReveal();
		return (HookResult)0;
	}

	private HookResult OnBombPlanted(EventBombPlanted @event, GameEventInfo info)
	{
		foreach (CCSPlayerController item in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
		{
			if (!((CEntityInstance)item).IsValid || !item.IsBot)
			{
				continue;
			}
			CCSPlayerPawn value = item.PlayerPawn.Value;
			if (!((CEntityInstance)(object)value == (CEntityInstance)null) && ((CEntityInstance)value).IsValid)
			{
				CCSBot bot = value.Bot;
				if (bot != null && !item.HasBeenControlledByPlayerThisRound)
				{
					CountdownTimer hurryTimer = bot.HurryTimer;
					ref float duration = ref hurryTimer.Duration;
					duration = 40f;
					hurryTimer.Timestamp = Server.CurrentTime + duration;
					hurryTimer.Timescale = 1f;
					((CBot)bot).IsRunning = true;
				}
			}
		}
		return (HookResult)0;
	}

	private HookResult OnBombBeginDefuse(EventBombBegindefuse @event, GameEventInfo info)
	{
		//IL_0136: Unknown result type (might be due to invalid IL or missing references)
		ResetLookAroundForBot(@event.Userid);
		CCSPlayerController player = @event.Userid;
		_defuserSlot = (((CEntityInstance)(object)player != (CEntityInstance)null && ((CEntityInstance)player).IsValid) ? player.Slot : (-1));
		_isBombBeingDefused = true;
		StartDefuseRevealCycle();
		if (!((CEntityInstance)(object)player == (CEntityInstance)null) && ((CEntityInstance)player).IsValid && player.IsBot)
		{
			CCSPlayerPawn val = player.PlayerPawn?.Value;
			if (!((CEntityInstance)(object)val == (CEntityInstance)null) && ((CEntityInstance)val).IsValid)
			{
				CCSBot bot = val.Bot;
				if (bot != null)
				{
					if (!player.HasBeenControlledByPlayerThisRound)
					{
						if (Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller").Any((CCSPlayerController p) => ((CEntityInstance)p).IsValid && p.PawnIsAlive && (((CBaseEntity)p).TeamNum == 2 || ((CBaseEntity)p).TeamNum == 3) && ((CBaseEntity)p).TeamNum != ((CBaseEntity)player).TeamNum))
						{
							CPlayer_ItemServices itemServices = ((CBasePlayerPawn)val).ItemServices;
							object obj = (object)((((itemServices != null) ? new nint?(((NativeObject)itemServices).Handle) : ((nint?)null)) == IntPtr.Zero) ? ((CCSPlayer_ItemServices)null) : new CCSPlayer_ItemServices(((NativeObject)((CBasePlayerPawn)val).ItemServices).Handle));
							double num = ((obj != null && ((CCSPlayer_ItemServices)obj).HasDefuser) ? 0.2 : 0.66);
							int valueOrDefault = _fakeDefuseCounts.GetValueOrDefault(((NativeObject)bot).Handle);
							double num2 = num * Math.Pow(0.66, valueOrDefault);
							int slot = player.Slot;
							if ((!_fakeDefuseCooldown.TryGetValue(slot, out var value) || !(Server.CurrentTime < value)) && _random.NextDouble() < num2)
							{
								ScheduleFakeDefuse(player);
							}
						}
						return (HookResult)0;
					}
					return (HookResult)0;
				}
				return (HookResult)0;
			}
			return (HookResult)0;
		}
		return (HookResult)0;
	}

	private void ScheduleFakeDefuse(CCSPlayerController player)
	{
		if (_botController != null && _defuseBombFunction != null)
		{
			float num = 0.1f + (float)_random.NextDouble() * 0.7f;
			float searchSeconds = 2f + (float)_random.NextDouble() * 2f;
			float currentTime = Server.CurrentTime;
			int slot = player.Slot;
			_fakeDefuseCooldown[slot] = currentTime + num + searchSeconds;
			((BasePlugin)this).AddTimer(num, (Action)(() =>
			{
				FinishFakeDefuse(slot, searchSeconds);
			}), (TimerFlags?)(TimerFlags)2);
		}
	}

	private void FinishFakeDefuse(int slot, float searchSeconds)
	{
		if (_botController == null)
		{
			return;
		}
		CCSPlayerController playerFromSlot = Utilities.GetPlayerFromSlot(slot);
		if ((CEntityInstance)(object)playerFromSlot == (CEntityInstance)null || !((CEntityInstance)playerFromSlot).IsValid || !playerFromSlot.IsBot || !playerFromSlot.PawnIsAlive || playerFromSlot.HasBeenControlledByPlayerThisRound)
		{
			return;
		}
		CCSPlayerPawn val = playerFromSlot.PlayerPawn?.Value;
		if ((CEntityInstance)(object)val == (CEntityInstance)null || !((CEntityInstance)val).IsValid || !val.IsDefusing)
		{
			return;
		}
		CCSBot bot = val.Bot;
		if (bot == null)
		{
			return;
		}
		long num = BotControllerBridge.StartUsercmdSuppression(_botController, slot, 32uL);
		if (num > 0)
		{
			_fakeDefuseSuppressionIds[slot] = num;
			_fakeDefuseGuardUntil[((NativeObject)bot).Handle] = Server.CurrentTime + searchSeconds;
			_fakeDefuseCounts[((NativeObject)bot).Handle] = _fakeDefuseCounts.GetValueOrDefault(((NativeObject)bot).Handle) + 1;
			_pendingDefuseRestore.Add(slot);
			bot.StateTimestamp = Server.CurrentTime - 2f;
			ResetLookAroundForBot(playerFromSlot);
			_fakeDefuseSearchingBots.Add(slot);
			ApplyFovPatches();
			((BasePlugin)this).AddTimer(searchSeconds, (Action)(() =>
			{
				EndFakeDefuseSearch(slot);
			}), (TimerFlags?)(TimerFlags)2);
		}
	}

	private void EndFakeDefuseSearch(int slot)
	{
		CancelFakeDefuseSuppression(slot);
		_fakeDefuseSearchingBots.Remove(slot);
		if (_fakeDefuseSearchingBots.Count == 0)
		{
			RestoreAllFovPatches();
		}
	}

	private void CancelFakeDefuseSuppression(int slot)
	{
		if (_fakeDefuseSuppressionIds.Remove(slot, out var value) && _botController != null)
		{
			BotControllerBridge.CancelUsercmdSuppression(_botController, slot, value);
		}
	}

	private void CancelAllFakeDefuseSuppressions()
	{
		foreach (int item in _fakeDefuseSuppressionIds.Keys.ToList())
		{
			CancelFakeDefuseSuppression(item);
		}
	}

	private void RestoreDefuseAfterElimination(int victimSlot, CsTeam victimTeam)
	{
		//IL_000e: Unknown result type (might be due to invalid IL or missing references)
		//IL_000f: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a7: Invalid comparison between I4 and Unknown
		if (_pendingDefuseRestore.Count == 0 || _defuseBombFunction == null || Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller").ToList().Any((CCSPlayerController p) =>
		{
			//IL_0027: Unknown result type (might be due to invalid IL or missing references)
			//IL_002c: Invalid comparison between I4 and Unknown
			return ((CEntityInstance)p).IsValid && p.Slot != victimSlot && p.PawnIsAlive && ((CBaseEntity)p).TeamNum == (int)victimTeam;
		}))
		{
			return;
		}
		foreach (int item in _pendingDefuseRestore.ToList())
		{
			CCSPlayerController playerFromSlot = Utilities.GetPlayerFromSlot(item);
			if (!((CEntityInstance)(object)playerFromSlot == (CEntityInstance)null) && ((CEntityInstance)playerFromSlot).IsValid && playerFromSlot.IsBot && playerFromSlot.PawnIsAlive && !playerFromSlot.HasBeenControlledByPlayerThisRound && ((CBaseEntity)playerFromSlot).TeamNum != (int)victimTeam)
			{
				_pendingDefuseRestore.Remove(item);
				ForceDefuseBombState(item);
			}
		}
	}

	private void ForceDefuseBombState(int slot)
	{
		Server.NextFrame((Action)(() =>
		{
			if (_defuseBombFunction != null)
			{
				CCSPlayerController playerFromSlot = Utilities.GetPlayerFromSlot(slot);
				if (!((CEntityInstance)(object)playerFromSlot == (CEntityInstance)null) && ((CEntityInstance)playerFromSlot).IsValid && playerFromSlot.IsBot && playerFromSlot.PawnIsAlive && !playerFromSlot.HasBeenControlledByPlayerThisRound)
				{
					CCSPlayerPawn val = playerFromSlot.PlayerPawn?.Value;
					if (!((CEntityInstance)(object)val == (CEntityInstance)null) && ((CEntityInstance)val).IsValid)
					{
						CCSBot bot = val.Bot;
						if (bot != null)
						{
							CancelFakeDefuseSuppression(slot);
							_fakeDefuseGuardUntil.Remove(((NativeObject)bot).Handle);
							_defuseBombFunction.Invoke((nint)((NativeObject)bot).Handle);
						}
					}
				}
			}
		}));
	}

	private void InstallDefuseBombHook()
	{
		try
		{
			string text = (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "48 8D 91 08 04 00 00 E9 ? ? ? ?" : "48 8D B7 00 04 00 00 E9 ? ? ? ?");
			_defuseBombFunction = new MemoryFunctionVoid<nint>(text);
			((BaseMemoryFunction)_defuseBombFunction).Hook((Func<DynamicHook, HookResult>)OnDefuseBombPre, (HookMode)0);
		}
		catch (Exception exception)
		{
			_defuseBombFunction = null;
			((BasePlugin)this).Logger.LogError(exception, "[Smarter-Bot] CCSBot::DefuseBomb hook unavailable; fake defuse disabled");
		}
	}

	private void UninstallDefuseBombHook()
	{
		if (_defuseBombFunction != null)
		{
			try
			{
				((BaseMemoryFunction)_defuseBombFunction).Unhook((Func<DynamicHook, HookResult>)OnDefuseBombPre, (HookMode)0);
			}
			catch (Exception exception)
			{
				((BasePlugin)this).Logger.LogWarning(exception, "[Smarter-Bot] Failed to remove CCSBot::DefuseBomb hook cleanly");
			}
			_defuseBombFunction = null;
		}
	}

	private HookResult OnDefuseBombPre(DynamicHook hook)
	{
		//IL_0041: Unknown result type (might be due to invalid IL or missing references)
		//IL_0011: Unknown result type (might be due to invalid IL or missing references)
		//IL_0046: Unknown result type (might be due to invalid IL or missing references)
		//IL_002d: Unknown result type (might be due to invalid IL or missing references)
		try
		{
			nint param = hook.GetParam<nint>(0);
			if (param == IntPtr.Zero)
			{
				return (HookResult)0;
			}
			if (_fakeDefuseGuardUntil.TryGetValue(param, out var value))
			{
				if (Server.CurrentTime < value)
				{
					return (HookResult)4;
				}
				_fakeDefuseGuardUntil.Remove(param);
			}
		}
		catch
		{
			return (HookResult)0;
		}
		return (HookResult)0;
	}

	private void ResetBombRoundState()
	{
		_fakeDefuseCooldown.Clear();
		_fakeDefuseGuardUntil.Clear();
		_fakeDefuseCounts.Clear();
		CancelAllFakeDefuseSuppressions();
		_fakeDefuseSearchingBots.Clear();
		_pendingDefuseRestore.Clear();
		RestoreAllFovPatches();
	}

	private HookResult OnWeaponFire(EventWeaponFire @event, GameEventInfo info)
	{
		CCSPlayerController userid = @event.Userid;
		if (!((CEntityInstance)(object)userid == (CEntityInstance)null) && ((CEntityInstance)userid).IsValid && userid.IsBot)
		{
			int index = (int)((CEntityInstance)userid).Index;
			CCSPlayerPawn val = userid.PlayerPawn?.Value;
			if (!((CEntityInstance)(object)val == (CEntityInstance)null) && ((CEntityInstance)val).IsValid)
			{
				CCSBot bot = val.Bot;
				if (bot != null)
				{
					_hasFiredThisAttack.Add(index);
					bool valueOrDefault = _cachedInAir.GetValueOrDefault(index, defaultValue: false);
					bool valueOrDefault2 = _cachedNearLadder.GetValueOrDefault(index, defaultValue: false);
					if (!valueOrDefault && !valueOrDefault2)
					{
						CPlayer_WeaponServices weaponServices = ((CBasePlayerPawn)val).WeaponServices;
						object obj;
						if (weaponServices == null)
						{
							obj = null;
						}
						else
						{
							CHandle<CBasePlayerWeapon> activeWeapon = weaponServices.ActiveWeapon;
							if (activeWeapon == null)
							{
								obj = null;
							}
							else
							{
								CBasePlayerWeapon value = activeWeapon.Value;
								obj = ((value != null) ? ((CEntityInstance)value).DesignerName : null);
							}
						}
						string text = (string)obj;
						if (text != null)
						{
							float x = ((CBaseEntity)val).AbsVelocity.X;
							float y = ((CBaseEntity)val).AbsVelocity.Y;
							float num = MathF.Sqrt(x * x + y * y);
							bool flag;
							switch (text)
							{
							case "weapon_cz75a":
							case "weapon_glock":
							case "weapon_mac10":
							case "weapon_p250":
							case "weapon_tec9":
							case "weapon_hkp2000":
							case "weapon_fiveseven":
							case "weapon_mp9":
								flag = true;
								break;
							default:
								flag = false;
								break;
							}
							if (flag)
							{
								if (num > 70f)
								{
									float num2 = 70f / num;
									((CBaseEntity)val).AbsVelocity.X = x * num2;
									((CBaseEntity)val).AbsVelocity.Y = y * num2;
								}
							}
							else
							{
								switch (text)
								{
								case "weapon_deagle":
								case "weapon_scar20":
								case "weapon_negev":
								case "weapon_sg556":
								case "weapon_ssg08":
								case "weapon_g3sg1":
								case "weapon_famas":
								case "weapon_aug":
								case "weapon_awp":
								case "weapon_m249":
								case "weapon_m4a1":
								case "weapon_ak47":
								case "weapon_usp_silencer":
								case "weapon_galilar":
								case "weapon_m4a1_silencer":
									flag = true;
									break;
								default:
									flag = false;
									break;
								}
								if (flag && num > 0f)
								{
									((CBaseEntity)val).AbsVelocity.X = 0f;
									((CBaseEntity)val).AbsVelocity.Y = 0f;
								}
							}
						}
					}
					if (!val.IsDefusing && bot.IsAttacking)
					{
						double num3 = 0.0;
						CPlayer_WeaponServices weaponServices2 = ((CBasePlayerPawn)val).WeaponServices;
						object obj2;
						if (weaponServices2 == null)
						{
							obj2 = null;
						}
						else
						{
							CHandle<CBasePlayerWeapon> activeWeapon2 = weaponServices2.ActiveWeapon;
							if (activeWeapon2 == null)
							{
								obj2 = null;
							}
							else
							{
								CBasePlayerWeapon value2 = activeWeapon2.Value;
								obj2 = ((value2 != null) ? ((CEntityInstance)value2).DesignerName : null);
							}
						}
						string text2 = (string)obj2;
						bool flag;
						switch (text2)
						{
						case "weapon_glock":
						case "weapon_hkp2000":
						case "weapon_p250":
						case "weapon_fiveseven":
							flag = true;
							goto IL_050e;
						default:
							flag = false;
							goto IL_050e;
						case null:
							break;
							IL_050e:
							if (flag)
							{
								num3 = 0.2;
								break;
							}
							if ((text2 == "weapon_usp_silencer" || text2 == "weapon_deagle") ? true : false)
							{
								num3 = 0.3;
								break;
							}
							switch (text2)
							{
							case "weapon_elite":
							case "weapon_tec9":
							case "weapon_cz75a":
							case "weapon_revolver":
							case "weapon_scar20":
							case "weapon_g3sg1":
								flag = true;
								break;
							default:
								flag = false;
								break;
							}
							if (flag)
							{
								num3 = 0.1;
								break;
							}
							switch (text2)
							{
							case "weapon_mac10":
							case "weapon_mp9":
							case "weapon_bizon":
								flag = true;
								break;
							default:
								flag = false;
								break;
							}
							if (flag)
							{
								num3 = 0.03;
								break;
							}
							switch (text2)
							{
							case "weapon_mp5sd":
							case "weapon_ssg08":
							case "weapon_ump45":
							case "weapon_awp":
							case "weapon_p90":
							case "weapon_mag7":
							case "weapon_nova":
							case "weapon_xm1014":
							case "weapon_sawedoff":
								flag = true;
								break;
							default:
								flag = false;
								break;
							}
							if (flag)
							{
								num3 = 0.05;
								break;
							}
							switch (text2)
							{
							case "weapon_m249":
							case "weapon_m4a1":
							case "weapon_ak47":
							case "weapon_famas":
							case "weapon_sg556":
							case "weapon_galilar":
							case "weapon_m4a1_silencer":
							case "weapon_aug":
								flag = true;
								break;
							default:
								flag = false;
								break;
							}
							if (flag)
							{
								num3 = 0.5;
							}
							else if (text2 == "weapon_negev")
							{
								num3 = 0.9;
							}
							break;
						}
						((CBot)bot).IsCrouching = _random.NextDouble() < num3;
						CountdownTimer sneakTimer = bot.SneakTimer;
						sneakTimer.Duration = 0f;
						sneakTimer.Timestamp = 0f;
						sneakTimer.Timescale = 1f;
						return (HookResult)0;
					}
					return (HookResult)0;
				}
				return (HookResult)0;
			}
			return (HookResult)0;
		}
		return (HookResult)0;
	}

	private bool UpdateBotCombat(CCSPlayerController player, CCSPlayerPawn pawn, CCSBot bot, int idx)
	{
		bool isAttacking = bot.IsAttacking;
		if (isAttacking && _hasFiredThisAttack.Remove(idx))
		{
			CPlayer_WeaponServices weaponServices = ((CBasePlayerPawn)pawn).WeaponServices;
			object obj;
			if (weaponServices == null)
			{
				obj = null;
			}
			else
			{
				CHandle<CBasePlayerWeapon> activeWeapon = weaponServices.ActiveWeapon;
				if (activeWeapon == null)
				{
					obj = null;
				}
				else
				{
					CBasePlayerWeapon value = activeWeapon.Value;
					obj = ((value != null) ? ((CEntityInstance)value).DesignerName : null);
				}
			}
			string text = (string)obj;
			if (text == "weapon_awp" || text == "weapon_ssg08")
			{
				_lastLateralDir.TryGetValue(idx, out var value2);
				if (value2 != 0f)
				{
					float x = pawn.EyeAngles.Y * (float)Math.PI / 180f;
					float num = 0f - MathF.Sin(x);
					float num2 = MathF.Cos(x);
					float num3 = num * (0f - value2) * 250f;
					float num4 = num2 * (0f - value2) * 250f;
					((CBaseEntity)pawn).AbsVelocity.X += num3;
					((CBaseEntity)pawn).AbsVelocity.Y += num4;
					ResetLookAroundForBot(player);
				}
			}
		}
		if (isAttacking)
		{
			bot.EyeAnglesUnderPathFinderControl = false;
			bot.InhibitLookAroundTimestamp = 0f;
		}
		if (bot.IsAimingAtEnemy && !isAttacking)
		{
			bot.IsAttacking = true;
		}
		if (_prevIsAttacking.TryGetValue(idx, out var value3) && value3 && !isAttacking)
		{
			((CBot)bot).IsCrouching = false;
		}
		_prevIsAttacking[idx] = isAttacking;
		if (!isAttacking)
		{
			_hasFiredThisAttack.Remove(idx);
			float x2 = pawn.EyeAngles.Y * (float)Math.PI / 180f;
			float num5 = 0f - MathF.Sin(x2);
			float num6 = MathF.Cos(x2);
			float num7 = ((CBaseEntity)pawn).AbsVelocity.X * num5 + ((CBaseEntity)pawn).AbsVelocity.Y * num6;
			if (MathF.Abs(num7) > 10f)
			{
				float value4 = ((num7 > 0f) ? 1f : (-1f));
				_lastLateralDir.GetValueOrDefault(idx);
				_lastLateralDir[idx] = value4;
			}
		}
		return isAttacking;
	}

	private void ResetCombatRoundState()
	{
		_lastLateralDir.Clear();
		_hasFiredThisAttack.Clear();
		_prevIsAttacking.Clear();
	}

	private void LoadCustomFov()
	{
		try
		{
			ApplyCustomFov(_fovOptions);
		}
		catch (Exception ex)
		{
			_customFovStatus = "unavailable: " + ex.Message;
			((BasePlugin)this).Logger.LogError(ex, "[Smarter-Bot] Custom FOV unavailable; native vision remains active");
		}
	}

	private void ApplyCustomFov(BotFovOptions options)
	{
		options.Validate();
		if (options.Enabled)
		{
			BotFovHooks customFov = _customFov;
			if (customFov != null && customFov.Failed)
			{
				throw new InvalidOperationException("Reload the plugin after a native FOV failure.");
			}
			if (_customFov == null)
			{
				_customFov = new BotFovHooks(GetCustomFovView, (Exception ex) =>
				{
					_customFovStatus = "failed: " + ex.Message;
					((BasePlugin)this).Logger.LogError(ex, "[Smarter-Bot] Custom FOV disabled; native vision resumes");
				});
			}
			RestoreAllFovPatches();
		}
		_fovOptions = options;
		_customFovStatus = (options.Enabled ? "custom" : "native");
		ClearFovObservers();
		if (!options.Enabled && _fakeDefuseSearchingBots.Count != 0)
		{
			ApplyFovPatches();
		}
	}

	private void ClearFovObservers()
	{
		_fovObservers.Clear();
		_fovFrames.Clear();
		_fovRosterTick = -1;
	}

	private BotViewFrustum? GetCustomFovView(nint bot)
	{
		if (!CustomFovActive || bot == 0)
		{
			return null;
		}
		if (_fovRosterTick != Server.TickCount)
		{
			ClearFovObservers();
			_fovRosterTick = Server.TickCount;
			foreach (CCSPlayerController player in Utilities.GetPlayers())
			{
				if (((CEntityInstance)player).IsValid && player.IsBot)
				{
					CCSPlayerPawn value = player.PlayerPawn.Value;
					CCSBot val = ((value != null) ? value.Bot : null);
					if (val != null)
					{
						_fovObservers[((NativeObject)val).Handle] = player;
					}
				}
			}
		}
		if (_fovObservers.TryGetValue(bot, out CCSPlayerController value2) && ((CEntityInstance)value2).IsValid && value2.IsBot && !((CBasePlayerController)value2).IsHLTV && !value2.HasBeenControlledByPlayerThisRound && !value2.ControllingBot)
		{
			CCSPlayerPawn value3 = value2.PlayerPawn.Value;
			if (value3 != null && ((CEntityInstance)value3).IsValid && ((CBaseEntity)value3).Health > 0 && ((CBaseEntity)value3).LifeState == 0)
			{
				CCSBot bot2 = value3.Bot;
				if (bot2 != null && ((NativeObject)bot2).Handle == (IntPtr)bot)
				{
					Vector absOrigin = ((CBaseEntity)value3).AbsOrigin;
					if (absOrigin != null && (_botController == null || !BotControllerBridge.IsViewControlled(_botController, value2.Slot)))
					{
						CNetworkViewOffsetVector viewOffset = ((CBaseModelEntity)value3).ViewOffset;
						System.Numerics.Vector3 vector = new System.Numerics.Vector3(absOrigin.X + viewOffset.X, absOrigin.Y + viewOffset.Y, absOrigin.Z + viewOffset.Z);
						QAngle v_angle = ((CBasePlayerPawn)value3).V_angle;
						System.Numerics.Vector3 vector2 = new System.Numerics.Vector3(v_angle.X, v_angle.Y, v_angle.Z);
						if (!BotViewFrustum.Finite(vector) || !BotViewFrustum.Finite(vector2))
						{
							return null;
						}
						uint raw = ((CHandle<CEntityInstance>)(object)((NativeEntity)value3).EntityHandle).Raw;
						if (_fovFrames.TryGetValue(bot, out (uint, System.Numerics.Vector3, System.Numerics.Vector3, BotViewFrustum) value4) && value4.Item1 == raw && value4.Item2 == vector && value4.Item3 == vector2)
						{
							return value4.Item4;
						}
						BotViewFrustum botViewFrustum = BotViewFrustum.Build(vector, vector2, _fovOptions);
						_fovFrames[bot] = (raw, vector, vector2, botViewFrustum);
						return botViewFrustum;
					}
				}
			}
		}
		return null;
	}

	[ConsoleCommand("css_bot_fov", "css_bot_fov status | native | <horizontal> [vertical]")]
	[RequiresPermissions(new string[] { "@css/root" })]
	public void OnBotFovCommand(CCSPlayerController? caller, CommandInfo command)
	{
		string text = ((command.ArgCount > 1) ? command.GetArg(1) : "status");
		try
		{
			if (text.Equals("native", StringComparison.OrdinalIgnoreCase) || text.Equals("status", StringComparison.OrdinalIgnoreCase))
			{
				if (command.ArgCount > 2)
				{
					throw new ArgumentException("Usage: css_bot_fov status | native | <horizontal> [vertical]");
				}
				if (text.Equals("native", StringComparison.OrdinalIgnoreCase))
				{
					ApplyCustomFov(_fovOptions with
					{
						Enabled = false
					});
				}
			}
			else
			{
				if (command.ArgCount > 3)
				{
					throw new ArgumentException("Usage: css_bot_fov status | native | <horizontal> [vertical]");
				}
				ApplyCustomFov(BotFovOptions.FromDegrees(float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture), (command.ArgCount == 3) ? new float?(float.Parse(command.GetArg(2), NumberStyles.Float, CultureInfo.InvariantCulture)) : ((float?)null)));
			}
			BotFovOptions fovOptions = _fovOptions;
			command.ReplyToCommand(FormattableString.Invariant($"[Smarter-Bot] FOV={_customFovStatus}; active={CustomFovActive}; horizontal={fovOptions.HorizontalDegrees:0.###}; vertical={fovOptions.VerticalDegrees:0.###}; points={_customFov?.PointsTested ?? 0}; rejected={_customFov?.PointsRejected ?? 0}"));
		}
		catch (Exception ex)
		{
			command.ReplyToCommand("[Smarter-Bot] " + ex.Message);
		}
	}


	private void InitializeFovPatches()
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && !RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
		{
			_fovPatchesAvailable = false;
		}
		else
		{
			_fovPatchesAvailable = true;
		}
	}

	private void ApplyFovPatches()
	{
		if (CustomFovActive || !_fovPatchesAvailable || _appliedFovPatches.Count > 0)
		{
			return;
		}
		FovPatchDefinition[] array = (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? LinuxFovPatches : FovPatches);
		foreach (FovPatchDefinition patch in array)
		{
			if (!TryApplyFovPatch(patch))
			{
				RestoreAllFovPatches();
				_fovPatchesAvailable = false;
				break;
			}
		}
	}

	private bool TryApplyFovPatch(FovPatchDefinition patch)
	{
		try
		{
			nint num = NativeAPI.FindSignature(Addresses.ServerPath, patch.Signature);
			if (num == IntPtr.Zero)
			{
				return false;
			}
			nint num2 = num + patch.Offset;
			byte[] array = new byte[patch.Replacement.Length];
			Marshal.Copy(num2, array, 0, array.Length);
			if (!array.SequenceEqual(patch.Expected))
			{
				return false;
			}
			if (!WriteExecutableMemory(num2, patch.Replacement))
			{
				WriteExecutableMemory(num2, array);
				return false;
			}
			_appliedFovPatches.Add(new AppliedFovPatch(patch.Name, num2, array));
			return true;
		}
		catch
		{
			return false;
		}
	}

	private void RestoreAllFovPatches()
	{
		for (int num = _appliedFovPatches.Count - 1; num >= 0; num--)
		{
			AppliedFovPatch appliedFovPatch = _appliedFovPatches[num];
			WriteExecutableMemory(appliedFovPatch.Address, appliedFovPatch.Original);
		}
		_appliedFovPatches.Clear();
	}

	private static bool WriteLinuxExecutableMemory(nint address, byte[] bytes)
	{
		if (bytes.Length == 0)
		{
			return true;
		}
		long num = Environment.SystemPageSize;
		if (num <= 0 || (num & (num - 1)) != 0L)
		{
			return false;
		}
		long num2 = ((IntPtr)address).ToInt64();
		long num3;
		try
		{
			num3 = checked((num2 + bytes.Length + num - 1) & ~(num - 1));
		}
		catch (OverflowException)
		{
			return false;
		}
		long num4 = num2 & ~(num - 1);
		if (num3 <= num4)
		{
			return false;
		}
		nint address2 = (nint)num4;
		nuint size = (nuint)(num3 - num4);
		if (MProtect(address2, size, 7) != 0)
		{
			return false;
		}
		bool result = false;
		try
		{
			Marshal.Copy(bytes, 0, address, bytes.Length);
			result = true;
		}
		finally
		{
			if (MProtect(address2, size, 5) != 0)
			{
				result = false;
			}
		}
		return result;
	}

	private static bool WriteExecutableMemory(nint address, byte[] bytes)
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
		{
			return WriteLinuxExecutableMemory(address, bytes);
		}
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			return false;
		}
		if (!VirtualProtect(address, (nuint)bytes.Length, 64u, out var oldProtect))
		{
			return false;
		}
		bool result = false;
		try
		{
			Marshal.Copy(bytes, 0, address, bytes.Length);
			result = FlushInstructionCache(GetCurrentProcess(), address, (nuint)bytes.Length);
		}
		finally
		{
			if (!VirtualProtect(address, (nuint)bytes.Length, oldProtect, out var _))
			{
				result = false;
			}
		}
		return result;
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool VirtualProtect(nint address, nuint size, uint newProtect, out uint oldProtect);

	[DllImport("libc.so.6", EntryPoint = "mprotect", SetLastError = true)]
	private static extern int MProtect(nint address, nuint size, int protection);

	[DllImport("kernel32.dll")]
	private static extern nint GetCurrentProcess();

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool FlushInstructionCache(nint process, nint baseAddress, nuint size);

	public override void Load(bool hotReload)
	{
		//IL_0020: Unknown result type (might be due to invalid IL or missing references)
		//IL_002a: Expected Obj, but got Unknown
		//IL_013c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0146: Expected Obj, but got Unknown
		InstallDefuseBombHook();
		InstallBotBlindHook();
		InitializeFovPatches();
		LoadCustomFov();
		((BasePlugin)this).RegisterListener<OnMapEnd>((OnMapEnd)ClearFovObservers);
		((BasePlugin)this).RegisterEventHandler<EventRoundStart>((GameEventHandler<EventRoundStart>)OnRoundStart, (HookMode)1);
		((BasePlugin)this).RegisterEventHandler<EventPlayerHurt>((GameEventHandler<EventPlayerHurt>)OnPlayerHurt, (HookMode)1);
		((BasePlugin)this).RegisterEventHandler<EventPlayerSpawn>((GameEventHandler<EventPlayerSpawn>)OnPlayerSpawn, (HookMode)1);
		((BasePlugin)this).RegisterEventHandler<EventRoundFreezeEnd>((GameEventHandler<EventRoundFreezeEnd>)OnRoundFreezeEnd, (HookMode)1);
		((BasePlugin)this).RegisterEventHandler<EventPlayerBlind>((GameEventHandler<EventPlayerBlind>)OnPlayerBlind, (HookMode)1);
		((BasePlugin)this).RegisterEventHandler<EventBombPlanted>((GameEventHandler<EventBombPlanted>)OnBombPlanted, (HookMode)1);
		((BasePlugin)this).RegisterEventHandler<EventBombBegindefuse>((GameEventHandler<EventBombBegindefuse>)OnBombBeginDefuse, (HookMode)1);
		((BasePlugin)this).RegisterEventHandler<EventBombAbortdefuse>((GameEventHandler<EventBombAbortdefuse>)OnBombAbortDefuse, (HookMode)1);
		((BasePlugin)this).RegisterEventHandler<EventBombDefused>((GameEventHandler<EventBombDefused>)OnBombDefused, (HookMode)1);
		((BasePlugin)this).RegisterEventHandler<EventBombExploded>((GameEventHandler<EventBombExploded>)OnBombExploded, (HookMode)1);
		((BasePlugin)this).RegisterEventHandler<EventDoorOpen>((GameEventHandler<EventDoorOpen>)OnDoorOpen, (HookMode)1);
		((BasePlugin)this).RegisterEventHandler<EventDoorClose>((GameEventHandler<EventDoorClose>)OnDoorClose, (HookMode)1);
		((BasePlugin)this).RegisterEventHandler<EventWeaponFire>((GameEventHandler<EventWeaponFire>)OnWeaponFire, (HookMode)1);
		((BasePlugin)this).RegisterEventHandler<EventPlayerDeath>((GameEventHandler<EventPlayerDeath>)OnPlayerDeath, (HookMode)1);
		((BasePlugin)this).RegisterListener<OnTick>((OnTick)OnTick);
		_gunReequipTimer = ((BasePlugin)this).AddTimer(1f, (Action)ReequipGunForActiveBots, (TimerFlags?)(TimerFlags)1);
		StartRushAntennaPublisher();
	}

	public override void OnAllPluginsLoaded(bool hotReload)
	{
		//IL_001c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0026: Expected Obj, but got Unknown
		_scratchEye = new Vector((float?)null, (float?)null, (float?)null);
		try
		{
			_botController = BotControllerBridge.TryGet();
		}
		catch
		{
			_botController = null;
		}
		if (_botController == null)
		{
			Console.WriteLine("[Smarter-Bot] BotController API not available");
		}
	}

	public override void Unload(bool hotReload)
	{
		_customFov?.Dispose();
		_customFov = null;
		ClearFovObservers();
		CancelAllFakeDefuseSuppressions();
		UninstallBotBlindHook();
		UninstallDefuseBombHook();
		RestoreAllFovPatches();
		ReleaseKnifeLocks();
		ClearReveals();
		Timer? defuseRevealTimer = _defuseRevealTimer;
		if (defuseRevealTimer != null)
		{
			defuseRevealTimer.Kill();
		}
		Timer? gunReequipTimer = _gunReequipTimer;
		if (gunReequipTimer != null)
		{
			gunReequipTimer.Kill();
		}
		StopRushAntennaPublisher();
	}

	[GameEventHandler]
	public HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
	{
		CCSPlayerController player = @event.Userid;
		if (!((CEntityInstance)(object)player == (CEntityInstance)null) && ((CEntityInstance)player).IsValid && player.IsBot)
		{
			Server.NextFrame((Action)(() =>
			{
				if (!((CEntityInstance)(object)player == (CEntityInstance)null) && ((CEntityInstance)player).IsValid)
				{
					ApplyBotState(player);
				}
			}));
			return (HookResult)0;
		}
		return (HookResult)0;
	}

	[GameEventHandler]
	public HookResult OnRoundFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
	{
		_isFreezeTime = false;
		foreach (CCSPlayerController item in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
		{
			if (((CEntityInstance)item).IsValid && item.IsBot)
			{
				ApplyBotState(item);
			}
		}
		return (HookResult)0;
	}

	private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
	{
		ClearFovObservers();
		ReleaseKnifeLocks();
		StopDefuseReveal();
		ClearReveals();
		_eliminationHandled = false;
		_isFreezeTime = true;
		ResetMovementRoundState();
		ResetCombatRoundState();
		ResetWeaponsRoundState();
		ResetBombRoundState();
		ResetFlashbangRoundState();
		return (HookResult)0;
	}

	private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
	{
		//IL_0035: Unknown result type (might be due to invalid IL or missing references)
		//IL_0036: Unknown result type (might be due to invalid IL or missing references)
		//IL_0038: Invalid comparison between Unknown and I4
		//IL_004d: Unknown result type (might be due to invalid IL or missing references)
		//IL_005a: Unknown result type (might be due to invalid IL or missing references)
		//IL_003a: Unknown result type (might be due to invalid IL or missing references)
		//IL_003c: Invalid comparison between Unknown and I4
		if (!IsDeathmatch())
		{
			if (_botController != null)
			{
				CCSPlayerController userid = @event.Userid;
				if (!((CEntityInstance)(object)userid == (CEntityInstance)null) && ((CEntityInstance)userid).IsValid)
				{
					CsTeam val = (CsTeam)((CBaseEntity)userid).TeamNum;
					if ((int)val != 2 && (int)val != 3)
					{
						return (HookResult)0;
					}
					bool eliminationHandled = _eliminationHandled;
					HandleTeamElimination(userid.Slot, val);
					RestoreDefuseAfterElimination(userid.Slot, val);
					if (eliminationHandled || !_eliminationHandled)
					{
						MaybeInspectOnKill(@event.Attacker);
					}
					return (HookResult)0;
				}
				return (HookResult)0;
			}
			return (HookResult)0;
		}
		return (HookResult)0;
	}

	private static bool IsDeathmatch()
	{
		ConVar val = ConVar.Find("game_type");
		ConVar val2 = ConVar.Find("game_mode");
		if (val != null && val.GetPrimitiveValue<int>() == 1)
		{
			if (val2 == null)
			{
				return false;
			}
			return val2.GetPrimitiveValue<int>() == 2;
		}
		return false;
	}

	private HookResult OnDoorOpen(EventDoorOpen @event, GameEventInfo info)
	{
		CCSPlayerController userid = @event.Userid;
		if (!((CEntityInstance)(object)userid == (CEntityInstance)null) && ((CEntityInstance)userid).IsValid && userid.IsBot)
		{
			int index = (int)((CEntityInstance)userid).Index;
			_doorEventCooldown[index] = Server.CurrentTime + 1f;
			return (HookResult)0;
		}
		return (HookResult)0;
	}

	private HookResult OnDoorClose(EventDoorClose @event, GameEventInfo info)
	{
		CCSPlayerController userid = @event.Userid;
		if (!((CEntityInstance)(object)userid == (CEntityInstance)null) && ((CEntityInstance)userid).IsValid && userid.IsBot)
		{
			int index = (int)((CEntityInstance)userid).Index;
			_doorEventCooldown[index] = Server.CurrentTime + 1f;
			return (HookResult)0;
		}
		return (HookResult)0;
	}

	private bool UpdateBotMovement(CCSPlayerPawn pawn, CCSBot bot, int idx, bool curIsAttacking, bool inDoorCooldown)
	{
		CPlayer_MovementServices movementServices = ((CBasePlayerPawn)pawn).MovementServices;
		CPlayer_MovementServices obj = ((movementServices is CCSPlayer_MovementServices) ? movementServices : null);
		Vector val = ((obj != null) ? ((CCSPlayer_MovementServices)obj).LadderNormal : null);
		bool flag = (int)((CBaseEntity)pawn).MoveType == 9 || (val != null && (val.X != 0f || val.Y != 0f || val.Z != 0f));
		if (flag)
		{
			_ladderExitTime[idx] = Server.CurrentTime;
		}
		bool flag2 = !flag && (!_ladderExitTime.TryGetValue(idx, out var value) || !(Server.CurrentTime - value < 5f)) && (((CBaseEntity)pawn).GroundEntity == null || !((CBaseEntity)pawn).GroundEntity.IsValid);
		_prevInAir.TryGetValue(idx, out var value2);
		if (inDoorCooldown)
		{
			_prevInAir[idx] = flag2;
			return false;
		}
		float x = pawn.EyeAngles.Y * (float)Math.PI / 180f;
		float num = MathF.Cos(x);
		float num2 = MathF.Sin(x);
		float num3 = ((CBaseEntity)pawn).AbsVelocity.X * num + ((CBaseEntity)pawn).AbsVelocity.Y * num2;
		if (num3 >= 20f || num3 <= -20f)
		{
			_lastForwardDir[idx] = ((num3 > 0f) ? 1f : (-1f));
		}
		if (flag2)
		{
			if (!pawn.IsDefusing)
			{
				((CBot)bot).IsCrouching = true;
			}
			if (!curIsAttacking)
			{
				float num4;
				if (num3 <= -20f)
				{
					num4 = -215f;
				}
				else if (num3 >= 20f)
				{
					num4 = 215f;
				}
				else
				{
					num4 = (((_lastForwardDir.TryGetValue(idx, out var value3) ? value3 : 1f) > 0f) ? 215f : (-215f));
				}
				float num5 = num4 - num3;
				if (num4 > 0f)
				{
					if (num5 > 0f)
					{
						float num6 = num5 * 12f * (1f / 64f);
						((CBaseEntity)pawn).AbsVelocity.X += num * num6;
						((CBaseEntity)pawn).AbsVelocity.Y += num2 * num6;
					}
				}
				else if (num5 < 0f)
				{
					float num7 = num5 * 12f * (1f / 64f);
					((CBaseEntity)pawn).AbsVelocity.X += num * num7;
					((CBaseEntity)pawn).AbsVelocity.Y += num2 * num7;
				}
			}
		}
		if (value2 && !flag2)
		{
			((CBot)bot).IsCrouching = false;
		}
		_prevInAir[idx] = flag2;
		_cachedInAir[idx] = flag2;
		_cachedNearLadder[idx] = flag;
		return true;
	}

	private void RecoverStuckBot(CCSPlayerController player, CCSPlayerPawn pawn, CCSBot bot, int idx, float now, bool curIsAttacking)
	{
		//IL_00d3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00dd: Expected Obj, but got Unknown
		//IL_0157: Unknown result type (might be due to invalid IL or missing references)
		//IL_0161: Expected Obj, but got Unknown
		//IL_0309: Unknown result type (might be due to invalid IL or missing references)
		//IL_0313: Expected Obj, but got Unknown
		if (bot.IsStuck)
		{
			((CBot)bot).IsRunning = true;
			((CBot)bot).JumpTimestamp = 0f;
			CountdownTimer stuckJumpTimer = bot.StuckJumpTimer;
			stuckJumpTimer.Duration = 0f;
			stuckJumpTimer.Timestamp = Server.CurrentTime;
			stuckJumpTimer.Timescale = 1f;
			float num = MathF.Sqrt(((CBaseEntity)pawn).AbsVelocity.X * ((CBaseEntity)pawn).AbsVelocity.X + ((CBaseEntity)pawn).AbsVelocity.Y * ((CBaseEntity)pawn).AbsVelocity.Y);
			Vector absOrigin = ((CBaseEntity)pawn).AbsOrigin;
			if (!_stuckStartTime.ContainsKey(idx))
			{
				_stuckStartTime[idx] = now;
				_stuckStartPos[idx] = new Vector((float?)absOrigin.X, (float?)absOrigin.Y, (float?)absOrigin.Z);
				_stuckJumpDone[idx] = false;
				_stuckMaxSpeed[idx] = 0f;
			}
			if (num > _stuckMaxSpeed.GetValueOrDefault(idx))
			{
				_stuckMaxSpeed[idx] = num;
			}
			float num2 = now - _stuckStartTime.GetValueOrDefault(idx);
			Vector valueOrDefault = _stuckStartPos.GetValueOrDefault(idx, new Vector((float?)absOrigin.X, (float?)absOrigin.Y, (float?)absOrigin.Z));
			float num3 = MathF.Sqrt(MathF.Pow(absOrigin.X - valueOrDefault.X, 2f) + MathF.Pow(absOrigin.Y - valueOrDefault.Y, 2f));
			float valueOrDefault2 = _stuckMaxSpeed.GetValueOrDefault(idx);
			bool flag = num2 >= 1f && valueOrDefault2 <= 10f;
			bool flag2 = num2 >= 3f && valueOrDefault2 > 10f && num3 < 75f;
			if ((flag | flag2) && !_stuckJumpDone.GetValueOrDefault(idx))
			{
				((CBot)bot).IsCrouching = false;
				_stuckJumpDone[idx] = true;
				int valueOrDefault3 = _stuckJumpCount.GetValueOrDefault(idx);
				_stuckJumpCount[idx] = valueOrDefault3 + 1;
				float num4 = ((valueOrDefault3 % 2 == 0) ? 1f : (-1f));
				float num5 = (float)Math.PI / 6f * num4;
				float x = pawn.EyeAngles.Y * (float)Math.PI / 180f + (float)Math.PI + num5;
				((CBaseEntity)pawn).AbsVelocity.X = MathF.Cos(x) * 100f;
				((CBaseEntity)pawn).AbsVelocity.Y = MathF.Sin(x) * 100f;
				CountdownTimer repathTimer = bot.RepathTimer;
				repathTimer.Duration = 0f;
				repathTimer.Timestamp = Server.CurrentTime;
				repathTimer.Timescale = 1f;
				_stuckStartTime[idx] = now;
				_stuckStartPos[idx] = new Vector((float?)absOrigin.X, (float?)absOrigin.Y, (float?)absOrigin.Z);
				_stuckMaxSpeed[idx] = 0f;
			}
			return;
		}
		_stuckStartTime.Remove(idx);
		_stuckStartPos.Remove(idx);
		_stuckJumpDone.Remove(idx);
		_stuckMaxSpeed.Remove(idx);
		if (MathF.Sqrt(((CBaseEntity)pawn).AbsVelocity.X * ((CBaseEntity)pawn).AbsVelocity.X + ((CBaseEntity)pawn).AbsVelocity.Y * ((CBaseEntity)pawn).AbsVelocity.Y) < 5f)
		{
			if (!_idleStartTime.ContainsKey(idx))
			{
				_idleStartTime[idx] = now;
			}
			float num6 = now - _idleStartTime[idx];
			float valueOrDefault4 = _lastRepathTime.GetValueOrDefault(idx, -999f);
			if (num6 >= 5f && now - valueOrDefault4 >= 5f && !curIsAttacking && !pawn.IsDefusing)
			{
				((CBot)bot).IsCrouching = false;
				_lastRepathTime[idx] = now;
				CountdownTimer repathTimer2 = bot.RepathTimer;
				repathTimer2.Duration = 0f;
				repathTimer2.Timestamp = Server.CurrentTime;
				repathTimer2.Timescale = 1f;
				ResetLookAroundForBot(player);
			}
		}
		else
		{
			_idleStartTime.Remove(idx);
		}
	}

	private static void ApplyMapMovementFix(CCSPlayerPawn pawn, CCSBot bot)
	{
		if (((CBaseEntity)pawn).AbsOrigin != null)
		{
			Vector absOrigin = ((CBaseEntity)pawn).AbsOrigin;
			bool flag = string.Equals(Server.MapName, "de_inferno", StringComparison.OrdinalIgnoreCase);
			float num = absOrigin.X - 285f;
			float num2 = absOrigin.Y - 450f;
			float num3 = MathF.Sqrt(num * num + num2 * num2);
			if (flag && num3 < 50f)
			{
				CountdownTimer repathTimer = bot.RepathTimer;
				repathTimer.Duration = 0f;
				repathTimer.Timestamp = Server.CurrentTime;
				repathTimer.Timescale = 1f;
			}
		}
	}

	private void ResetMovementRoundState()
	{
		_prevInAir.Clear();
		_lastForwardDir.Clear();
		_ladderExitTime.Clear();
		_doorEventCooldown.Clear();
		_stuckStartTime.Clear();
		_stuckStartPos.Clear();
		_stuckJumpDone.Clear();
		_stuckJumpCount.Clear();
		_stuckMaxSpeed.Clear();
		_idleStartTime.Clear();
		_lastRepathTime.Clear();
		_cachedInAir.Clear();
		_cachedNearLadder.Clear();
	}

	[ConsoleCommand("css_rush_antenna", "Show the current Rush antenna owner (read-only).")]
	public void OnRushAntennaCommand(CCSPlayerController? caller, CommandInfo command)
	{
		int owner;
		Vector origin;
		string message;
		if (command.ArgCount != 1)
		{
			command.ReplyToCommand("[Smarter-Bot] Usage: css_rush_antenna");
		}
		else if (TryReadRushAntenna(out owner, out origin, out message) == RushAntennaStatus.Read)
		{
			command.ReplyToCommand("[Smarter-Bot] Rush antenna: " + message + ".");
		}
		else
		{
			command.ReplyToCommand("[Smarter-Bot] Rush antenna: unavailable (" + message + ").");
		}
	}

	private static RushAntennaStatus TryReadRushAntenna(out int owner, out Vector? origin, out string message)
	{
		//IL_01a7: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ad: Expected Obj, but got Unknown
		owner = 0;
		origin = null;
		if (!string.Equals(Server.MapName, "rush_001", StringComparison.OrdinalIgnoreCase))
		{
			message = "requires rush_001";
			return RushAntennaStatus.Unavailable;
		}
		try
		{
			CBaseEntity val = null;
			CBaseEntity val2 = null;
			int num = 0;
			foreach (CBaseEntity item in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("radar_element"))
			{
				if (((CEntityInstance)item).IsValid)
				{
					CEntityIdentity entity = ((CEntityInstance)item).Entity;
					string text = ((entity != null) ? entity.Name : null);
					if (text == "ant.base.radar")
					{
						val = item;
						break;
					}
					if (text != null && text.EndsWith("ant.base.radar", StringComparison.Ordinal))
					{
						val2 = item;
						num++;
					}
				}
			}
			if ((CEntityInstance)(object)val == (CEntityInstance)null && num > 1)
			{
				message = "multiple antenna markers";
				return RushAntennaStatus.Unavailable;
			}
			if (val == null)
			{
				val = val2;
			}
			if (val == null || !((CEntityInstance)val).IsValid)
			{
				message = "antenna marker not found";
				return RushAntennaStatus.Unavailable;
			}
			uint schemaValue = Schema.GetSchemaValue<uint>(((NativeEntity)val).Handle, "CCSRadarElement", "m_nElementColor");
			switch (schemaValue)
			{
			case 1u:
				owner = 0;
				message = "neutral (team=0)";
				break;
			case 3u:
				owner = 3;
				message = "CT (team=3)";
				break;
			case 4u:
				owner = 2;
				message = "T (team=2)";
				break;
			default:
				message = $"unknown (radar color={schemaValue})";
				return RushAntennaStatus.Read;
			}
			Vector absOrigin = val.AbsOrigin;
			if (absOrigin != null)
			{
				origin = new Vector((float?)absOrigin.X, (float?)absOrigin.Y, (float?)absOrigin.Z);
			}
			return RushAntennaStatus.Read;
		}
		catch (Exception)
		{
			message = "could not read map state";
			return RushAntennaStatus.Unavailable;
		}
	}

	private static void PublishRushAntennaOwner()
	{
		if (!string.Equals(Server.MapName, "rush_001", StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		LockRushRoundTime();
		bool flag = TryReadRushAntenna(out int owner, out Vector origin, out string _) == RushAntennaStatus.Read && (owner == 2 || owner == 3);
		try
		{
			if (!flag)
			{
				RemoveRushAntennaMarkers();
				return;
			}
			CBaseEntity val = null;
			foreach (CBaseEntity item in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("info_target"))
			{
				if (((CEntityInstance)item).IsValid && item.TeamNum != 0)
				{
					if ((CEntityInstance)(object)val == (CEntityInstance)null)
					{
						val = item;
					}
					else
					{
						((CEntityInstance)item).Remove();
					}
				}
			}
			if ((CEntityInstance)(object)val == (CEntityInstance)null)
			{
				val = Utilities.CreateEntityByName<CBaseEntity>("info_target");
				if ((CEntityInstance)(object)val == (CEntityInstance)null)
				{
					return;
				}
				val.DispatchSpawn();
				if (origin != null)
				{
					val.Teleport(origin, (QAngle)null, (Vector)null);
				}
			}
			if (val.TeamNum != owner)
			{
				val.TeamNum = (byte)owner;
			}
		}
		catch (Exception)
		{
		}
	}

	private static void LockRushRoundTime()
	{
		try
		{
			ConVar val = ConVar.Find("mp_roundtime");
			if (val != null && Math.Abs(val.GetPrimitiveValue<float>() - 0.75f) > 0.0001f)
			{
				val.SetValue<float>(0.75f);
			}
		}
		catch (Exception)
		{
		}
	}

	private static void RemoveRushAntennaMarkers()
	{
		foreach (CBaseEntity item in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("info_target"))
		{
			if (((CEntityInstance)item).IsValid && item.TeamNum != 0)
			{
				((CEntityInstance)item).Remove();
			}
		}
	}

	private void StartRushAntennaPublisher()
	{
		_rushAntennaTimer = ((BasePlugin)this).AddTimer(0.1f, (Action)PublishRushAntennaOwner, (TimerFlags?)(TimerFlags)1);
	}

	private void StopRushAntennaPublisher()
	{
		Timer? rushAntennaTimer = _rushAntennaTimer;
		if (rushAntennaTimer != null)
		{
			rushAntennaTimer.Kill();
		}
		_rushAntennaTimer = null;
		if (!string.Equals(Server.MapName, "rush_001", StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		try
		{
			RemoveRushAntennaMarkers();
		}
		catch (Exception)
		{
		}
	}

	private HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo _)
	{
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_0066: Unknown result type (might be due to invalid IL or missing references)
		//IL_004b: Unknown result type (might be due to invalid IL or missing references)
		try
		{
			CCSPlayerController userid = @event.Userid;
			if ((CEntityInstance)(object)userid == (CEntityInstance)null || !((CEntityInstance)userid).IsValid || !userid.IsBot)
			{
				return (HookResult)0;
			}
			CCSPlayerController attacker = @event.Attacker;
			if ((CEntityInstance)(object)attacker == (CEntityInstance)null || !((CEntityInstance)attacker).IsValid || attacker.Slot == userid.Slot)
			{
				return (HookResult)0;
			}
			RevealThroughSmoke(attacker.Slot, 0.8f);
		}
		catch
		{
		}
		return (HookResult)0;
	}

	private void RevealThroughSmoke(int slot, float seconds)
	{
		if (slot < 0)
		{
			return;
		}
		float num = Server.CurrentTime + seconds;
		if (_revealUntil.TryGetValue(slot, out var value))
		{
			if (num > value)
			{
				_revealUntil[slot] = num;
			}
		}
		else
		{
			_revealUntil[slot] = num;
			Server.ExecuteCommand($"bv_reveal add {slot}");
		}
	}

	private void EndReveal(int slot)
	{
		if (_revealUntil.Remove(slot))
		{
			Server.ExecuteCommand($"bv_reveal remove {slot}");
		}
	}

	private void ExpireReveals(float now)
	{
		if (_revealUntil.Count == 0)
		{
			return;
		}
		List<int> list = null;
		foreach (KeyValuePair<int, float> item in _revealUntil)
		{
			if (!(now < item.Value))
			{
				(list ?? (list = new List<int>())).Add(item.Key);
			}
		}
		if (list == null)
		{
			return;
		}
		foreach (int item2 in list)
		{
			EndReveal(item2);
		}
	}

	private void ClearReveals()
	{
		_revealUntil.Clear();
		Server.ExecuteCommand("bv_reveal clear");
	}

	private void StopDefuseReveal()
	{
		_isBombBeingDefused = false;
		Timer? defuseRevealTimer = _defuseRevealTimer;
		if (defuseRevealTimer != null)
		{
			defuseRevealTimer.Kill();
		}
		_defuseRevealTimer = null;
		if (_defuserSlot >= 0 && _revealUntil.TryGetValue(_defuserSlot, out var value) && value <= _defuseRevealUntil)
		{
			EndReveal(_defuserSlot);
		}
		_defuserSlot = -1;
		_defuseRevealUntil = 0f;
	}

	private void StartDefuseRevealCycle()
	{
		if (_defuseRevealTimer != null)
		{
			return;
		}
		_defuseRevealTimer = ((BasePlugin)this).AddTimer(3.5f, (Action)(() =>
		{
			_defuseRevealTimer = null;
			if (_isBombBeingDefused)
			{
				_defuseRevealUntil = Server.CurrentTime + 1.5f;
				RevealThroughSmoke(_defuserSlot, 1.5f);
				((BasePlugin)this).AddTimer(1.5f, (Action)(() =>
				{
					if (_isBombBeingDefused)
					{
						StartDefuseRevealCycle();
					}
				}), (TimerFlags?)null);
			}
		}), (TimerFlags?)null);
	}

	private void OnTick()
	{
		ProcessFlashbangAvoidance();
		ExpireReveals(Server.CurrentTime);
		foreach (CCSPlayerController item in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
		{
			if (!((CEntityInstance)item).IsValid || !item.IsBot)
			{
				continue;
			}
			CCSPlayerPawn value = item.PlayerPawn.Value;
			if ((CEntityInstance)(object)value == (CEntityInstance)null || !((CEntityInstance)value).IsValid)
			{
				continue;
			}
			CCSBot bot = value.Bot;
			if (bot != null && !item.HasBeenControlledByPlayerThisRound)
			{
				int index = (int)((CEntityInstance)item).Index;
				float currentTime = Server.CurrentTime;
				InterruptReload(item, value, bot, currentTime);
				bool inDoorCooldown = _doorEventCooldown.TryGetValue(index, out var value2) && currentTime < value2;
				UpdateBotBehavior(bot, currentTime);
				bool curIsAttacking = UpdateBotCombat(item, value, bot, index);
				if (UpdateBotMovement(value, bot, index, curIsAttacking, inDoorCooldown))
				{
					RecoverStuckBot(item, value, bot, index, currentTime, curIsAttacking);
					ApplyMapMovementFix(value, bot);
				}
			}
		}
	}

	private void MaybeInspectOnKill(CCSPlayerController? attacker)
	{
		if (_botController != null && !((CEntityInstance)(object)attacker == (CEntityInstance)null) && ((CEntityInstance)attacker).IsValid && attacker.IsBot && attacker.PawnIsAlive && !attacker.HasBeenControlledByPlayerThisRound && !(_random.NextDouble() >= 0.1))
		{
			QueueInspectInjection(attacker.Slot);
		}
	}

	private void HandleTeamElimination(int victimSlot, CsTeam victimTeam)
	{
		//IL_000e: Unknown result type (might be due to invalid IL or missing references)
		//IL_000f: Unknown result type (might be due to invalid IL or missing references)
		//IL_006b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0071: Invalid comparison between Unknown and I4
		//IL_0077: Unknown result type (might be due to invalid IL or missing references)
		if (_eliminationHandled || _botController == null)
		{
			return;
		}
		List<CCSPlayerController> source = (from player in Utilities.GetPlayers()
			where ((CEntityInstance)player).IsValid && !((CBasePlayerController)player).IsHLTV && (((CBaseEntity)player).TeamNum == 2 || ((CBaseEntity)player).TeamNum == 3)
			select player).ToList();
		if (source.Any((CCSPlayerController player) =>
		{
			//IL_0016: Unknown result type (might be due to invalid IL or missing references)
			//IL_001b: Invalid comparison between I4 and Unknown
			return player.Slot != victimSlot && ((CBaseEntity)player).TeamNum == (int)victimTeam && player.PawnIsAlive;
		}))
		{
			return;
		}
		CsTeam winningTeam = (CsTeam)(((int)victimTeam == 2) ? 3 : 2);
		List<CCSPlayerController> list = source.Where((CCSPlayerController player) =>
		{
			//IL_0022: Unknown result type (might be due to invalid IL or missing references)
			//IL_0027: Invalid comparison between I4 and Unknown
			return player.IsBot && player.PawnIsAlive && !player.HasBeenControlledByPlayerThisRound && ((CBaseEntity)player).TeamNum == (int)winningTeam;
		}).ToList();
		if (!source.Any((CCSPlayerController player) =>
		{
			//IL_0008: Unknown result type (might be due to invalid IL or missing references)
			//IL_000d: Invalid comparison between I4 and Unknown
			return ((CBaseEntity)player).TeamNum == (int)winningTeam && player.PawnIsAlive;
		}))
		{
			return;
		}
		_eliminationHandled = true;
		foreach (CCSPlayerController item in list)
		{
			bool flag = BotControllerBridge.SwitchBotWeapon(_botController, item.Slot, 9001);
			bool flag2 = BotControllerBridge.LockKnife(_botController, item.Slot);
			if (flag)
			{
				QueueInspectInjection(item.Slot);
			}
			if (flag2)
			{
				_knifeLockedBotSlots.Add(item.Slot);
			}
			if (!flag || !flag2)
			{
				Console.WriteLine($"[Smarter-Bot] Knife action failed for slot {item.Slot}: switch={flag}, lock={flag2}");
			}
		}
	}

	private void QueueInspectInjection(int slot)
	{
		Server.NextFrame((Action)(() =>
		{
			if (_botController != null)
			{
				CCSPlayerController playerFromSlot = Utilities.GetPlayerFromSlot(slot);
				if (!((CEntityInstance)(object)playerFromSlot == (CEntityInstance)null) && ((CEntityInstance)playerFromSlot).IsValid && playerFromSlot.IsBot && playerFromSlot.PawnIsAlive && !playerFromSlot.HasBeenControlledByPlayerThisRound && BotControllerBridge.InjectUsercmd(_botController, slot, 34359738368uL) <= 0)
				{
					Console.WriteLine($"[Smarter-Bot] Inspect injection failed for slot {slot}");
				}
			}
		}));
	}

	private void ReleaseKnifeLocks()
	{
		if (_botController != null)
		{
			foreach (int knifeLockedBotSlot in _knifeLockedBotSlots)
			{
				if (BotControllerBridge.IsKnifeLocked(_botController, knifeLockedBotSlot))
				{
					BotControllerBridge.UnlockWeapon(_botController, knifeLockedBotSlot);
				}
			}
		}
		_knifeLockedBotSlots.Clear();
	}

	private void InterruptReload(CCSPlayerController player, CCSPlayerPawn pawn, CCSBot bot, float now)
	{
		if (_botController == null || !bot.IsEnemyVisible || !IsReloading(player))
		{
			return;
		}
		int slot = player.Slot;
		if ((!_reloadInterruptCooldown.TryGetValue(slot, out var value) || !(now < value)) && GetReloadInterruptWeapon(pawn, out var weaponDefIndex) && BotControllerBridge.SwitchBotWeapon(_botController, slot, 9001))
		{
			_reloadInterruptCooldown[slot] = now + 0.75f;
			Server.NextFrame((Action)(() =>
			{
				SwitchBackAfterReloadInterrupt(slot, weaponDefIndex);
			}));
		}
	}

	private static bool GetReloadInterruptWeapon(CCSPlayerPawn pawn, out int weaponDefIndex)
	{
		//IL_004c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0053: Expected Obj, but got Unknown
		weaponDefIndex = -1;
		int num = -1;
		CPlayer_WeaponServices weaponServices = ((CBasePlayerPawn)pawn).WeaponServices;
		NetworkedVector<CHandle<CBasePlayerWeapon>> val = ((weaponServices != null) ? weaponServices.MyWeapons : null);
		if (val == null)
		{
			return false;
		}
		foreach (CHandle<CBasePlayerWeapon> item in val)
		{
			CBasePlayerWeapon value = item.Value;
			if ((CEntityInstance)(object)value == (CEntityInstance)null || !((CEntityInstance)value).IsValid)
			{
				continue;
			}
			CCSWeaponBase val2 = new CCSWeaponBase(((NativeEntity)value).Handle);
			CCSWeaponBaseVData vData = val2.VData;
			if (vData == null)
			{
				continue;
			}
			int itemDefinitionIndex = ((CEconEntity)val2).AttributeManager.Item.ItemDefinitionIndex;
			if (itemDefinitionIndex > 0)
			{
				if ((int)vData.GearSlot == 0 && ((CBasePlayerWeapon)val2).Clip1 > 0)
				{
					weaponDefIndex = itemDefinitionIndex;
					return true;
				}
				if (num < 0 && (int)vData.GearSlot == 1 && ((CBasePlayerWeapon)val2).Clip1 > 0)
				{
					num = itemDefinitionIndex;
				}
			}
		}
		weaponDefIndex = num;
		return weaponDefIndex >= 0;
	}

	private void SwitchBackAfterReloadInterrupt(int slot, int weaponDefIndex)
	{
		if (_botController != null)
		{
			CCSPlayerController playerFromSlot = Utilities.GetPlayerFromSlot(slot);
			if (!((CEntityInstance)(object)playerFromSlot == (CEntityInstance)null) && ((CEntityInstance)playerFromSlot).IsValid && playerFromSlot.IsBot && playerFromSlot.PawnIsAlive && !playerFromSlot.HasBeenControlledByPlayerThisRound && !BotControllerBridge.SwitchBotWeapon(_botController, slot, weaponDefIndex))
			{
				Console.WriteLine($"[Smarter-Bot] Reload interrupt restore failed for slot {slot}");
			}
		}
	}

	private void ReequipGunForActiveBots()
	{
		//IL_015a: Unknown result type (might be due to invalid IL or missing references)
		if (_botController == null)
		{
			return;
		}
		List<CCSPlayerController> list = Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller").ToList();
		int num = 0;
		int num2 = 0;
		foreach (CCSPlayerController item in list)
		{
			if (((CEntityInstance)item).IsValid && item.PawnIsAlive)
			{
				switch ((int)((CBaseEntity)item).TeamNum)
				{
				case 2:
					num++;
					break;
				case 3:
					num2++;
					break;
				}
			}
		}
		if (num == 0 && num2 == 0)
		{
			return;
		}
		foreach (CCSPlayerController item2 in list)
		{
			if (!((CEntityInstance)item2).IsValid || !item2.IsBot || !item2.PawnIsAlive || item2.HasBeenControlledByPlayerThisRound)
			{
				continue;
			}
			int teamNum = ((CBaseEntity)item2).TeamNum;
			int num3;
			if (teamNum != 2)
			{
				if (teamNum != 3)
				{
					continue;
				}
				num3 = num;
			}
			else
			{
				num3 = num2;
			}
			if (num3 == 0)
			{
				continue;
			}
			CCSPlayerPawn val = item2.PlayerPawn?.Value;
			if ((CEntityInstance)(object)val == (CEntityInstance)null || !((CEntityInstance)val).IsValid || val.IsDefusing)
			{
				continue;
			}
			CPlayer_WeaponServices weaponServices = ((CBasePlayerPawn)val).WeaponServices;
			CBasePlayerWeapon val2 = ((weaponServices == null) ? null : weaponServices.ActiveWeapon?.Value);
			if (!((CEntityInstance)(object)val2 == (CEntityInstance)null) && ((CEntityInstance)val2).IsValid)
			{
				CCSWeaponBaseVData vData = new CCSWeaponBase(((NativeEntity)val2).Handle).VData;
				if (vData != null && (int)vData.GearSlot == 2 && GetReequipWeapon(val, out var weaponDefIndex))
				{
					BotControllerBridge.SwitchBotWeapon(_botController, item2.Slot, weaponDefIndex);
				}
			}
		}
	}

	private static bool GetReequipWeapon(CCSPlayerPawn pawn, out int weaponDefIndex)
	{
		//IL_0049: Unknown result type (might be due to invalid IL or missing references)
		//IL_0050: Expected Obj, but got Unknown
		weaponDefIndex = -1;
		int num = -1;
		CPlayer_WeaponServices weaponServices = ((CBasePlayerPawn)pawn).WeaponServices;
		NetworkedVector<CHandle<CBasePlayerWeapon>> val = ((weaponServices != null) ? weaponServices.MyWeapons : null);
		if (val == null)
		{
			return false;
		}
		foreach (CHandle<CBasePlayerWeapon> item in val)
		{
			CBasePlayerWeapon value = item.Value;
			if ((CEntityInstance)(object)value == (CEntityInstance)null || !((CEntityInstance)value).IsValid)
			{
				continue;
			}
			CCSWeaponBase val2 = new CCSWeaponBase(((NativeEntity)value).Handle);
			CCSWeaponBaseVData vData = val2.VData;
			if (vData == null)
			{
				continue;
			}
			int itemDefinitionIndex = ((CEconEntity)val2).AttributeManager.Item.ItemDefinitionIndex;
			if (itemDefinitionIndex > 0)
			{
				if ((int)vData.GearSlot == 0)
				{
					weaponDefIndex = itemDefinitionIndex;
					return true;
				}
				if (num < 0 && (int)vData.GearSlot == 1)
				{
					num = itemDefinitionIndex;
				}
			}
		}
		weaponDefIndex = num;
		return weaponDefIndex >= 0;
	}

	private bool IsReloading(CCSPlayerController player)
	{
		if ((CEntityInstance)(object)player == (CEntityInstance)null || !((CEntityInstance)player).IsValid)
		{
			return false;
		}
		CCSPlayerPawn val = player.PlayerPawn?.Value;
		if ((CEntityInstance)(object)val == (CEntityInstance)null || !((CEntityInstance)val).IsValid)
		{
			return false;
		}
		CPlayer_WeaponServices weaponServices = ((CBasePlayerPawn)val).WeaponServices;
		CBasePlayerWeapon val2 = ((weaponServices == null) ? null : weaponServices.ActiveWeapon?.Value);
		if ((CEntityInstance)(object)val2 == (CEntityInstance)null || !((CEntityInstance)val2).IsValid)
		{
			return false;
		}
		return Schema.GetRef<bool>(((NativeEntity)val2).Handle, "CCSWeaponBase", "m_bInReload");
	}

	private void ResetWeaponsRoundState()
	{
		_reloadInterruptCooldown.Clear();
	}
}
