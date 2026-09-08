namespace BotState;

internal static class HumanlikeMode
{
    internal const string DomainKey = "CS2BotImprover.bot_humanlike";

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
