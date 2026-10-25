using System;
using System.Numerics;

namespace BotState;

internal readonly record struct BotViewFrustum(Vector3 Eye, Vector3 Front, Vector3 Left, Vector3 Right, Vector3 Bottom, Vector3 Top, bool Omnidirectional)
{
	private const float Radians = (float)Math.PI / 180f;

	internal static bool Finite(Vector3 v)
	{
		if (float.IsFinite(v.X) && float.IsFinite(v.Y))
		{
			return float.IsFinite(v.Z);
		}
		return false;
	}

	internal static BotViewFrustum Build(Vector3 eye, Vector3 angles, BotFovOptions options)
	{
		options.Validate();
		if (!Finite(eye) || !Finite(angles))
		{
			throw new ArgumentException("Invalid bot eye/angles.");
		}
		float x = angles.X * ((float)Math.PI / 180f);
		float x2 = angles.Y * ((float)Math.PI / 180f);
		float x3 = angles.Z * ((float)Math.PI / 180f);
		float num = MathF.Sin(x);
		float num2 = MathF.Cos(x);
		float num3 = MathF.Sin(x2);
		float num4 = MathF.Cos(x2);
		Vector3 vector = new Vector3(num2 * num4, num2 * num3, 0f - num);
		Vector3 vector2 = new Vector3(0f - num3, num4, 0f);
		Vector3 vector3 = new Vector3(num * num4, num * num3, num2);
		Vector3 vector4 = vector2 * MathF.Cos(x3) + vector3 * MathF.Sin(x3);
		Vector3 vector5 = vector3 * MathF.Cos(x3) - vector2 * MathF.Sin(x3);
		bool flag = options.HorizontalDegrees == 360f;
		float num5 = (flag ? 180f : options.HorizontalDegrees);
		float num6 = (flag ? 180f : options.VerticalDegrees);
		float num7 = MathF.Sin(num5 * ((float)Math.PI / 180f) * 0.5f);
		float num8 = ((num5 == 180f) ? 0f : MathF.Cos(num5 * ((float)Math.PI / 180f) * 0.5f));
		float num9 = MathF.Sin(num6 * ((float)Math.PI / 180f) * 0.5f);
		float num10 = ((num6 == 180f) ? 0f : MathF.Cos(num6 * ((float)Math.PI / 180f) * 0.5f));
		return new BotViewFrustum(eye, vector, vector * num7 + vector4 * num8, vector * num7 - vector4 * num8, vector * num9 + vector5 * num10, vector * num9 - vector5 * num10, flag);
	}

	internal bool Contains(Vector3 point)
	{
		Vector3 vector = point - Eye;
		if (!Finite(vector))
		{
			return false;
		}
		if (Omnidirectional)
		{
			return true;
		}
		if (Vector3.Dot(vector, Front) > 0f && Vector3.Dot(vector, Left) >= -0.0001f && Vector3.Dot(vector, Right) >= -0.0001f && Vector3.Dot(vector, Bottom) >= -0.0001f)
		{
			return Vector3.Dot(vector, Top) >= -0.0001f;
		}
		return false;
	}
}
