using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;

namespace BotState;

internal sealed class BotFovHooks : IDisposable
{
	private readonly MemoryFunctionWithReturn<nint, nint, byte, nint, byte> player;

	private readonly MemoryFunctionWithReturn<nint, nint, byte, nint, byte> position;

	private readonly Func<nint, BotViewFrustum?> getView;

	private readonly Action<Exception> fault;

	private readonly BotFovQueryContext context = new BotFovQueryContext();

	private bool playerHooked;

	private bool positionHooked;

	private readonly int thread = Environment.CurrentManagedThreadId;

	internal bool Failed { get; private set; }

	internal long PointsTested { get; private set; }

	internal long PointsRejected { get; private set; }

	private bool Ready
	{
		get
		{
			if (!Failed && playerHooked && positionHooked)
			{
				return Environment.CurrentManagedThreadId == thread;
			}
			return false;
		}
	}

	internal BotFovHooks(Func<nint, BotViewFrustum?> getView, Action<Exception> fault)
	{
		if ((!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) || RuntimeInformation.ProcessArchitecture != Architecture.X64)
		{
			throw new PlatformNotSupportedException("Custom FOV requires Windows or Linux x64.");
		}
		this.getView = getView;
		this.fault = fault;
		player = new MemoryFunctionWithReturn<nint, nint, byte, nint, byte>(OperatingSystem.IsWindows() ? "48 89 5C 24 ? 44 88 44 24 ? 55 56 57 41 54 41 55 41 56 41 57 48 8D 6C 24 ? 48 81 EC ? ? ? ? 48 8D 05 ? ? ? ? 48 C7 45 ? ? ? ? ? 48 89 45 ? ? ? ? ? 0F 10 45 ? 48 8D 05 ? ? ? ? 4C 8B F2" : "55 48 8D 05 ? ? ? ? 48 89 E5 41 57 4C 8D 7D ? 41 56 41 89 D6 41 55 4C 89 FA 41 54 49 89 FC 53 48 8D 3D ? ? ? ? 48 89 F3 48 8D 35 ? ? ? ? 48 83 EC ? 48 89 45 ? 48 8D 05 ? ? ? ? 48 89 4D ?");
		position = new MemoryFunctionWithReturn<nint, nint, byte, nint, byte>(OperatingSystem.IsWindows() ? "48 89 5C 24 ? 48 89 74 24 ? 48 89 7C 24 ? 55 41 54 41 55 41 56 41 57 48 8D AC 24 ? ? ? ? 48 81 EC ? ? ? ? 48 8D 05 ? ? ? ? 48 C7 45 ? ? ? ? ? 48 89 45 ? ? ? ? ? 0F 10 45 ? 48 8D 05 ? ? ? ? 4C 8B E2" : "55 48 8D 05 ? ? ? ? 48 89 E5 41 57 4C 8D BD ? ? ? ? 41 56 41 55 41 54 49 89 CC 53 48 89 FB 48 8D 3D ? ? ? ? 48 81 EC ? ? ? ? 48 89 B5 ? ? ? ? 89 95 ? ? ? ? 4C 89 FA");
		if (((NativeObject)player).Handle == (IntPtr)0 || ((NativeObject)position).Handle == (IntPtr)0)
		{
			throw new InvalidOperationException("Custom FOV visibility signatures unavailable; native vision remains active.");
		}
		try
		{
			((BaseMemoryFunction)position).Hook((Func<DynamicHook, HookResult>)OnPosition, (HookMode)0);
			positionHooked = true;
			((BaseMemoryFunction)player).Hook((Func<DynamicHook, HookResult>)OnPlayer, (HookMode)0);
			playerHooked = true;
		}
		catch
		{
			Dispose();
			throw;
		}
	}

	private HookResult OnPlayer(DynamicHook hook)
	{
		//IL_00ae: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b7: Unknown result type (might be due to invalid IL or missing references)
		//IL_003f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0095: Unknown result type (might be due to invalid IL or missing references)
		if (!Ready || hook.GetParam<byte>(2) == 0)
		{
			return (HookResult)0;
		}
		bool owned = false;
		try
		{
			nint param = hook.GetParam<nint>(0);
			BotViewFrustum? botViewFrustum = getView(param);
			if (botViewFrustum.HasValue)
			{
				BotViewFrustum valueOrDefault = botViewFrustum.GetValueOrDefault();
				owned = true;
				using (context.Enter(param, valueOrDefault))
				{
					nint param2 = hook.GetParam<nint>(3);
					byte b = player.Invoke(param, hook.GetParam<nint>(1), (byte)0, param2, true);
					if (Failed)
					{
						b = 0;
						if (param2 != 0)
						{
							Marshal.WriteByte(param2, 0);
						}
					}
					hook.SetReturn<byte>(b);
					return (HookResult)3;
				}
			}
			return (HookResult)0;
		}
		catch (Exception ex)
		{
			return Fail(hook, ex, owned);
		}
	}

	private HookResult OnPosition(DynamicHook hook)
	{
		//IL_00d4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00dd: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a4: Unknown result type (might be due to invalid IL or missing references)
		//IL_0050: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c9: Unknown result type (might be due to invalid IL or missing references)
		if (!Ready)
		{
			return (HookResult)0;
		}
		bool owned = false;
		try
		{
			nint param = hook.GetParam<nint>(0);
			if (!context.TryGet(param, out var frame))
			{
				if (hook.GetParam<byte>(2) != 0)
				{
					BotViewFrustum? botViewFrustum = getView(param);
					if (botViewFrustum.HasValue)
					{
						BotViewFrustum valueOrDefault = botViewFrustum.GetValueOrDefault();
						frame = valueOrDefault;
						goto IL_005a;
					}
				}
				return (HookResult)0;
			}
			goto IL_005a;
			IL_005a:
			owned = true;
			nint param2 = hook.GetParam<nint>(1);
			PointsTested++;
			if (param2 == 0 || !frame.Contains(Marshal.PtrToStructure<Vector3>(param2)))
			{
				PointsRejected++;
				hook.SetReturn<byte>((byte)0);
				return (HookResult)3;
			}
			byte b = position.Invoke(param, param2, (byte)0, hook.GetParam<nint>(3), true);
			hook.SetReturn<byte>(b);
			return (HookResult)3;
		}
		catch (Exception ex)
		{
			return Fail(hook, ex, owned);
		}
	}

	private HookResult Fail(DynamicHook hook, Exception ex, bool owned)
	{
		Failed = true;
		fault(ex);
		if (owned)
		{
			hook.SetReturn<byte>((byte)0);
			return (HookResult)3;
		}
		return (HookResult)0;
	}

	public void Dispose()
	{
		try
		{
			if (playerHooked)
			{
				((BaseMemoryFunction)player).Unhook((Func<DynamicHook, HookResult>)OnPlayer, (HookMode)0);
				playerHooked = false;
			}
		}
		finally
		{
			if (positionHooked)
			{
				((BaseMemoryFunction)position).Unhook((Func<DynamicHook, HookResult>)OnPosition, (HookMode)0);
				positionHooked = false;
			}
		}
	}
}
