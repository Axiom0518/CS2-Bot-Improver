using System;

namespace BotState;

public sealed record BotFovOptions
{
	public bool Enabled { get; init; } = true;

	public float HorizontalDegrees { get; init; } = 120f;

	public float VerticalDegrees { get; init; } = 180f;

	internal static BotFovOptions FromDegrees(float horizontal, float? vertical = null)
	{
		float? num = vertical;
		float verticalDegrees;
		if (num.HasValue)
		{
			verticalDegrees = num.GetValueOrDefault();
		}
		else
		{
			bool flag = ((horizontal == 180f || horizontal == 360f) ? true : false);
			verticalDegrees = (flag ? horizontal : ((float)(2.0 * Math.Atan(Math.Tan((double)horizontal * Math.PI / 360.0) / 1.7777777777777777) * 180.0 / Math.PI)));
		}
		BotFovOptions botFovOptions = new BotFovOptions
		{
			HorizontalDegrees = horizontal,
			VerticalDegrees = verticalDegrees
		};
		botFovOptions.Validate();
		return botFovOptions;
	}

	internal void Validate()
	{
		if (HorizontalDegrees == 360f)
		{
			if (VerticalDegrees != 360f)
			{
				throw new ArgumentException("360-degree mode requires both angles to be 360.");
			}
			return;
		}
		if (!ValidAngle(HorizontalDegrees))
		{
			throw new ArgumentException("HorizontalDegrees must be finite and within 1..180, or 360 for omnidirectional mode.");
		}
		if (ValidAngle(VerticalDegrees))
		{
			return;
		}
		throw new ArgumentException("Vertical FOV must be finite and within 1..180, including when derived from 16:9.");
	}

	private static bool ValidAngle(float value)
	{
		if (float.IsFinite(value) && value >= 1f)
		{
			return value <= 180f;
		}
		return false;
	}
}
