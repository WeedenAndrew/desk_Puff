namespace DeskPuff.Bluetooth.Windows.Protocol;

internal static class LoraxPaths
{
    internal const string ModelCode = "/p/sys/hw/mdcd";
    internal const string FirmwareVersion = "/p/sys/fw/ver";
    internal const string DeviceName = "/u/sys/name";
    internal const string BatteryCapacity = "/p/bat/cap";
    internal const string BatteryStateOfCharge = "/p/bat/soc";
    internal const string BatteryChargeState = "/p/bat/chg/stat";
    internal const string ChamberType = "/p/htr/chmt";
    internal const string OperatingState = "/p/app/stat/id";
    internal const string ActiveProfile = "/p/app/hcs";
    internal const string ActiveProfileName = "/p/app/thc/name";
    internal const string ActiveProfileTemperature = "/p/app/thc/temp";
    internal const string ActiveProfileTime = "/p/app/thc/time";
    internal const string HeaterTemperature = "/p/app/htr/temp";
    internal const string HeaterTargetTemperature = "/p/app/htr/tcmd";
    internal const string StateElapsedTime = "/p/app/stat/elap";
    internal const string StateTotalTime = "/p/app/stat/tott";
    internal const string ModeCommand = "/p/app/mc";
    internal const string TemperatureOverride = "/p/app/tmpo";
    internal const string TimeOverride = "/p/app/timo";
    internal const string StealthMode = "/u/app/ui/stlm";
    internal const string LanternMode = "/p/app/ltrn/cmd";

    private static readonly HashSet<string> ExactWritePaths = new(StringComparer.Ordinal)
    {
        ActiveProfile,
        ModeCommand,
        TemperatureOverride,
        TimeOverride,
        StealthMode,
        LanternMode,
    };

    internal static string ProfileName(int index) => ProfilePath(index, "name");

    internal static string ProfileTemperature(int index) => ProfilePath(index, "temp");

    internal static string ProfileTime(int index) => ProfilePath(index, "time");

    internal static string ProfileBoostTemperature(int index) => ProfilePath(index, "btmp");

    internal static string ProfileBoostTime(int index) => ProfilePath(index, "btim");

    internal static string ProfileColor(int index) => ProfilePath(index, "colr");

    internal static bool IsWriteAllowed(string path)
    {
        if (ExactWritePaths.Contains(path))
        {
            return true;
        }

        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments is ["u", "app", "hc", _, _] &&
            int.TryParse(segments[3], out int index) &&
            index is >= 0 and <= 3 &&
            segments[4] is "name" or "temp" or "time" or "btmp" or "btim" or "colr";
    }

    private static string ProfilePath(int index, string field)
    {
        if (index is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "Profile index must be between 0 and 3.");
        }

        return $"/u/app/hc/{index}/{field}";
    }
}
