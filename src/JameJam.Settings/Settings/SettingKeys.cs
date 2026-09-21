namespace JameJam.Settings;

/// <summary>Well-known keys the toolbox itself relies on. Custom keys are allowed too.</summary>
public static class SettingKeys
{
    /// <summary>Default name used when JameJam is run without arguments.</summary>
    public const string GreeterDefaultName = "greeter.defaultName";

    /// <summary>Default length for vault-generated passwords (Raz).</summary>
    public const string RazDefaultLength = "raz.length";

    /// <summary>Preferred default notebook for new Divan notes.</summary>
    public const string DivanNotebook = "divan.notebook";

    /// <summary>Default Soroush AI provider (e.g. <c>"openai"</c>, <c>"anthropic"</c>).</summary>
    public const string SoroushProvider = "soroush.provider";

    /// <summary>Default Soroush AI endpoint URL.</summary>
    public const string SoroushEndpoint = "soroush.endpoint";

    /// <summary>Default Soroush AI model identifier.</summary>
    public const string SoroushModel = "soroush.model";

    /// <summary>Default remote sync URL for Haft Khan.</summary>
    public const string SyncUrl = "haftkhan.syncUrl";

    /// <summary>Default remote sync URL for the Divan pad (two-device sync).</summary>
    public const string DivanSyncUrl = "divan.syncUrl";

    /// <summary>Sync endpoint for the Taqvim calendar.</summary>
    public const string TaqvimSyncUrl = "taqvim.syncUrl";

    /// <summary>Stable identity of this installation for sync (a GUID; generated on first sync).</summary>
    public const string SyncDeviceId = "sync.deviceId";

    /// <summary>Friendly name of this device shown in sync reports.</summary>
    public const string SyncDeviceName = "sync.deviceName";

    /// <summary>Default Anahita weather location (place name or "lat,lon").</summary>
    public const string AnahitaLocation = "anahita.location";

    /// <summary>Default Anahita weather units (metric|imperial).</summary>
    public const string AnahitaUnits = "anahita.units";

    /// <summary>Base currency for the Ganjoor wallet (e.g. <c>"EUR"</c>).</summary>
    public const string GanjoorCurrency = "ganjoor.currency";
}
