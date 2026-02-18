using System.ComponentModel;
using Fabr.Sdk;

namespace OpenCaddis.Agentic.Tools;

public static class DateTimeTools
{
    [ToolAlias("GetDateTime")]
    [Description("Get the current date and time. Returns the current local date, time, day of week, and UTC offset.")]
    public static string GetDateTime()
    {
        var now = DateTimeOffset.Now;
        return $"{now:dddd, MMMM d, yyyy h:mm:ss tt} (UTC{now.Offset:hhmm})";
    }
}
