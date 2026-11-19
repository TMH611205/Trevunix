namespace Trevunix.Core.Tweaks;

/// <summary>The built-in tweaks. Every one is undoable through <see cref="TweakEngine"/>.</summary>
public static class TweakCatalog
{
    private const string CurrentVersion = @"HKCU\Software\Microsoft\Windows\CurrentVersion";
    private const string ContentDelivery = CurrentVersion + @"\ContentDeliveryManager";
    private const string ExplorerAdvanced = CurrentVersion + @"\Explorer\Advanced";
    private const string Policies = @"HKLM\SOFTWARE\Policies\Microsoft\Windows";
    private const string UserPolicies = @"HKCU\Software\Policies\Microsoft\Windows";
    private const string Personalize = CurrentVersion + @"\Themes\Personalize";
    private const string UserClsid = @"HKCU\Software\Classes\CLSID";

    /// <summary>Categories shown on the Appearance page instead of the Optimize page.</summary>
    public static bool IsAppearance(TweakCategory category) =>
        category is TweakCategory.Appearance or TweakCategory.Taskbar or TweakCategory.FileExplorer;

    public static IReadOnlyList<Tweak> All { get; } =
    [
        // Privacy
        new(
            "AdvertisingId",
            TweakCategory.AdsAndSuggestions,
            TweakRisk.Recommended,
            [
                Dword(CurrentVersion + @"\AdvertisingInfo", "Enabled", 0),
                Dword(Policies + @"\AdvertisingInfo", "DisabledByGroupPolicy", 1),
            ]
        ),
        new(
            "Telemetry",
            TweakCategory.DataCollection,
            TweakRisk.Recommended,
            [
                Dword(Policies + @"\DataCollection", "AllowTelemetry", 0),
                Dword(Policies + @"\DataCollection", "DoNotShowFeedbackNotifications", 1),
                new ServiceStartAction("DiagTrack", ServiceStartAction.Disabled),
                new ServiceStartAction("dmwappushservice", ServiceStartAction.Disabled),
            ],
            TweakAfter.Reboot
        ),
        new(
            "TailoredExperiences",
            TweakCategory.AdsAndSuggestions,
            TweakRisk.Recommended,
            [Dword(CurrentVersion + @"\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0)]
        ),
        new(
            "ActivityHistory",
            TweakCategory.DataCollection,
            TweakRisk.Recommended,
            [
                Dword(Policies + @"\System", "EnableActivityFeed", 0),
                Dword(Policies + @"\System", "PublishUserActivities", 0),
                Dword(Policies + @"\System", "UploadUserActivities", 0),
            ]
        ),
        new(
            "StartSuggestions",
            TweakCategory.AdsAndSuggestions,
            TweakRisk.Recommended,
            [
                Dword(ContentDelivery, "SystemPaneSuggestionsEnabled", 0),
                Dword(ContentDelivery, "SubscribedContent-338388Enabled", 0),
                Dword(ContentDelivery, "SubscribedContent-338389Enabled", 0),
                Dword(ContentDelivery, "SubscribedContent-353694Enabled", 0),
                Dword(ContentDelivery, "SubscribedContent-353696Enabled", 0),
                Dword(ContentDelivery, "SilentInstalledAppsEnabled", 0),
                Dword(ContentDelivery, "SoftLandingEnabled", 0),
                Dword(ExplorerAdvanced, "Start_IrisRecommendations", 0),
            ]
        ),
        new(
            "WebSearch",
            TweakCategory.AdsAndSuggestions,
            TweakRisk.Recommended,
            [
                Dword(UserPolicies + @"\Explorer", "DisableSearchBoxSuggestions", 1),
                Dword(CurrentVersion + @"\Search", "BingSearchEnabled", 0),
            ],
            TweakAfter.RestartExplorer
        ),
        new(
            "FeedbackRequests",
            TweakCategory.DataCollection,
            TweakRisk.Recommended,
            [
                Dword(@"HKCU\Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0),
                Dword(@"HKCU\Software\Microsoft\Siuf\Rules", "PeriodInNanoSeconds", 0),
            ]
        ),
        new(
            "ExperienceTasks",
            TweakCategory.DataCollection,
            TweakRisk.Optional,
            [
                new ScheduledTaskAction(
                    @"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator"
                ),
                new ScheduledTaskAction(
                    @"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip"
                ),
                new ScheduledTaskAction(
                    @"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser"
                ),
                new ScheduledTaskAction(
                    @"\Microsoft\Windows\Application Experience\ProgramDataUpdater"
                ),
                new ScheduledTaskAction(@"\Microsoft\Windows\Feedback\Siuf\DmClient"),
            ]
        ),
        new(
            "Copilot",
            TweakCategory.Ai,
            TweakRisk.Optional,
            [
                Dword(UserPolicies + @"\WindowsCopilot", "TurnOffWindowsCopilot", 1),
                Dword(ExplorerAdvanced, "ShowCopilotButton", 0),
            ],
            TweakAfter.RestartExplorer,
            MinBuild: 22621
        ),
        new(
            "RecallSnapshots",
            TweakCategory.Ai,
            TweakRisk.Recommended,
            [Dword(UserPolicies + @"\WindowsAI", "DisableAIDataAnalysis", 1)],
            MinBuild: 26100
        ),
        new(
            "RecallFeature",
            TweakCategory.Ai,
            TweakRisk.Optional,
            [Dword(Policies + @"\WindowsAI", "AllowRecallEnablement", 0)],
            TweakAfter.Reboot,
            MinBuild: 26100
        ),
        new(
            "ClickToDo",
            TweakCategory.Ai,
            TweakRisk.Optional,
            [Dword(UserPolicies + @"\WindowsAI", "DisableClickToDo", 1)],
            MinBuild: 26100
        ),
        new(
            "EdgeSidebar",
            TweakCategory.Ai,
            TweakRisk.Optional,
            [Dword(@"HKLM\SOFTWARE\Policies\Microsoft\Edge", "HubsSidebarEnabled", 0)]
        ),
        // Performance
        new(
            "StartupDelay",
            TweakCategory.Performance,
            TweakRisk.Recommended,
            [Dword(CurrentVersion + @"\Explorer\Serialize", "StartupDelayInMSec", 0)]
        ),
        new(
            "MenuDelay",
            TweakCategory.Performance,
            TweakRisk.Recommended,
            [Text(@"HKCU\Control Panel\Desktop", "MenuShowDelay", "100")],
            TweakAfter.RestartExplorer
        ),
        new(
            "BackgroundApps",
            TweakCategory.Performance,
            TweakRisk.Optional,
            [Dword(CurrentVersion + @"\BackgroundAccessApplications", "GlobalUserDisabled", 1)]
        ),
        new(
            "FastShutdown",
            TweakCategory.Performance,
            TweakRisk.Optional,
            [
                Text(@"HKCU\Control Panel\Desktop", "AutoEndTasks", "1"),
                Text(@"HKCU\Control Panel\Desktop", "WaitToKillAppTimeout", "3000"),
                Text(@"HKCU\Control Panel\Desktop", "HungAppTimeout", "3000"),
            ]
        ),
        new(
            "SearchIndexing",
            TweakCategory.Performance,
            TweakRisk.Optional,
            [new ServiceStartAction("WSearch", ServiceStartAction.Disabled)],
            TweakAfter.Reboot
        ),
        new(
            "XboxServices",
            TweakCategory.Performance,
            TweakRisk.Optional,
            [
                new ServiceStartAction("XblAuthManager", ServiceStartAction.Disabled),
                new ServiceStartAction("XblGameSave", ServiceStartAction.Disabled),
                new ServiceStartAction("XboxNetApiSvc", ServiceStartAction.Disabled),
                new ServiceStartAction("XboxGipSvc", ServiceStartAction.Disabled),
            ],
            TweakAfter.Reboot
        ),
        new(
            "UnusedServices",
            TweakCategory.Performance,
            TweakRisk.Optional,
            [
                new ServiceStartAction("Fax", ServiceStartAction.Disabled),
                new ServiceStartAction("MapsBroker", ServiceStartAction.Disabled),
                new ServiceStartAction("RetailDemo", ServiceStartAction.Disabled),
                new ServiceStartAction("WMPNetworkSvc", ServiceStartAction.Disabled),
            ],
            TweakAfter.Reboot
        ),
        new(
            "GpuScheduling",
            TweakCategory.Gaming,
            TweakRisk.Optional,
            [Dword(@"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2)],
            TweakAfter.Reboot,
            MinBuild: 19041
        ),
        new(
            "MultimediaResponsiveness",
            TweakCategory.Gaming,
            TweakRisk.Optional,
            [
                Dword(
                    @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
                    "SystemResponsiveness",
                    10
                ),
                new RegistryValueAction(
                    @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
                    "NetworkThrottlingIndex",
                    ValueKind.DWord,
                    0xFFFFFFFFu
                ),
            ],
            TweakAfter.Reboot
        ),
        // Gaming
        new(
            "GameMode",
            TweakCategory.Gaming,
            TweakRisk.Recommended,
            [
                Dword(@"HKCU\Software\Microsoft\GameBar", "AutoGameModeEnabled", 1),
                Dword(@"HKCU\Software\Microsoft\GameBar", "AllowAutoGameMode", 1),
            ]
        ),
        new(
            "BackgroundRecording",
            TweakCategory.Gaming,
            TweakRisk.Recommended,
            [
                Dword(@"HKCU\System\GameConfigStore", "GameDVR_Enabled", 0),
                Dword(CurrentVersion + @"\GameDVR", "AppCaptureEnabled", 0),
            ]
        ),
        new(
            "MouseAcceleration",
            TweakCategory.Gaming,
            TweakRisk.Optional,
            [
                Text(@"HKCU\Control Panel\Mouse", "MouseSpeed", "0"),
                Text(@"HKCU\Control Panel\Mouse", "MouseThreshold1", "0"),
                Text(@"HKCU\Control Panel\Mouse", "MouseThreshold2", "0"),
            ],
            TweakAfter.Reboot
        ),
        new(
            "StickyKeysPrompt",
            TweakCategory.Gaming,
            TweakRisk.Recommended,
            [
                Text(@"HKCU\Control Panel\Accessibility\StickyKeys", "Flags", "506"),
                Text(@"HKCU\Control Panel\Accessibility\ToggleKeys", "Flags", "58"),
                Text(@"HKCU\Control Panel\Accessibility\Keyboard Response", "Flags", "122"),
            ]
        ),
        // Explorer
        new(
            "FileExtensions",
            TweakCategory.FileExplorer,
            TweakRisk.Recommended,
            [Dword(ExplorerAdvanced, "HideFileExt", 0)],
            TweakAfter.RestartExplorer
        ),
        new(
            "HiddenFiles",
            TweakCategory.FileExplorer,
            TweakRisk.Optional,
            [Dword(ExplorerAdvanced, "Hidden", 1)],
            TweakAfter.RestartExplorer
        ),
        new(
            "OpenThisPc",
            TweakCategory.FileExplorer,
            TweakRisk.Optional,
            [Dword(ExplorerAdvanced, "LaunchTo", 1)]
        ),
        new(
            "ClassicContextMenu",
            TweakCategory.FileExplorer,
            TweakRisk.Optional,
            [
                Text(
                    @"HKCU\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32",
                    "",
                    ""
                ),
            ],
            TweakAfter.RestartExplorer,
            MinBuild: 22000
        ),
        new(
            "TaskbarLeft",
            TweakCategory.Taskbar,
            TweakRisk.Optional,
            [Dword(ExplorerAdvanced, "TaskbarAl", 0)],
            MinBuild: 22000
        ),
        new(
            "TaskbarEndTask",
            TweakCategory.Taskbar,
            TweakRisk.Optional,
            [Dword(ExplorerAdvanced + @"\TaskbarDeveloperSettings", "TaskbarEndTask", 1)],
            MinBuild: 22631
        ),
        new(
            "HideWidgets",
            TweakCategory.Taskbar,
            TweakRisk.Optional,
            // The policy, because Windows blocks apps from writing the TaskbarDa setting itself.
            [Dword(@"HKLM\SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0)],
            TweakAfter.RestartExplorer,
            MinBuild: 22000
        ),
        new(
            "HideTaskView",
            TweakCategory.Taskbar,
            TweakRisk.Optional,
            [Dword(ExplorerAdvanced, "ShowTaskViewButton", 0)]
        ),
        new(
            "SearchIcon",
            TweakCategory.Taskbar,
            TweakRisk.Optional,
            [Dword(CurrentVersion + @"\Search", "SearchboxTaskbarMode", 1)]
        ),
        new(
            "ClockSeconds",
            TweakCategory.Taskbar,
            TweakRisk.Optional,
            [Dword(ExplorerAdvanced, "ShowSecondsInSystemClock", 1)],
            TweakAfter.RestartExplorer
        ),
        // Explorer look
        new(
            "CompactExplorer",
            TweakCategory.FileExplorer,
            TweakRisk.Optional,
            [Dword(ExplorerAdvanced, "UseCompactMode", 1)],
            TweakAfter.RestartExplorer,
            MinBuild: 22000
        ),
        new(
            "HideExplorerHome",
            TweakCategory.FileExplorer,
            TweakRisk.Optional,
            [
                Dword(
                    UserClsid + @"\{f874310e-b6b7-47dc-bc84-b9e6b38f5903}",
                    "System.IsPinnedToNameSpaceTree",
                    0
                ),
            ],
            TweakAfter.RestartExplorer,
            MinBuild: 22000
        ),
        new(
            "HideExplorerGallery",
            TweakCategory.FileExplorer,
            TweakRisk.Optional,
            [
                Dword(
                    UserClsid + @"\{e88865ea-0e1c-4e20-9aa6-edcd0212c87c}",
                    "System.IsPinnedToNameSpaceTree",
                    0
                ),
            ],
            TweakAfter.RestartExplorer,
            MinBuild: 22621
        ),
        new(
            "NoShortcutSuffix",
            TweakCategory.FileExplorer,
            TweakRisk.Optional,
            [Text(CurrentVersion + @"\Explorer\NamingTemplates", "ShortcutNameTemplate", "%s.lnk")],
            TweakAfter.RestartExplorer
        ),
        // Colors and effects
        new(
            "DarkMode",
            TweakCategory.Appearance,
            TweakRisk.Optional,
            [
                Dword(Personalize, "AppsUseLightTheme", 0),
                Dword(Personalize, "SystemUsesLightTheme", 0),
            ]
        ),
        new(
            "AccentTitleBars",
            TweakCategory.Appearance,
            TweakRisk.Optional,
            [Dword(@"HKCU\Software\Microsoft\Windows\DWM", "ColorPrevalence", 1)]
        ),
        new(
            "AccentTaskbar",
            TweakCategory.Appearance,
            TweakRisk.Optional,
            [Dword(Personalize, "ColorPrevalence", 1)]
        ),
        new(
            "NoTransparency",
            TweakCategory.Appearance,
            TweakRisk.Optional,
            [Dword(Personalize, "EnableTransparency", 0)]
        ),
        new(
            "FewerAnimations",
            TweakCategory.Appearance,
            TweakRisk.Optional,
            [
                Text(@"HKCU\Control Panel\Desktop\WindowMetrics", "MinAnimate", "0"),
                Dword(ExplorerAdvanced, "TaskbarAnimations", 0),
            ],
            TweakAfter.Reboot
        ),
    ];

    private static RegistryValueAction Dword(string key, string name, int value) =>
        new(key, name, ValueKind.DWord, value);

    private static RegistryValueAction Text(string key, string name, string value) =>
        new(key, name, ValueKind.String, value);
}
