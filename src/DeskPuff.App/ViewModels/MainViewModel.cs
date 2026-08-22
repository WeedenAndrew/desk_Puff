using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using DeskPuff.App.Infrastructure;
using DeskPuff.Core.Devices;
using DeskPuff.Core.Profiles;
using DeskPuff.Core.Safety;
using DeskPuff.Core.Sessions;

namespace DeskPuff.App.ViewModels;

internal enum AppPage
{
    Home,
    Profiles,
    Color,
    Settings,
}

internal sealed record ShortcutOption(Key? Value, string Label);

internal static class PalettePresentation
{
    public static string ContrastForeground(IReadOnlyList<string> colors)
    {
        int totalBrightness = 0;
        int validColors = 0;
        foreach (string color in colors)
        {
            if (color is not { Length: 7 } || color[0] != '#' ||
                !uint.TryParse(
                    color.AsSpan(1),
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture,
                    out uint rgb))
            {
                continue;
            }

            int red = (int)((rgb >> 16) & 0xFF);
            int green = (int)((rgb >> 8) & 0xFF);
            int blue = (int)(rgb & 0xFF);
            totalBrightness += ((red * 299) + (green * 587) + (blue * 114)) / 1000;
            validColors++;
        }

        return validColors > 0 && totalBrightness / validColors >= 150
            ? "#0B1715"
            : "#F6F7F9";
    }
}

internal sealed record ColorPaletteOption(
    string Name,
    IReadOnlyList<string> Colors,
    string StorageFileName = "")
{
    public string ColorOne => ColorAt(0);

    public string ColorTwo => ColorAt(1);

    public string ColorThree => ColorAt(2);

    public string ColorFour => ColorAt(3);

    public Visibility ColorTwoVisibility => ColorVisibilityAt(1);

    public Visibility ColorThreeVisibility => ColorVisibilityAt(2);

    public Visibility ColorFourVisibility => ColorVisibilityAt(3);

    private string ColorAt(int index) => index < Colors.Count ? Colors[index] : Colors[^1];

    private Visibility ColorVisibilityAt(int index) =>
        index < Colors.Count ? Visibility.Visible : Visibility.Collapsed;
}

internal sealed record DeviceProfileOption(
    int Index,
    string Name,
    IReadOnlyList<string> Colors)
{
    public string SlotText => $"PROFILE {Index + 1}";

    public string ColorOne => ColorAt(0);

    public string ColorTwo => ColorAt(1);

    public string ColorThree => ColorAt(2);

    public string ColorFour => ColorAt(3);

    private string ColorAt(int index) => index < Colors.Count ? Colors[index] : Colors[^1];
}

internal sealed record HeatingProfileOption(
    string Name,
    string DeviceProfileName,
    double TargetTemperatureCelsius,
    double DurationSeconds,
    VaporLevel Vapor,
    double BoostTemperatureCelsius,
    double BoostDurationSeconds,
    string ColorProfileName,
    IReadOnlyList<string> Colors,
    string StorageFileName = "")
{
    public string DetailText => $"{DeviceProfileName} • {ColorProfileName}";

    public string ColorOne => Colors[0];

    public string ColorTwo => ColorAt(1);

    public string ColorThree => ColorAt(2);

    public string ColorFour => ColorAt(3);

    public Visibility ColorTwoVisibility => ColorVisibilityAt(1);

    public Visibility ColorThreeVisibility => ColorVisibilityAt(2);

    public Visibility ColorFourVisibility => ColorVisibilityAt(3);

    private string ColorAt(int index) => index < Colors.Count ? Colors[index] : Colors[^1];

    private Visibility ColorVisibilityAt(int index) =>
        index < Colors.Count ? Visibility.Visible : Visibility.Collapsed;
}

internal sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private const string DefaultAppAccentHex = "#BB376A";
    private const string LegacyDefaultAppAccentHex = "#8CE9D2";

    private static readonly string[] DefaultProfileColors =
    [
        "#0000FF",
        "#6EE916",
        "#F80B00",
        "#FFFFFF",
    ];

    private static readonly DeviceLimits PreferenceFallbackLimits = new(
        190,
        327,
        TimeSpan.FromSeconds(10),
        TimeSpan.FromMinutes(2),
        30,
        TimeSpan.FromMinutes(2));

    private static readonly ShortcutOption[] AvailableShortcutOptions =
    [
        new(Key.Up, "Up Arrow"),
        new(Key.Down, "Down Arrow"),
        new(Key.Left, "Left Arrow"),
        new(Key.Right, "Right Arrow"),
        new(Key.PageUp, "Page Up"),
        new(Key.PageDown, "Page Down"),
        new(Key.Home, "Home"),
        new(Key.End, "End"),
        new(Key.F1, "F1"),
        new(Key.F2, "F2"),
        new(Key.F3, "F3"),
        new(Key.F4, "F4"),
        new(Key.F5, "F5"),
        new(Key.F6, "F6"),
        new(Key.F7, "F7"),
        new(Key.F8, "F8"),
        new(Key.F9, "F9"),
        new(Key.F10, "F10"),
        new(Key.F11, "F11"),
        new(Key.F12, "F12"),
    ];

    private static readonly ShortcutOption[] AvailableProfileMacroOptions =
    [
        new(null, "None"),
        .. AvailableShortcutOptions,
    ];

    private readonly SessionController controller;
    private readonly LocalProfileLibrary profileLibrary;
    private readonly bool demoMode;
    private readonly List<AsyncRelayCommand> asyncCommands = [];
    private readonly List<RelayCommand> relayCommands = [];
    private CancellationTokenSource? pollingCancellation;
    private Task? pollingTask;
    private DeviceSnapshot snapshot;
    private IReadOnlyList<HeatProfile> profiles = [];
    private readonly Dictionary<int, Key> profileMacros = [];
    private DeviceCandidate? selectedCandidate;
    private DeviceCandidate? selectedHotSwapCandidate;
    private DeviceCandidate? connectedCandidate;
    private AppPage currentPage;
    private string statusText = "Ready to scan";
    private string editorName = string.Empty;
    private double editorTemperature = 500;
    private double editorDurationSeconds = 40;
    private VaporLevel editorVapor;
    private double editorBoostTemperature = 10;
    private double editorBoostSeconds = 10;
    private string editorColor = "#0000FF";
    private string editorColorTwo = string.Empty;
    private string editorColorThree = string.Empty;
    private string editorColorFour = string.Empty;
    private string appAccentHex = DefaultAppAccentHex;
    private string paletteName = string.Empty;
    private ColorPaletteOption? selectedSavedColorPalette;
    private DeviceProfileOption? selectedDeviceProfile;
    private string heatingProfileName = string.Empty;
    private HeatingProfileOption? selectedSavedHeatingProfile;
    private int selectedColorStopIndex;
    private bool useFahrenheit = true;
    private bool stealthEnabled;
    private bool lanternEnabled;
    private Key previousProfileKey = Key.Left;
    private Key nextProfileKey = Key.Right;
    private Key temperatureBoostKey = Key.Up;
    private Key timeBoostKey = Key.Down;
    private double quickHitTemperature = 9;
    private double quickHitTimeSeconds = 10;
    private Key? editorProfileMacroKey;
    private bool userOperationBusy;
    private bool disposed;

    internal MainViewModel(SessionController controller, bool demoMode)
    {
        this.controller = controller;
        this.demoMode = demoMode;
        profileLibrary = new LocalProfileLibrary(demoMode
            ? Path.Combine(Path.GetTempPath(), "desk_Puff", "demo-profiles")
            : LocalProfileLibrary.DefaultRootPath());
        snapshot = controller.Snapshot;
        controller.SnapshotChanged += ControllerSnapshotChanged;

        ScanCommand = CreateAsync(ScanAsync, () => !IsConnected);
        ConnectCommand = CreateAsync(ConnectAsync, () => !IsConnected && SelectedCandidate is not null);
        DisconnectCommand = CreateAsync(DisconnectAsync, () => IsConnected);
        StartStopCommand = CreateAsync(StartStopAsync, () => CanStartOrStop);
        PreviousProfileCommand = CreateAsync(
            token => SelectRelativeProfileAsync(-1, token),
            () => CanEditDevice && profiles.Count > 1 && CurrentPage is AppPage.Home or AppPage.Profiles);
        NextProfileCommand = CreateAsync(
            token => SelectRelativeProfileAsync(1, token),
            () => CanEditDevice && profiles.Count > 1 && CurrentPage is AppPage.Home or AppPage.Profiles);
        SelectDeviceProfileCommand = CreateAsync(
            SelectSelectedDeviceProfileAsync,
            () => CanEditDevice && CurrentPage == AppPage.Profiles && SelectedDeviceProfile is not null);
        BoostTemperatureCommand = CreateAsync(
            BoostTemperatureAsync,
            () => CanBoost);
        BoostTimeCommand = CreateAsync(BoostTimeAsync, () => CanBoost);
        SaveProfileCommand = CreateAsync(SaveProfileAsync, () => CanEditDevice && profiles.Count > 0);
        SaveColorPaletteCommand = CreateAsync(SaveColorPaletteAsync, () => IsConnected);
        DeleteColorPaletteCommand = CreateAsync(
            DeleteColorPaletteAsync,
            () => SelectedSavedColorPalette is not null);
        SaveHeatingProfileCommand = CreateAsync(
            SaveHeatingProfileAsync,
            () => IsConnected && profiles.Count > 0);
        DeleteHeatingProfileCommand = CreateAsync(
            DeleteHeatingProfileAsync,
            () => SelectedSavedHeatingProfile is not null);
        ReloadLocalProfilesCommand = CreateAsync(
            ReloadLocalProfilesAsync,
            () => IsConnected);
        ToggleStealthCommand = CreateAsync(ToggleStealthAsync, () => CanEditDevice);
        ToggleLanternCommand = CreateAsync(ToggleLanternAsync, () => CanEditDevice);
        SavePreferencesCommand = CreateAsync(SavePreferencesAsync);
        HotSwapScanCommand = CreateAsync(HotSwapScanAsync, () => CanHotSwap);
        HotSwapConnectCommand = CreateAsync(
            HotSwapConnectAsync,
            () => CanHotSwap && SelectedHotSwapCandidate is not null);

        ShowHomeCommand = CreateRelay(() => CurrentPage = AppPage.Home, () => IsConnected);
        ShowProfilesCommand = CreateRelay(() => CurrentPage = AppPage.Profiles, () => IsConnected);
        ShowColorCommand = CreateRelay(() => CurrentPage = AppPage.Color, () => IsConnected);
        ShowSettingsCommand = CreateRelay(() => CurrentPage = AppPage.Settings, () => IsConnected);
        ApplyColorPaletteCommand = CreateRelay(
            ApplySelectedColorPalette,
            () => SelectedSavedColorPalette is not null);
        ApplyHeatingProfileCommand = CreateAsync(
            ApplySelectedHeatingProfileAsync,
            () => SelectedSavedHeatingProfile is not null && IsConnected && profiles.Count > 0);
        UseColorWithHeatingProfileCommand = CreateRelay(
            UseColorWithHeatingProfile,
            () => IsConnected && profiles.Count > 0);
        PreviousColorStopCommand = CreateRelay(
            () => SelectRelativeColorStop(-1),
            () => CurrentPage == AppPage.Color && EditorPaletteColors().Length > 1);
        NextColorStopCommand = CreateRelay(
            () => SelectRelativeColorStop(1),
            () => CurrentPage == AppPage.Color && EditorPaletteColors().Length > 1);
        AddColorStopCommand = CreateRelay(
            AddColorStop,
            () => CurrentPage == AppPage.Color && EditorPaletteColors().Length < 4);
        RemoveColorStopCommand = CreateRelay(
            RemoveColorStop,
            () => CurrentPage == AppPage.Color && EditorPaletteColors().Length > 1);
        ToggleTemperatureUnitCommand = CreateRelay(ToggleTemperatureUnit);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<DeviceCandidate> Candidates { get; } = [];

    public ObservableCollection<DeviceCandidate> HotSwapCandidates { get; } = [];

    public ObservableCollection<ColorPaletteOption> SavedColorPalettes { get; } = [];

    public ObservableCollection<DeviceProfileOption> DeviceProfiles { get; } = [];

    public ObservableCollection<HeatingProfileOption> SavedHeatingProfiles { get; } = [];

    public IReadOnlyList<VaporLevel> VaporLevels { get; } = Enum.GetValues<VaporLevel>();

    public IReadOnlyList<ShortcutOption> ShortcutOptions { get; } = AvailableShortcutOptions;

    public IReadOnlyList<ShortcutOption> ProfileMacroOptions { get; } = AvailableProfileMacroOptions;

    public ICommand ScanCommand { get; }

    public ICommand ConnectCommand { get; }

    public ICommand DisconnectCommand { get; }

    public ICommand StartStopCommand { get; }

    public ICommand PreviousProfileCommand { get; }

    public ICommand NextProfileCommand { get; }

    public ICommand SelectDeviceProfileCommand { get; }

    public ICommand BoostTemperatureCommand { get; }

    public ICommand BoostTimeCommand { get; }

    public ICommand SaveProfileCommand { get; }

    public ICommand SaveColorPaletteCommand { get; }

    public ICommand ApplyColorPaletteCommand { get; }

    public ICommand DeleteColorPaletteCommand { get; }

    public ICommand SaveHeatingProfileCommand { get; }

    public ICommand ApplyHeatingProfileCommand { get; }

    public ICommand DeleteHeatingProfileCommand { get; }

    public ICommand UseColorWithHeatingProfileCommand { get; }

    public ICommand ReloadLocalProfilesCommand { get; }

    public ICommand ToggleStealthCommand { get; }

    public ICommand ToggleLanternCommand { get; }

    public ICommand SavePreferencesCommand { get; }

    public ICommand HotSwapScanCommand { get; }

    public ICommand HotSwapConnectCommand { get; }

    public ICommand ShowHomeCommand { get; }

    public ICommand ShowProfilesCommand { get; }

    public ICommand ShowColorCommand { get; }

    public ICommand ShowSettingsCommand { get; }

    public ICommand PreviousColorStopCommand { get; }

    public ICommand NextColorStopCommand { get; }

    public ICommand AddColorStopCommand { get; }

    public ICommand RemoveColorStopCommand { get; }

    public ICommand ToggleTemperatureUnitCommand { get; }

    public DeviceCandidate? SelectedCandidate
    {
        get => selectedCandidate;
        set
        {
            if (SetProperty(ref selectedCandidate, value))
            {
                NotifyCommandStates();
            }
        }
    }

    public DeviceCandidate? SelectedHotSwapCandidate
    {
        get => selectedHotSwapCandidate;
        set
        {
            if (SetProperty(ref selectedHotSwapCandidate, value))
            {
                NotifyCommandStates();
            }
        }
    }

    public AppPage CurrentPage
    {
        get => currentPage;
        set
        {
            if (SetProperty(ref currentPage, value))
            {
                NotifyPageProperties();
                NotifyCommandStates();
            }
        }
    }

    public string StatusText
    {
        get => statusText;
        private set => SetProperty(ref statusText, value);
    }

    public string EditorName
    {
        get => editorName;
        set
        {
            string previousName = editorName;
            if (SetProperty(ref editorName, value) &&
                (string.IsNullOrWhiteSpace(HeatingProfileName) ||
                 string.Equals(HeatingProfileName, previousName, StringComparison.Ordinal)))
            {
                HeatingProfileName = value;
            }
        }
    }

    public double EditorTemperature
    {
        get => editorTemperature;
        set => SetProperty(ref editorTemperature, value);
    }

    public double EditorDurationSeconds
    {
        get => editorDurationSeconds;
        set => SetProperty(ref editorDurationSeconds, value);
    }

    public VaporLevel EditorVapor
    {
        get => editorVapor;
        set => SetProperty(ref editorVapor, value);
    }

    public double EditorBoostTemperature
    {
        get => editorBoostTemperature;
        set => SetProperty(ref editorBoostTemperature, value);
    }

    public double EditorBoostSeconds
    {
        get => editorBoostSeconds;
        set => SetProperty(ref editorBoostSeconds, value);
    }

    public string EditorColor
    {
        get => editorColor;
        set => SetEditorColor(ref editorColor, value, nameof(EditorColor), nameof(EditorColorDisplay));
    }

    public string EditorColorTwo
    {
        get => editorColorTwo;
        set => SetEditorColor(ref editorColorTwo, value, nameof(EditorColorTwo), nameof(EditorColorTwoDisplay));
    }

    public string EditorColorThree
    {
        get => editorColorThree;
        set => SetEditorColor(ref editorColorThree, value, nameof(EditorColorThree), nameof(EditorColorThreeDisplay));
    }

    public string EditorColorFour
    {
        get => editorColorFour;
        set => SetEditorColor(ref editorColorFour, value, nameof(EditorColorFour), nameof(EditorColorFourDisplay));
    }

    public string AppAccentHex
    {
        get => appAccentHex;
        set
        {
            if (SetProperty(ref appAccentHex, value ?? string.Empty) &&
                IsHexColor(appAccentHex.Trim()))
            {
                ApplyAppAccent(appAccentHex.Trim());
            }
        }
    }

    public string PaletteName
    {
        get => paletteName;
        set => SetProperty(ref paletteName, value);
    }

    public ColorPaletteOption? SelectedSavedColorPalette
    {
        get => selectedSavedColorPalette;
        set
        {
            if (SetProperty(ref selectedSavedColorPalette, value))
            {
                NotifyCommandStates();
            }
        }
    }

    public DeviceProfileOption? SelectedDeviceProfile
    {
        get => selectedDeviceProfile;
        set
        {
            if (SetProperty(ref selectedDeviceProfile, value))
            {
                NotifyCommandStates();
                if (value is not null &&
                    value.Index != snapshot.ActiveProfileIndex &&
                    SelectDeviceProfileCommand.CanExecute(null))
                {
                    SelectDeviceProfileCommand.Execute(null);
                }
            }
        }
    }

    public string HeatingProfileName
    {
        get => heatingProfileName;
        set => SetProperty(ref heatingProfileName, value);
    }

    public HeatingProfileOption? SelectedSavedHeatingProfile
    {
        get => selectedSavedHeatingProfile;
        set
        {
            if (SetProperty(ref selectedSavedHeatingProfile, value))
            {
                NotifyCommandStates();
            }
        }
    }

    public Key PreviousProfileKey
    {
        get => previousProfileKey;
        set => SetShortcutKey(
            ref previousProfileKey,
            value,
            nameof(PreviousProfileKey),
            nameof(PreviousProfileShortcutText));
    }

    public Key NextProfileKey
    {
        get => nextProfileKey;
        set => SetShortcutKey(
            ref nextProfileKey,
            value,
            nameof(NextProfileKey),
            nameof(NextProfileShortcutText));
    }

    public Key TemperatureBoostKey
    {
        get => temperatureBoostKey;
        set => SetShortcutKey(
            ref temperatureBoostKey,
            value,
            nameof(TemperatureBoostKey),
            nameof(TemperatureBoostShortcutText));
    }

    public Key TimeBoostKey
    {
        get => timeBoostKey;
        set => SetShortcutKey(
            ref timeBoostKey,
            value,
            nameof(TimeBoostKey),
            nameof(TimeBoostShortcutText));
    }

    public double QuickHitTemperature
    {
        get => quickHitTemperature;
        set
        {
            if (SetProperty(ref quickHitTemperature, value))
            {
                OnPropertyChanged(nameof(QuickTemperatureBoostText));
            }
        }
    }

    public double QuickHitTimeSeconds
    {
        get => quickHitTimeSeconds;
        set
        {
            if (SetProperty(ref quickHitTimeSeconds, value))
            {
                OnPropertyChanged(nameof(QuickTimeBoostText));
            }
        }
    }

    public Key? EditorProfileMacroKey
    {
        get => editorProfileMacroKey;
        set
        {
            if (value is { } requestedKey &&
                (IsGlobalShortcutKey(requestedKey) ||
                 profileMacros.Any(item =>
                     item.Key != snapshot.ActiveProfileIndex &&
                     item.Value == requestedKey)))
            {
                StatusText = "That key is already assigned to another shortcut";
                OnPropertyChanged(nameof(EditorProfileMacroKey));
                return;
            }

            if (!SetProperty(ref editorProfileMacroKey, value) || profiles.Count == 0)
            {
                return;
            }

            if (value is { } key)
            {
                profileMacros[snapshot.ActiveProfileIndex] = key;
            }
            else
            {
                profileMacros.Remove(snapshot.ActiveProfileIndex);
            }

            NotifyProfileCarouselProperties();
        }
    }

    public bool UseFahrenheit
    {
        get => useFahrenheit;
        private set
        {
            if (SetProperty(ref useFahrenheit, value))
            {
                NotifyTemperatureProperties();
            }
        }
    }

    public bool IsDemoMode => demoMode;

    public bool IsConnected => snapshot.ConnectionState is
        DeviceConnectionState.ConnectedReadOnly or
        DeviceConnectionState.ConnectedControlEnabled;

    public bool IsDisconnected => !IsConnected;

    public bool IsReadOnly => snapshot.ConnectionState == DeviceConnectionState.ConnectedReadOnly;

    public bool IsHeating => snapshot.IsHeating;

    public bool CanEditDevice =>
        snapshot.ConnectionState == DeviceConnectionState.ConnectedControlEnabled &&
        !snapshot.IsHeating;

    public bool CanStartOrStop =>
        snapshot.ConnectionState == DeviceConnectionState.ConnectedControlEnabled &&
        (snapshot.IsHeating ||
         (snapshot.OperatingState == DeviceOperatingState.Idle &&
          snapshot.Chamber is not (ChamberKind.None or ChamberKind.Unknown) &&
          snapshot.CurrentTemperatureCelsius is not null));

    public bool CanBoost =>
        snapshot.ConnectionState == DeviceConnectionState.ConnectedControlEnabled &&
        snapshot.OperatingState == DeviceOperatingState.Active &&
        snapshot.Capabilities?.SupportsIndependentBoost == true;

    public bool CanHotSwap => DeviceHandoffPolicy.EvaluateSource(snapshot).IsAllowed;

    public Visibility ConnectionVisibility => IsDisconnected ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DeviceVisibility => IsConnected ? Visibility.Visible : Visibility.Collapsed;

    public Visibility HomeVisibility => PageVisibility(AppPage.Home);

    public Visibility ProfilesVisibility => PageVisibility(AppPage.Profiles);

    public Visibility ColorVisibility => PageVisibility(AppPage.Color);

    public Visibility SettingsVisibility => PageVisibility(AppPage.Settings);

    public string DeviceName => snapshot.Identity?.Name ?? "NO DEVICE";

    public string DeviceModelText => snapshot.Identity?.Family switch
    {
        DeviceFamily.PeakPro => "PEAK PRO",
        DeviceFamily.NewProxy => "NEW PROXY",
        _ when snapshot.Identity is not null => $"MODEL {snapshot.Identity.ModelCode}",
        _ => "NO DEVICE",
    };

    public string ProfileName => snapshot.ActiveProfileName.ToUpperInvariant();

    public string VaporText => $"{snapshot.Vapor.ToString().ToUpperInvariant()} VAPOR";

    public string ChamberText => snapshot.Chamber switch
    {
        ChamberKind.ThreeDXL => "3DXL CHAMBER",
        ChamberKind.ThreeD => "3D CHAMBER",
        ChamberKind.Classic => "CLASSIC CHAMBER",
        ChamberKind.None => "NO CHAMBER",
        _ => "UNKNOWN CHAMBER",
    };

    public string BatteryText => $"{Math.Round(snapshot.BatteryPercent):0}%";

    public double BatteryBlockOneOpacity => BatteryBlockOpacity(1);

    public double BatteryBlockTwoOpacity => BatteryBlockOpacity(2);

    public double BatteryBlockThreeOpacity => BatteryBlockOpacity(3);

    public double BatteryBlockFourOpacity => BatteryBlockOpacity(4);

    public string ActiveProfileColor
    {
        get
        {
            HeatProfile? profile = profiles.FirstOrDefault(item => item.Index == snapshot.ActiveProfileIndex);
            if (profile is not null)
            {
                return profile.ColorHex;
            }

            int index = Math.Clamp(snapshot.ActiveProfileIndex, 0, DefaultProfileColors.Length - 1);
            return DefaultProfileColors[index];
        }
    }

    public string ProfileColorOne => ProfileColorAt(0);

    public string ProfileColorTwo => ProfileColorAt(1);

    public string ProfileColorThree => ProfileColorAt(2);

    public string ProfileColorFour => ProfileColorAt(3);

    public string ActiveProfileForegroundDisplay => PalettePresentation.ContrastForeground(ActiveProfilePalette());

    public string ActiveProfilePositionText => profiles.Count == 0
        ? "NO PROFILE"
        : $"PROFILE {snapshot.ActiveProfileIndex + 1} OF {profiles.Count}";

    public string ProfileColorSourceText => profiles
        .FirstOrDefault(profile => profile.Index == snapshot.ActiveProfileIndex)?.HasDeviceColor == true
            ? "DEVICE COLOR"
            : "DEFAULT COLOR PREVIEW";

    public string TemperatureText
    {
        get
        {
            double? celsius = snapshot.IsHeating
                ? snapshot.CurrentTemperatureCelsius
                : snapshot.TargetTemperatureCelsius;
            return celsius is null
                ? "--°"
                : $"{Math.Round(ToDisplayTemperature(celsius.Value)):0}°";
        }
    }

    public string TemperatureCaption => snapshot.IsHeating ? "LIVE CHAMBER" : "SET TEMPERATURE";

    public string SessionTimeText
    {
        get
        {
            TimeSpan time = snapshot.OperatingState == DeviceOperatingState.Active
                ? snapshot.SessionRemaining
                : snapshot.SessionTotal;
            int minutes = Math.Max(0, (int)time.TotalMinutes);
            int seconds = Math.Max(0, time.Seconds);
            return $"{minutes:00}:{seconds:00}";
        }
    }

    public string StartStopText => snapshot.IsHeating ? "STOP" : "START";

    public string PreviousProfileShortcutText => KeyLabel(PreviousProfileKey);

    public string NextProfileShortcutText => KeyLabel(NextProfileKey);

    public string TemperatureBoostShortcutText => KeyLabel(TemperatureBoostKey);

    public string TimeBoostShortcutText => KeyLabel(TimeBoostKey);

    public string QuickHitTemperatureLabel => UseFahrenheit
        ? "TEMP INCREASE °F"
        : "TEMP INCREASE °C";

    public string QuickTemperatureBoostText => $"+{Math.Round(QuickHitTemperature, 1):0.#}°";

    public string QuickTimeBoostText => $"+{Math.Round(QuickHitTimeSeconds):0}s";

    public string OperatingStateText => snapshot.OperatingState switch
    {
        DeviceOperatingState.Preheating => "PREHEATING",
        DeviceOperatingState.Active => "SESSION ACTIVE",
        DeviceOperatingState.Fading => "COOLING",
        DeviceOperatingState.Idle => "READY",
        _ => snapshot.OperatingState.ToString().ToUpperInvariant(),
    };

    public string TemperatureUnitText => UseFahrenheit ? "FAHRENHEIT (°F)" : "CELSIUS (°C)";

    public string TemperatureEditorLabel => UseFahrenheit ? "TEMPERATURE °F" : "TEMPERATURE °C";

    public string BoostEditorLabel => UseFahrenheit ? "TEMP BOOST °F" : "TEMP BOOST °C";

    public string FirmwareText => snapshot.Identity is null
        ? "—"
        : $"{snapshot.Identity.Family} • model {snapshot.Identity.ModelCode} • firmware {snapshot.Identity.FirmwareVersion}";

    public string SafetyText => snapshot.Fault ??
        (snapshot.IsFirmwareVerified
            ? "Control enabled for this verified hardware profile."
            : "Read-only safety lock: this exact firmware has not been hardware-verified.");

    public string HeaderBadgeText
    {
        get
        {
            string connectionLabel = snapshot.ConnectionState switch
            {
                DeviceConnectionState.Connecting => "CONNECTING",
                DeviceConnectionState.Authenticating => "AUTHENTICATING",
                _ when IsConnected => DeviceModelText,
                _ => "NO DEVICE",
            };
            return demoMode ? $"DEMO • {connectionLabel}" : connectionLabel;
        }
    }

    public string EditorColorDisplay => ColorDisplayOrFallback(EditorColor, "#000000");

    public string EditorColorTwoDisplay => ColorDisplayOrFallback(EditorColorTwo, EditorColorDisplay);

    public string EditorColorThreeDisplay => ColorDisplayOrFallback(EditorColorThree, EditorColorTwoDisplay);

    public string EditorColorFourDisplay => ColorDisplayOrFallback(EditorColorFour, EditorColorThreeDisplay);

    public string EditorPaletteForegroundDisplay => PalettePresentation.ContrastForeground(EditorPaletteColors());

    public Visibility EditorColorTwoVisibility => HexColorVisibility(EditorColorTwo);

    public Visibility EditorColorThreeVisibility => HexColorVisibility(EditorColorThree);

    public Visibility EditorColorFourVisibility => HexColorVisibility(EditorColorFour);

    public string WheelColor
    {
        get => selectedColorStopIndex switch
        {
            1 => EditorColorTwoDisplay,
            2 => EditorColorThreeDisplay,
            3 => EditorColorFourDisplay,
            _ => EditorColorDisplay,
        };
        set
        {
            string normalized = value?.Trim().ToUpperInvariant() ?? string.Empty;
            if (!IsHexColor(normalized))
            {
                return;
            }

            switch (selectedColorStopIndex)
            {
                case 1:
                    EditorColorTwo = normalized;
                    break;
                case 2:
                    EditorColorThree = normalized;
                    break;
                case 3:
                    EditorColorFour = normalized;
                    break;
                default:
                    EditorColor = normalized;
                    break;
            }
        }
    }

    public string ColorStopPositionText
    {
        get
        {
            int count = EditorPaletteColors().Length;
            return $"COLOR {Math.Min(selectedColorStopIndex + 1, count)} OF {count}";
        }
    }

    public string CurrentColorProfileName => SavedColorPalettes.FirstOrDefault(
        palette => ColorsMatch(palette.Colors, EditorPaletteColors()))?.Name.ToUpperInvariant() ??
        "CUSTOM COLORWAY";

    public string StealthButtonText => stealthEnabled ? "TURN STEALTH OFF" : "TURN STEALTH ON";

    public string LanternButtonText => lanternEnabled ? "TURN LANTERN OFF" : "TURN LANTERN ON";

    internal async Task InitializeAsync()
    {
        await LoadPreferencesAsync(CancellationToken.None);
        await RunUserOperationAsync(
            async cancellationToken =>
            {
                if (demoMode)
                {
                    IReadOnlyList<DeviceCandidate> candidates = await controller.ScanAsync(
                        TimeSpan.FromMilliseconds(1),
                        cancellationToken);
                    SelectedCandidate = candidates[0];
                    await ConnectAsync(cancellationToken);
                    return;
                }

                await ScanAsync(cancellationToken);
            },
            CancellationToken.None);
    }

    internal async Task<bool> HandleShortcutAsync(Key key)
    {
        if (CurrentPage != AppPage.Home)
        {
            return false;
        }

        ICommand? command = key switch
        {
            _ when key == PreviousProfileKey => PreviousProfileCommand,
            _ when key == NextProfileKey => NextProfileCommand,
            _ when key == TemperatureBoostKey => BoostTemperatureCommand,
            _ when key == TimeBoostKey => BoostTimeCommand,
            _ => null,
        };
        if (command is not null)
        {
            if (command.CanExecute(null))
            {
                command.Execute(null);
            }

            return true;
        }

        int? profileIndex = profileMacros
            .Where(item => item.Value == key)
            .Select(item => (int?)item.Key)
            .FirstOrDefault();
        if (profileIndex is null)
        {
            return false;
        }

        if (userOperationBusy || !CanEditDevice)
        {
            return true;
        }

        try
        {
            await RunUserOperationAsync(
                token => SelectProfileAsync(profileIndex.Value, token),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        controller.SnapshotChanged -= ControllerSnapshotChanged;
        foreach (AsyncRelayCommand command in asyncCommands)
        {
            command.Cancel();
        }

        foreach (AsyncRelayCommand command in asyncCommands)
        {
            await command.DisposeAsync();
        }

        await StopPollingAsync();
    }

    private AsyncRelayCommand CreateAsync(
        Func<CancellationToken, Task> operation,
        Func<bool>? canExecute = null)
    {
        AsyncRelayCommand command = new(
            cancellationToken => RunUserOperationAsync(operation, cancellationToken),
            ShowError,
            () => !userOperationBusy && (canExecute?.Invoke() ?? true));
        asyncCommands.Add(command);
        return command;
    }

    private RelayCommand CreateRelay(Action operation, Func<bool>? canExecute = null)
    {
        RelayCommand command = new(operation, canExecute);
        relayCommands.Add(command);
        return command;
    }

    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        StatusText = "Scanning nearby Bluetooth devices…";
        IReadOnlyList<DeviceCandidate> found = await controller.ScanAsync(
            TimeSpan.FromSeconds(demoMode ? 0.1 : 6),
            cancellationToken);
        Candidates.Clear();
        foreach (DeviceCandidate candidate in found)
        {
            Candidates.Add(candidate);
        }

        SelectedCandidate = Candidates.FirstOrDefault();
        StatusText = Candidates.Count == 0
            ? "No compatible app-enabled device found"
            : $"Found {Candidates.Count} compatible device{(Candidates.Count == 1 ? string.Empty : "s")}";
    }

    private async Task HotSwapScanAsync(CancellationToken cancellationToken)
    {
        DeviceHandoffPolicy.EvaluateSource(snapshot).ThrowIfDenied();
        StatusText = "Scanning for nearby Peak e-rigs…";
        await StopPollingAsync();
        try
        {
            IReadOnlyList<DeviceCandidate> found = await controller.ScanAsync(
                TimeSpan.FromSeconds(demoMode ? 0.1 : 6),
                cancellationToken);
            HotSwapCandidates.Clear();
            foreach (DeviceCandidate candidate in found)
            {
                if (!string.Equals(candidate.PlatformId, connectedCandidate?.PlatformId, StringComparison.Ordinal) &&
                    DeviceHandoffPolicy.EvaluateCandidate(candidate).IsAllowed)
                {
                    HotSwapCandidates.Add(candidate);
                }
            }

            SelectedHotSwapCandidate = HotSwapCandidates.FirstOrDefault();
            StatusText = HotSwapCandidates.Count == 0
                ? "No other nearby Peak e-rig found"
                : $"Found {HotSwapCandidates.Count} safe handoff target{(HotSwapCandidates.Count == 1 ? string.Empty : "s")}";
        }
        finally
        {
            if (IsConnected)
            {
                StartPolling();
            }
        }
    }

    private async Task HotSwapConnectAsync(CancellationToken cancellationToken)
    {
        DeviceHandoffPolicy.EvaluateSource(snapshot).ThrowIfDenied();
        DeviceCandidate candidate = SelectedHotSwapCandidate ??
            throw new DeviceSafetyException("Select a nearby Peak e-rig before handoff.");
        DeviceHandoffPolicy.EvaluateCandidate(candidate).ThrowIfDenied();

        StatusText = "Safely releasing the current e-rig…";
        await StopPollingAsync();
        try
        {
            await controller.DisconnectAsync(cancellationToken);
            ApplySnapshot(controller.Snapshot);
            SetDeviceProfiles([]);

            StatusText = "Pairing and authenticating the selected e-rig…";
            await controller.ConnectAsync(candidate, cancellationToken);
            ApplySnapshot(controller.Snapshot);
            DeviceHandoffPolicy.EvaluateDestination(snapshot).ThrowIfDenied();

            SetDeviceProfiles(await controller.GetProfilesAsync(cancellationToken));
            connectedCandidate = candidate;
            SelectedCandidate = candidate;
            LoadEditorFromActiveProfile();
            HotSwapCandidates.Clear();
            SelectedHotSwapCandidate = null;
            StatusText = snapshot.IsFirmwareVerified
                ? "Handoff complete • control enabled"
                : "Handoff complete • read-only safety mode";
        }
        catch
        {
            try
            {
                await controller.DisconnectAsync(CancellationToken.None);
            }
            finally
            {
                ApplySnapshot(controller.Snapshot);
                SetDeviceProfiles([]);
                connectedCandidate = null;
            }

            throw;
        }
        finally
        {
            if (IsConnected)
            {
                StartPolling();
            }
        }
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        DeviceCandidate candidate = SelectedCandidate ??
            throw new InvalidOperationException("Select a device before connecting.");
        StatusText = "Pairing and authenticating…";
        await controller.ConnectAsync(candidate, cancellationToken);
        ApplySnapshot(controller.Snapshot);
        connectedCandidate = candidate;
        SetDeviceProfiles(await controller.GetProfilesAsync(cancellationToken));
        LoadEditorFromActiveProfile();
        CurrentPage = AppPage.Home;
        StatusText = snapshot.IsFirmwareVerified
            ? "Connected • control enabled"
            : "Connected • read-only safety mode";
        StartPolling();
    }

    private async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await StopPollingAsync();
        await controller.DisconnectAsync(cancellationToken);
        SetDeviceProfiles([]);
        connectedCandidate = null;
        HotSwapCandidates.Clear();
        SelectedHotSwapCandidate = null;
        Candidates.Clear();
        SelectedCandidate = null;
        CurrentPage = AppPage.Home;
        StatusText = "Disconnected safely";
    }

    private async Task StartStopAsync(CancellationToken cancellationToken)
    {
        if (snapshot.IsHeating)
        {
            await controller.StopAsync(cancellationToken);
            StatusText = "Heat cycle stopped";
        }
        else
        {
            await controller.StartAsync(cancellationToken);
            StatusText = "Heat cycle started";
        }

        await controller.RefreshAsync(cancellationToken);
    }

    private async Task SelectRelativeProfileAsync(int offset, CancellationToken cancellationToken)
    {
        int profileIndex = (snapshot.ActiveProfileIndex + offset + profiles.Count) % profiles.Count;
        await SelectProfileAsync(profileIndex, cancellationToken);
    }

    private async Task SelectProfileAsync(int profileIndex, CancellationToken cancellationToken)
    {
        if (profiles.All(profile => profile.Index != profileIndex))
        {
            throw new InvalidOperationException("The macro points to a profile that is not available on this device.");
        }

        await controller.SelectProfileAsync(profileIndex, cancellationToken);
        ApplySnapshot(controller.Snapshot);
        LoadEditorFromActiveProfile();
        StatusText = $"Selected {snapshot.ActiveProfileName}";
    }

    private async Task SelectSelectedDeviceProfileAsync(CancellationToken cancellationToken)
    {
        DeviceProfileOption selected = SelectedDeviceProfile ??
            throw new InvalidOperationException("Select a device profile first.");
        try
        {
            await SelectProfileAsync(selected.Index, cancellationToken);
        }
        finally
        {
            SyncSelectedDeviceProfile();
        }
    }

    private async Task SaveProfileAsync(CancellationToken cancellationToken)
    {
        HeatProfile updated = BuildEditorProfile();
        await controller.UpdateProfileAsync(updated, cancellationToken);
        SetDeviceProfiles(profiles.Select(
            profile => profile.Index == updated.Index ? updated : profile).ToArray());
        StatusText = $"Saved {updated.Name}";
    }

    private async Task LoadPreferencesAsync(CancellationToken cancellationToken)
    {
        UserPreferences preferences = await UserPreferencesStore.LoadAsync(cancellationToken);
        string savedAccentHex = preferences.AppAccentHex?.Trim() ?? string.Empty;
        string normalizedAccentHex = savedAccentHex.ToUpperInvariant();
        appAccentHex = IsHexColor(normalizedAccentHex) &&
            !string.Equals(normalizedAccentHex, LegacyDefaultAppAccentHex, StringComparison.Ordinal)
                ? normalizedAccentHex
                : DefaultAppAccentHex;
        OnPropertyChanged(nameof(AppAccentHex));
        ApplyAppAccent(appAccentHex);

        Key[] shortcutKeys =
        [
            ParsePreferenceKey(preferences.PreviousProfileKey, Key.Left),
            ParsePreferenceKey(preferences.NextProfileKey, Key.Right),
            ParsePreferenceKey(preferences.TemperatureBoostKey, Key.Up),
            ParsePreferenceKey(preferences.TimeBoostKey, Key.Down),
        ];
        if (shortcutKeys.Distinct().Count() != shortcutKeys.Length)
        {
            shortcutKeys = [Key.Left, Key.Right, Key.Up, Key.Down];
        }

        previousProfileKey = shortcutKeys[0];
        nextProfileKey = shortcutKeys[1];
        temperatureBoostKey = shortcutKeys[2];
        timeBoostKey = shortcutKeys[3];
        OnPropertyChanged(nameof(PreviousProfileKey));
        OnPropertyChanged(nameof(NextProfileKey));
        OnPropertyChanged(nameof(TemperatureBoostKey));
        OnPropertyChanged(nameof(TimeBoostKey));
        OnPropertyChanged(nameof(PreviousProfileShortcutText));
        OnPropertyChanged(nameof(NextProfileShortcutText));
        OnPropertyChanged(nameof(TemperatureBoostShortcutText));
        OnPropertyChanged(nameof(TimeBoostShortcutText));

        double savedTemperatureCelsius = preferences.QuickHitTemperatureCelsius;
        if (!double.IsFinite(savedTemperatureCelsius) || savedTemperatureCelsius is <= 0 or > 30)
        {
            savedTemperatureCelsius = 5;
        }

        double savedTimeSeconds = preferences.QuickHitTimeSeconds;
        if (!double.IsFinite(savedTimeSeconds) || savedTimeSeconds is <= 0 or > 120)
        {
            savedTimeSeconds = 10;
        }

        QuickHitTemperature = ToDisplayTemperatureDelta(savedTemperatureCelsius);
        QuickHitTimeSeconds = savedTimeSeconds;

        profileMacros.Clear();
        HashSet<Key> assignedKeys = [.. shortcutKeys];
        foreach ((int profileIndex, string savedKey) in preferences.ProfileMacros.OrderBy(item => item.Key))
        {
            if (profileIndex is < 0 or > 3 ||
                !Enum.TryParse(savedKey, ignoreCase: true, out Key key) ||
                !IsAllowedShortcutKey(key) ||
                !assignedKeys.Add(key))
            {
                continue;
            }

            profileMacros[profileIndex] = key;
        }

        await LoadLocalProfileLibraryAsync(preferences, cancellationToken);

        NotifyProfileCarouselProperties();
    }

    private async Task LoadLocalProfileLibraryAsync(
        UserPreferences preferences,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<StoredLocalProfile<LocalColorProfile>> storedColors =
            await profileLibrary.LoadColorsAsync(cancellationToken);
        if (storedColors.Count == 0 && preferences.SavedColorPalettes is { Length: > 0 } legacyColors)
        {
            foreach (SavedColorPalettePreference legacy in legacyColors)
            {
                string name = legacy.Name?.Trim() ?? string.Empty;
                string[] colors = (legacy.Colors ?? [])
                    .Select(color => color?.Trim().ToUpperInvariant() ?? string.Empty)
                    .ToArray();
                if (name.Length is < 1 or > 64 ||
                    colors.Length is < 1 or > 4 ||
                    colors.Any(color => !IsHexColor(color)))
                {
                    continue;
                }

                try
                {
                    await profileLibrary.SaveColorAsync(
                        new LocalColorProfile { Name = name, Colors = colors },
                        existingFileName: null,
                        cancellationToken);
                }
                catch (InvalidDataException)
                {
                }
            }

            storedColors = await profileLibrary.LoadColorsAsync(cancellationToken);
        }

        SavedColorPalettes.Clear();
        foreach (StoredLocalProfile<LocalColorProfile> stored in storedColors)
        {
            SavedColorPalettes.Add(new ColorPaletteOption(
                stored.Profile.Name,
                stored.Profile.Colors,
                stored.FileName));
        }

        SelectedSavedColorPalette = SavedColorPalettes.FirstOrDefault();

        IReadOnlyList<StoredLocalProfile<LocalHeatingProfile>> storedHeating =
            await profileLibrary.LoadHeatingAsync(cancellationToken);
        if (storedHeating.Count == 0 && preferences.SavedHeatingProfiles is { Length: > 0 } legacyHeating)
        {
            foreach (SavedHeatingProfilePreference legacy in legacyHeating)
            {
                string name = legacy.Name?.Trim() ?? string.Empty;
                string deviceProfileName = legacy.DeviceProfileName?.Trim() ?? string.Empty;
                string colorProfileName = legacy.ColorProfileName?.Trim() ?? string.Empty;
                string[] colors = (legacy.Colors ?? [])
                    .Select(color => color?.Trim().ToUpperInvariant() ?? string.Empty)
                    .ToArray();
                if (name.Length is < 1 or > 64 ||
                    deviceProfileName.Length is < 1 or > 31 ||
                    colorProfileName.Length is < 1 or > 64 ||
                    !Enum.TryParse(legacy.Vapor, ignoreCase: true, out VaporLevel vapor))
                {
                    continue;
                }

                LocalHeatingProfile migrated = new()
                {
                    Name = name,
                    DeviceProfileName = deviceProfileName,
                    TargetTemperatureCelsius = legacy.TargetTemperatureCelsius,
                    DurationSeconds = legacy.DurationSeconds,
                    Vapor = vapor,
                    BoostTemperatureCelsius = legacy.BoostTemperatureCelsius,
                    BoostDurationSeconds = legacy.BoostDurationSeconds,
                    ColorProfileName = colorProfileName,
                    Colors = colors,
                };
                try
                {
                    await profileLibrary.SaveHeatingAsync(
                        migrated,
                        existingFileName: null,
                        cancellationToken);
                }
                catch (InvalidDataException)
                {
                }
            }

            storedHeating = await profileLibrary.LoadHeatingAsync(cancellationToken);
        }

        SavedHeatingProfiles.Clear();
        foreach (StoredLocalProfile<LocalHeatingProfile> stored in storedHeating)
        {
            LocalHeatingProfile saved = stored.Profile;
            HeatProfile candidate = new(
                0,
                saved.DeviceProfileName,
                saved.TargetTemperatureCelsius,
                TimeSpan.FromSeconds(saved.DurationSeconds),
                saved.Vapor,
                saved.BoostTemperatureCelsius,
                TimeSpan.FromSeconds(saved.BoostDurationSeconds),
                saved.Colors[0])
            {
                ColorPalette = saved.Colors,
            };
            if (!DeviceSafetyPolicy.ValidateProfileConfiguration(
                    candidate,
                    PreferenceFallbackLimits,
                    ChamberKind.ThreeDXL).IsAllowed)
            {
                continue;
            }

            SavedHeatingProfiles.Add(new HeatingProfileOption(
                saved.Name,
                saved.DeviceProfileName,
                saved.TargetTemperatureCelsius,
                saved.DurationSeconds,
                saved.Vapor,
                saved.BoostTemperatureCelsius,
                saved.BoostDurationSeconds,
                SavedColorPalettes.FirstOrDefault(
                    palette => ColorsMatch(palette.Colors, saved.Colors))?.Name ?? saved.ColorProfileName,
                saved.Colors,
                stored.FileName));
        }

        SelectedSavedHeatingProfile = SavedHeatingProfiles.FirstOrDefault();
        OnPropertyChanged(nameof(CurrentColorProfileName));
    }

    private async Task ReloadLocalProfilesAsync(CancellationToken cancellationToken)
    {
        await LoadLocalProfileLibraryAsync(new UserPreferences(), cancellationToken);
        StatusText = $"Reloaded {SavedColorPalettes.Count} color and {SavedHeatingProfiles.Count} heating profiles from JSON";
    }

    private async Task SavePreferencesAsync(CancellationToken cancellationToken)
    {
        string normalizedAccentHex = AppAccentHex.Trim().ToUpperInvariant();
        if (!IsHexColor(normalizedAccentHex))
        {
            throw new InvalidOperationException("The app accent must be a six-digit RGB hex color such as #2878FF.");
        }

        AppAccentHex = normalizedAccentHex;

        Key[] shortcutKeys =
        [
            PreviousProfileKey,
            NextProfileKey,
            TemperatureBoostKey,
            TimeBoostKey,
        ];
        if (shortcutKeys.Any(key => !IsAllowedShortcutKey(key)))
        {
            throw new InvalidOperationException("Choose a supported key for every shortcut.");
        }

        if (shortcutKeys.Distinct().Count() != shortcutKeys.Length)
        {
            throw new InvalidOperationException("Each shortcut must use a different key.");
        }

        if (profileMacros.Values.Distinct().Count() != profileMacros.Count ||
            profileMacros.Values.Any(shortcutKeys.Contains))
        {
            throw new InvalidOperationException("Profile macros cannot duplicate another shortcut or macro.");
        }

        double quickHitTemperatureCelsius = FromDisplayTemperatureDelta(QuickHitTemperature);
        TimeSpan quickHitDuration = TimeSpan.FromSeconds(QuickHitTimeSeconds);
        DeviceSafetyPolicy.ValidateBoostConfiguration(
            quickHitTemperatureCelsius,
            quickHitDuration,
            snapshot.Limits ?? PreferenceFallbackLimits).ThrowIfDenied();

        UserPreferences preferences = new()
        {
            AppAccentHex = normalizedAccentHex,
            PreviousProfileKey = PreviousProfileKey.ToString(),
            NextProfileKey = NextProfileKey.ToString(),
            TemperatureBoostKey = TemperatureBoostKey.ToString(),
            TimeBoostKey = TimeBoostKey.ToString(),
            QuickHitTemperatureCelsius = quickHitTemperatureCelsius,
            QuickHitTimeSeconds = QuickHitTimeSeconds,
            ProfileMacros = profileMacros.ToDictionary(item => item.Key, item => item.Value.ToString()),
        };
        await UserPreferencesStore.SaveAsync(preferences, cancellationToken);
        StatusText = "Appearance, controls, and macros saved";
    }

    private async Task SaveColorPaletteAsync(CancellationToken cancellationToken)
    {
        string name = PaletteName.Trim();
        if (name.Length is < 1 or > 64)
        {
            throw new InvalidOperationException("Color profile names must contain 1 to 64 characters.");
        }

        string[] colors = EditorPaletteColors();
        if (colors.Length is < 1 or > 4 || colors.Any(color => !IsHexColor(color)))
        {
            throw new InvalidOperationException("A saved palette requires one to four six-digit RGB colors.");
        }

        int existingIndex = SavedColorPalettes
            .Select((item, index) => (item, index))
            .Where(pair => string.Equals(pair.item.Name, name, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.index)
            .DefaultIfEmpty(-1)
            .First();
        ColorPaletteOption? existing = existingIndex >= 0
            ? SavedColorPalettes[existingIndex]
            : null;
        StoredLocalProfile<LocalColorProfile> stored = await profileLibrary.SaveColorAsync(
            new LocalColorProfile { Name = name, Colors = colors },
            existing?.StorageFileName,
            cancellationToken);
        ColorPaletteOption palette = new(
            stored.Profile.Name,
            stored.Profile.Colors,
            stored.FileName);
        if (existingIndex >= 0)
        {
            SavedColorPalettes[existingIndex] = palette;
        }
        else
        {
            SavedColorPalettes.Add(palette);
        }

        SelectedSavedColorPalette = palette;
        RefreshHeatingProfileColorLinks();
        OnPropertyChanged(nameof(CurrentColorProfileName));
        StatusText = $"Saved color profile {name} to its JSON file";
    }

    private void ApplySelectedColorPalette()
    {
        ColorPaletteOption palette = SelectedSavedColorPalette ??
            throw new InvalidOperationException("Select a saved palette first.");
        SetEditorPalette(palette.Colors);
        StatusText = $"Loaded {palette.Name} • save the profile to send it to the device";
    }

    private async Task DeleteColorPaletteAsync(CancellationToken cancellationToken)
    {
        ColorPaletteOption palette = SelectedSavedColorPalette ??
            throw new InvalidOperationException("Select a saved palette first.");
        await profileLibrary.DeleteColorAsync(palette.StorageFileName, cancellationToken);
        SavedColorPalettes.Remove(palette);
        SelectedSavedColorPalette = SavedColorPalettes.FirstOrDefault();
        RefreshHeatingProfileColorLinks();
        OnPropertyChanged(nameof(CurrentColorProfileName));
        StatusText = $"Deleted color profile {palette.Name}";
    }

    private async Task SaveHeatingProfileAsync(CancellationToken cancellationToken)
    {
        string name = HeatingProfileName.Trim();
        if (name.Length is < 1 or > 64)
        {
            throw new InvalidOperationException("Heating profile names must contain 1 to 64 characters.");
        }

        HeatProfile editorProfile = BuildEditorProfile();
        DeviceSafetyPolicy.ValidateProfileConfiguration(
            editorProfile,
            snapshot.Limits ?? PreferenceFallbackLimits,
            snapshot.Chamber).ThrowIfDenied();

        string[] colors = editorProfile.ColorPalette.ToArray();
        ColorPaletteOption? linkedPalette = SavedColorPalettes.FirstOrDefault(
            palette => ColorsMatch(palette.Colors, colors));
        string colorProfileName = linkedPalette?.Name ?? "Custom colorway";
        int existingIndex = SavedHeatingProfiles
            .Select((item, index) => (item, index))
            .Where(pair => string.Equals(pair.item.Name, name, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.index)
            .DefaultIfEmpty(-1)
            .First();
        HeatingProfileOption? existing = existingIndex >= 0
            ? SavedHeatingProfiles[existingIndex]
            : null;
        StoredLocalProfile<LocalHeatingProfile> stored = await profileLibrary.SaveHeatingAsync(
            new LocalHeatingProfile
            {
                Name = name,
                DeviceProfileName = editorProfile.Name,
                TargetTemperatureCelsius = editorProfile.TargetTemperatureCelsius,
                DurationSeconds = editorProfile.Duration.TotalSeconds,
                Vapor = editorProfile.Vapor,
                BoostTemperatureCelsius = editorProfile.BoostTemperatureCelsius,
                BoostDurationSeconds = editorProfile.BoostDuration.TotalSeconds,
                ColorProfileName = colorProfileName,
                Colors = colors,
            },
            existing?.StorageFileName,
            cancellationToken);
        HeatingProfileOption savedProfile = new(
            stored.Profile.Name,
            stored.Profile.DeviceProfileName,
            stored.Profile.TargetTemperatureCelsius,
            stored.Profile.DurationSeconds,
            stored.Profile.Vapor,
            stored.Profile.BoostTemperatureCelsius,
            stored.Profile.BoostDurationSeconds,
            stored.Profile.ColorProfileName,
            stored.Profile.Colors,
            stored.FileName);
        if (existingIndex >= 0)
        {
            SavedHeatingProfiles[existingIndex] = savedProfile;
        }
        else
        {
            SavedHeatingProfiles.Add(savedProfile);
        }

        SelectedSavedHeatingProfile = savedProfile;
        StatusText = $"Saved heating profile {name} with {colorProfileName} to JSON";
    }

    private Task ApplySelectedHeatingProfileAsync(CancellationToken cancellationToken)
    {
        HeatingProfileOption savedProfile = SelectedSavedHeatingProfile ??
            throw new InvalidOperationException("Select a local heating profile first.");
        HeatProfile profile = new(
            snapshot.ActiveProfileIndex,
            savedProfile.DeviceProfileName,
            savedProfile.TargetTemperatureCelsius,
            TimeSpan.FromSeconds(savedProfile.DurationSeconds),
            savedProfile.Vapor,
            savedProfile.BoostTemperatureCelsius,
            TimeSpan.FromSeconds(savedProfile.BoostDurationSeconds),
            savedProfile.Colors[0])
        {
            ColorPalette = savedProfile.Colors,
        };
        DeviceSafetyPolicy.ValidateProfileConfiguration(
            profile,
            snapshot.Limits ?? PreferenceFallbackLimits,
            snapshot.Chamber).ThrowIfDenied();

        EditorName = profile.Name;
        EditorTemperature = ToDisplayTemperature(profile.TargetTemperatureCelsius);
        EditorDurationSeconds = profile.Duration.TotalSeconds;
        EditorVapor = profile.Vapor;
        EditorBoostTemperature = ToDisplayTemperatureDelta(profile.BoostTemperatureCelsius);
        EditorBoostSeconds = profile.BoostDuration.TotalSeconds;
        SetEditorPalette(profile.ColorPalette);
        SelectedSavedColorPalette = SavedColorPalettes.FirstOrDefault(
            palette => ColorsMatch(palette.Colors, profile.ColorPalette));
        HeatingProfileName = savedProfile.Name;
        StatusText = $"Loaded {savedProfile.Name} with {savedProfile.ColorProfileName} • Save Profile writes it to the selected slot";
        return Task.CompletedTask;
    }

    private async Task DeleteHeatingProfileAsync(CancellationToken cancellationToken)
    {
        HeatingProfileOption savedProfile = SelectedSavedHeatingProfile ??
            throw new InvalidOperationException("Select a local heating profile first.");
        await profileLibrary.DeleteHeatingAsync(savedProfile.StorageFileName, cancellationToken);
        SavedHeatingProfiles.Remove(savedProfile);
        SelectedSavedHeatingProfile = SavedHeatingProfiles.FirstOrDefault();
        StatusText = $"Deleted local heating profile {savedProfile.Name}";
    }

    private void UseColorWithHeatingProfile()
    {
        string[] colors = EditorPaletteColors();
        if (colors.Length is < 1 or > 4 || colors.Any(color => !IsHexColor(color)))
        {
            StatusText = "Enter one to four valid six-digit RGB colors before returning to Profiles";
            return;
        }

        SelectedSavedColorPalette = SavedColorPalettes.FirstOrDefault(
            palette => ColorsMatch(palette.Colors, colors));
        CurrentPage = AppPage.Profiles;
        StatusText = SelectedSavedColorPalette is { } palette
            ? $"Using color profile {palette.Name} with the heating profile"
            : "Using this custom colorway with the heating profile";
    }

    private async Task BoostTemperatureAsync(CancellationToken cancellationToken)
    {
        double temperatureCelsius = FromDisplayTemperatureDelta(QuickHitTemperature);
        await controller.BoostTemperatureAsync(temperatureCelsius, cancellationToken);
        StatusText = $"{QuickTemperatureBoostText} temperature quick hit applied";
    }

    private async Task BoostTimeAsync(CancellationToken cancellationToken)
    {
        TimeSpan duration = TimeSpan.FromSeconds(QuickHitTimeSeconds);
        await controller.BoostTimeAsync(duration, cancellationToken);
        StatusText = $"{QuickTimeBoostText} time quick hit applied";
    }

    private async Task ToggleStealthAsync(CancellationToken cancellationToken)
    {
        bool updated = !stealthEnabled;
        await controller.SetStealthModeAsync(updated, cancellationToken);
        stealthEnabled = updated;
        OnPropertyChanged(nameof(StealthButtonText));
        StatusText = updated ? "Stealth mode enabled" : "Stealth mode disabled";
    }

    private async Task ToggleLanternAsync(CancellationToken cancellationToken)
    {
        bool updated = !lanternEnabled;
        await controller.SetLanternModeAsync(updated, cancellationToken);
        lanternEnabled = updated;
        OnPropertyChanged(nameof(LanternButtonText));
        StatusText = updated ? "Lantern mode enabled" : "Lantern mode disabled";
    }

    private void ToggleTemperatureUnit()
    {
        double celsius = FromDisplayTemperature(EditorTemperature);
        double boostCelsius = FromDisplayTemperatureDelta(EditorBoostTemperature);
        double quickHitCelsius = FromDisplayTemperatureDelta(QuickHitTemperature);
        UseFahrenheit = !UseFahrenheit;
        EditorTemperature = ToDisplayTemperature(celsius);
        EditorBoostTemperature = ToDisplayTemperatureDelta(boostCelsius);
        QuickHitTemperature = ToDisplayTemperatureDelta(quickHitCelsius);
    }

    private void LoadEditorFromActiveProfile()
    {
        HeatProfile? profile = profiles.FirstOrDefault(item => item.Index == snapshot.ActiveProfileIndex);
        if (profile is null)
        {
            return;
        }

        EditorName = profile.Name;
        EditorTemperature = ToDisplayTemperature(profile.TargetTemperatureCelsius);
        EditorDurationSeconds = profile.Duration.TotalSeconds;
        EditorVapor = profile.Vapor;
        EditorBoostTemperature = ToDisplayTemperatureDelta(profile.BoostTemperatureCelsius);
        EditorBoostSeconds = profile.BoostDuration.TotalSeconds;
        HeatingProfileName = profile.Name;
        SetEditorPalette(profile.ColorPalette.Count > 0 ? profile.ColorPalette : [profile.ColorHex]);
        editorProfileMacroKey = profileMacros.TryGetValue(profile.Index, out Key macroKey)
            ? macroKey
            : null;
        OnPropertyChanged(nameof(EditorProfileMacroKey));
        SyncSelectedDeviceProfile();
        NotifyProfileColorProperties();
        NotifyProfileCarouselProperties();
    }

    private void SetDeviceProfiles(IReadOnlyList<HeatProfile> updatedProfiles)
    {
        profiles = updatedProfiles;
        DeviceProfiles.Clear();
        foreach (HeatProfile profile in profiles.OrderBy(profile => profile.Index))
        {
            IReadOnlyList<string> colors = profile.ColorPalette.Count > 0
                ? profile.ColorPalette
                : [profile.ColorHex];
            DeviceProfiles.Add(new DeviceProfileOption(profile.Index, profile.Name, colors));
        }

        SyncSelectedDeviceProfile();
        NotifyProfileColorProperties();
        NotifyProfileCarouselProperties();
    }

    private void SyncSelectedDeviceProfile()
    {
        DeviceProfileOption? active = DeviceProfiles.FirstOrDefault(
            profile => profile.Index == snapshot.ActiveProfileIndex);
        if (!Equals(selectedDeviceProfile, active))
        {
            selectedDeviceProfile = active;
            OnPropertyChanged(nameof(SelectedDeviceProfile));
            NotifyCommandStates();
        }
    }

    private void StartPolling()
    {
        if (pollingTask is not null)
        {
            return;
        }

        pollingCancellation = new CancellationTokenSource();
        pollingTask = PollAsync(pollingCancellation.Token);
    }

    private async Task StopPollingAsync()
    {
        if (pollingCancellation is null || pollingTask is null)
        {
            return;
        }

        await pollingCancellation.CancelAsync();
        try
        {
            await pollingTask;
        }
        catch (OperationCanceledException)
        {
        }

        pollingCancellation.Dispose();
        pollingCancellation = null;
        pollingTask = null;
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(500));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                await controller.RefreshAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                RunOnUiThread(() => ShowError(exception));
                return;
            }
        }
    }

    private async Task RunUserOperationAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        if (userOperationBusy)
        {
            return;
        }

        userOperationBusy = true;
        NotifyCommandStates();
        try
        {
            await operation(cancellationToken);
        }
        finally
        {
            userOperationBusy = false;
            NotifyCommandStates();
        }
    }

    private void ControllerSnapshotChanged(object? sender, DeviceSnapshot updatedSnapshot) =>
        RunOnUiThread(() => ApplySnapshot(updatedSnapshot));

    private void ApplySnapshot(DeviceSnapshot updatedSnapshot)
    {
        bool activeProfileChanged = snapshot.ActiveProfileIndex != updatedSnapshot.ActiveProfileIndex;
        snapshot = updatedSnapshot;
        NotifySnapshotProperties();
        if (activeProfileChanged)
        {
            SyncSelectedDeviceProfile();
            NotifyProfileCarouselProperties();
        }

        NotifyCommandStates();
    }

    private void ShowError(Exception exception)
    {
        StatusText = exception is DeviceSafetyException
            ? $"Safety lock: {exception.Message}"
            : $"Error: {Sanitize(exception.Message)}";
    }

    private void NotifySnapshotProperties()
    {
        string[] properties =
        [
            nameof(IsConnected),
            nameof(IsDisconnected),
            nameof(IsReadOnly),
            nameof(IsHeating),
            nameof(CanEditDevice),
            nameof(CanStartOrStop),
            nameof(CanBoost),
            nameof(CanHotSwap),
            nameof(ConnectionVisibility),
            nameof(DeviceVisibility),
            nameof(DeviceName),
            nameof(DeviceModelText),
            nameof(ProfileName),
            nameof(VaporText),
            nameof(ChamberText),
            nameof(BatteryText),
            nameof(BatteryBlockOneOpacity),
            nameof(BatteryBlockTwoOpacity),
            nameof(BatteryBlockThreeOpacity),
            nameof(BatteryBlockFourOpacity),
            nameof(ActiveProfileColor),
            nameof(ProfileColorOne),
            nameof(ProfileColorTwo),
            nameof(ProfileColorThree),
            nameof(ProfileColorFour),
            nameof(ActiveProfileForegroundDisplay),
            nameof(HeaderBadgeText),
            nameof(TemperatureText),
            nameof(TemperatureCaption),
            nameof(SessionTimeText),
            nameof(StartStopText),
            nameof(OperatingStateText),
            nameof(FirmwareText),
            nameof(SafetyText),
        ];
        foreach (string property in properties)
        {
            OnPropertyChanged(property);
        }

        NotifyPageProperties();
    }

    private void NotifyPageProperties()
    {
        OnPropertyChanged(nameof(HomeVisibility));
        OnPropertyChanged(nameof(ProfilesVisibility));
        OnPropertyChanged(nameof(ColorVisibility));
        OnPropertyChanged(nameof(SettingsVisibility));
    }

    private void NotifyTemperatureProperties()
    {
        OnPropertyChanged(nameof(TemperatureText));
        OnPropertyChanged(nameof(TemperatureUnitText));
        OnPropertyChanged(nameof(TemperatureEditorLabel));
        OnPropertyChanged(nameof(BoostEditorLabel));
        OnPropertyChanged(nameof(QuickHitTemperatureLabel));
    }

    private void NotifyProfileColorProperties()
    {
        OnPropertyChanged(nameof(ActiveProfileColor));
        OnPropertyChanged(nameof(ProfileColorOne));
        OnPropertyChanged(nameof(ProfileColorTwo));
        OnPropertyChanged(nameof(ProfileColorThree));
        OnPropertyChanged(nameof(ProfileColorFour));
    }

    private void NotifyProfileCarouselProperties()
    {
        OnPropertyChanged(nameof(ActiveProfilePositionText));
        OnPropertyChanged(nameof(ProfileColorSourceText));
    }

    private IReadOnlyList<string> ActiveProfilePalette()
    {
        HeatProfile? profile = profiles.FirstOrDefault(item => item.Index == snapshot.ActiveProfileIndex);
        return profile?.ColorPalette is { Count: > 0 } colors
            ? colors
            : [ActiveProfileColor];
    }

    private string ProfileColorAt(int slot)
    {
        IReadOnlyList<string> colors = ActiveProfilePalette();
        return slot < colors.Count ? colors[slot] : colors[^1];
    }

    private string[] EditorPaletteColors() =>
    [
        EditorColor.Trim().ToUpperInvariant(),
        .. new[] { EditorColorTwo, EditorColorThree, EditorColorFour }
            .Where(color => !string.IsNullOrWhiteSpace(color))
            .Select(color => color.Trim().ToUpperInvariant()),
    ];

    private HeatProfile BuildEditorProfile()
    {
        HeatProfile current = profiles.Single(profile => profile.Index == snapshot.ActiveProfileIndex);
        string[] colorPalette = EditorPaletteColors();
        return current with
        {
            Name = EditorName.Trim(),
            TargetTemperatureCelsius = FromDisplayTemperature(EditorTemperature),
            Duration = TimeSpan.FromSeconds(EditorDurationSeconds),
            Vapor = EditorVapor,
            BoostTemperatureCelsius = FromDisplayTemperatureDelta(EditorBoostTemperature),
            BoostDuration = TimeSpan.FromSeconds(EditorBoostSeconds),
            ColorHex = colorPalette[0],
            ColorPalette = colorPalette,
            HasDeviceColor = true,
        };
    }

    private static bool ColorsMatch(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        left.Count == right.Count &&
        left.Zip(right).All(pair => string.Equals(
            pair.First,
            pair.Second,
            StringComparison.OrdinalIgnoreCase));

    private void RefreshHeatingProfileColorLinks()
    {
        string? selectedName = SelectedSavedHeatingProfile?.Name;
        for (int index = 0; index < SavedHeatingProfiles.Count; index++)
        {
            HeatingProfileOption profile = SavedHeatingProfiles[index];
            string linkedName = SavedColorPalettes.FirstOrDefault(
                palette => ColorsMatch(palette.Colors, profile.Colors))?.Name ?? "Custom colorway";
            if (!string.Equals(profile.ColorProfileName, linkedName, StringComparison.Ordinal))
            {
                SavedHeatingProfiles[index] = profile with { ColorProfileName = linkedName };
            }
        }

        SelectedSavedHeatingProfile = SavedHeatingProfiles.FirstOrDefault(
            profile => string.Equals(profile.Name, selectedName, StringComparison.OrdinalIgnoreCase));
    }

    private void SetEditorPalette(IReadOnlyList<string> colors)
    {
        EditorColor = colors.ElementAtOrDefault(0) ?? string.Empty;
        EditorColorTwo = colors.ElementAtOrDefault(1) ?? string.Empty;
        EditorColorThree = colors.ElementAtOrDefault(2) ?? string.Empty;
        EditorColorFour = colors.ElementAtOrDefault(3) ?? string.Empty;
        selectedColorStopIndex = Math.Clamp(selectedColorStopIndex, 0, Math.Max(0, colors.Count - 1));
        NotifyColorEditorProperties();
    }

    private void SelectRelativeColorStop(int offset)
    {
        int count = EditorPaletteColors().Length;
        selectedColorStopIndex = (selectedColorStopIndex + offset + count) % count;
        NotifyColorEditorProperties();
    }

    private void AddColorStop()
    {
        List<string> colors = [.. EditorPaletteColors()];
        if (colors.Count >= 4)
        {
            return;
        }

        colors.Add(WheelColor);
        selectedColorStopIndex = colors.Count - 1;
        SetEditorPalette(colors);
        StatusText = $"Added color {colors.Count} to the colorway";
    }

    private void RemoveColorStop()
    {
        List<string> colors = [.. EditorPaletteColors()];
        if (colors.Count <= 1)
        {
            return;
        }

        colors.RemoveAt(selectedColorStopIndex);
        selectedColorStopIndex = Math.Min(selectedColorStopIndex, colors.Count - 1);
        SetEditorPalette(colors);
        StatusText = $"Colorway now contains {colors.Count} colors";
    }

    private void NotifyColorEditorProperties()
    {
        OnPropertyChanged(nameof(WheelColor));
        OnPropertyChanged(nameof(ColorStopPositionText));
        OnPropertyChanged(nameof(CurrentColorProfileName));
        OnPropertyChanged(nameof(EditorPaletteForegroundDisplay));
        NotifyCommandStates();
    }

    private void SetEditorColor(
        ref string storage,
        string value,
        string propertyName,
        string displayPropertyName)
    {
        if (SetProperty(ref storage, value ?? string.Empty, propertyName))
        {
            OnPropertyChanged(displayPropertyName);
            OnPropertyChanged(nameof(EditorColorTwoVisibility));
            OnPropertyChanged(nameof(EditorColorThreeVisibility));
            OnPropertyChanged(nameof(EditorColorFourVisibility));
            OnPropertyChanged(nameof(WheelColor));
            OnPropertyChanged(nameof(ColorStopPositionText));
            OnPropertyChanged(nameof(CurrentColorProfileName));
            OnPropertyChanged(nameof(EditorPaletteForegroundDisplay));
            NotifyCommandStates();
        }
    }

    private static string ColorDisplayOrFallback(string color, string fallback) =>
        IsHexColor(color) ? color : fallback;

    private static Visibility HexColorVisibility(string color) =>
        IsHexColor(color) ? Visibility.Visible : Visibility.Collapsed;

    private static void ApplyAppAccent(string colorHex)
    {
        byte red = byte.Parse(colorHex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte green = byte.Parse(colorHex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte blue = byte.Parse(colorHex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        Application? application = Application.Current;
        if (application is null)
        {
            return;
        }

        application.Resources["AccentBrush"] = new SolidColorBrush(Color.FromRgb(red, green, blue));

        int perceivedBrightness = ((red * 299) + (green * 587) + (blue * 114)) / 1000;
        Color foreground = perceivedBrightness >= 150
            ? Color.FromRgb(11, 23, 21)
            : Color.FromRgb(246, 247, 249);
        application.Resources["AccentForegroundBrush"] = new SolidColorBrush(foreground);
    }

    private static bool IsHexColor(string color) =>
        color.Length == 7 &&
        color[0] == '#' &&
        uint.TryParse(
            color.AsSpan(1),
            NumberStyles.AllowHexSpecifier,
            CultureInfo.InvariantCulture,
            out _);

    private static Key ParsePreferenceKey(string? value, Key fallback) =>
        Enum.TryParse(value, ignoreCase: true, out Key key) && IsAllowedShortcutKey(key)
            ? key
            : fallback;

    private static bool IsAllowedShortcutKey(Key key) =>
        AvailableShortcutOptions.Any(option => option.Value == key);

    private static string KeyLabel(Key key) =>
        AvailableShortcutOptions.First(option => option.Value == key).Label;

    private void SetShortcutKey(
        ref Key storage,
        Key value,
        string propertyName,
        string labelPropertyName)
    {
        if (storage == value)
        {
            return;
        }

        bool usedByAnotherShortcut =
            (propertyName != nameof(PreviousProfileKey) && PreviousProfileKey == value) ||
            (propertyName != nameof(NextProfileKey) && NextProfileKey == value) ||
            (propertyName != nameof(TemperatureBoostKey) && TemperatureBoostKey == value) ||
            (propertyName != nameof(TimeBoostKey) && TimeBoostKey == value);
        if (!IsAllowedShortcutKey(value) || usedByAnotherShortcut || profileMacros.ContainsValue(value))
        {
            StatusText = "That key is already assigned to another shortcut";
            OnPropertyChanged(propertyName);
            return;
        }

        storage = value;
        OnPropertyChanged(propertyName);
        OnPropertyChanged(labelPropertyName);
    }

    private bool IsGlobalShortcutKey(Key key) =>
        PreviousProfileKey == key ||
        NextProfileKey == key ||
        TemperatureBoostKey == key ||
        TimeBoostKey == key;

    private void NotifyCommandStates()
    {
        foreach (AsyncRelayCommand command in asyncCommands)
        {
            command.NotifyCanExecuteChanged();
        }

        foreach (RelayCommand command in relayCommands)
        {
            command.NotifyCanExecuteChanged();
        }
    }

    private Visibility PageVisibility(AppPage page) =>
        IsConnected && CurrentPage == page ? Visibility.Visible : Visibility.Collapsed;

    private double ToDisplayTemperature(double celsius) =>
        UseFahrenheit ? ((celsius * 9) / 5) + 32 : celsius;

    private double FromDisplayTemperature(double display) =>
        UseFahrenheit ? ((display - 32) * 5) / 9 : display;

    private double ToDisplayTemperatureDelta(double celsius) =>
        UseFahrenheit ? (celsius * 9) / 5 : celsius;

    private double FromDisplayTemperatureDelta(double display) =>
        UseFahrenheit ? (display * 5) / 9 : display;

    private double BatteryBlockOpacity(int block)
    {
        double threshold = block == 1 ? 1 : (block - 1) * 25;
        return snapshot.BatteryPercent >= threshold ? 1 : 0.18;
    }

    private static string Sanitize(string message)
    {
        string oneLine = message.Replace('\r', ' ').Replace('\n', ' ');
        return oneLine.Length <= 160 ? oneLine : oneLine[..160];
    }

    private static void RunOnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
