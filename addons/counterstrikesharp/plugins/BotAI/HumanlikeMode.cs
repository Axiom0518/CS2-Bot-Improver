// Cross-plugin humanlike flag (same AppDomain key used by BotState / Aim / NadeSystem).
namespace BotAI;

internal static class HumanlikeMode
{
    internal const string DomainKey = "CS2BotImprover.bot_humanlike";

    // Default true when unset: prefer humanlike until explicitly disabled.
    internal static bool Enabled
    {
        get
        {
            object? v = AppDomain.CurrentDomain.GetData(DomainKey);
            return v is not bool b || b;
        }
        set => AppDomain.CurrentDomain.SetData(DomainKey, value);
    }
}
