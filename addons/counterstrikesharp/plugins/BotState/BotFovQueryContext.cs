using System;

namespace BotState;

internal sealed class BotFovQueryContext
{
	internal readonly struct Scope : IDisposable
	{
		private readonly BotFovQueryContext owner;

		private readonly (nint Bot, BotViewFrustum Frame)? previous;

		internal Scope(BotFovQueryContext owner, nint bot, BotViewFrustum frame)
		{
			this.owner = owner;
			previous = owner.current;
			owner.current = (bot, frame);
		}

		public void Dispose()
		{
			owner.current = previous;
		}
	}

	private (nint Bot, BotViewFrustum Frame)? current;

	internal bool TryGet(nint bot, out BotViewFrustum frame)
	{
		frame = default;
		(nint, BotViewFrustum)? tuple = current;
		if (tuple.HasValue)
		{
			(nint, BotViewFrustum) valueOrDefault = tuple.GetValueOrDefault();
			if (valueOrDefault.Item1 == bot)
			{
				frame = valueOrDefault.Item2;
				return true;
			}
		}
		return false;
	}

	internal Scope Enter(nint bot, BotViewFrustum frame)
	{
		return new Scope(this, bot, frame);
	}
}
