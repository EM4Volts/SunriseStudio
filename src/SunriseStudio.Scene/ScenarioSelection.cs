namespace SunriseStudio.Scene;

public enum ScenarioStateMode { All, EnabledByPackage, Selected }
public enum SceneNameEvidence
{
    Unknown, PackageTag, PackageInline, HashCandidate, IncomingReference, PackageClass,
    // A content path in a behaviour the object's class links to; it names the behaviour, not the object.
    LinkedContent,
    // A generic kind label from the class and components; the object has no known name.
    KindLabel,
    // Localized text a component of the object references, such as an interaction prompt.
    LinkedText,
    // A descriptive label built from the kind and context such as the bubble; not an authored name.
    Derived
}

public sealed class SceneLoadOptions
{
    public ScenarioStateMode ScenarioStates { get; init; } = ScenarioStateMode.All;
    public IReadOnlyDictionary<uint, int> StateOrdinals { get; init; } = new Dictionary<uint, int>();
    public IReadOnlySet<uint>? BubbleNameHashes { get; init; }
    public IReadOnlySet<int>? MapGlobalIndices { get; init; }
    /** Variant row the opened entity requests, as a spawn would; zero requests none. */
    public uint PreviewVariant { get; init; }
    /** Spawn row the opened entity is read with; null reads the native default. */
    public SpawnInput? PreviewSpawn { get; init; }
    /** Authored spawn groups of the opened entity; the scene lists them as picker entries. */
    public IReadOnlyList<AuthoredSpawn> AuthoredSpawns { get; init; } = [];
    /** Variant parameter pairs the opened entity's spawn passes, as gear spawns do; later pairs win. */
    public IReadOnlyList<(uint Key, uint Value)> SpawnPairs { get; init; } = [];
    /** Variant parameter values the user picked for the opened entity; they apply last. */
    public IReadOnlyDictionary<uint, uint> PreviewParameters { get; init; } = new Dictionary<uint, uint>();
    /** Object channel values by channel name for the opened entity; an unset channel keeps its rest value. */
    public IReadOnlyDictionary<uint, float> ChannelValues { get; init; } = new Dictionary<uint, float>();
}

public sealed record ScenarioStateInfo(uint BubbleNameHash, int Ordinal, uint NameHash,
    string Name, bool PackageEnabled, bool IsPublic, uint EntryTag, uint EntryIndex,
    uint RegistryTag, uint MapBubbleIndex);

public sealed record ScenarioBubbleInfo(uint NameHash, string Name, IReadOnlyList<ScenarioStateInfo> States);
public sealed record ScenarioInfo(uint TagHash, string Name, IReadOnlyList<ScenarioBubbleInfo> Bubbles);
public sealed record AuthoredSlotIdentity(uint RegistryKey, ushort Type, ushort Index);
public sealed record AuthoredSlotInfo(AuthoredSlotIdentity Identity, uint NameHash, string Name);
public sealed record AuthoredObjectInfo(uint TagHash, uint RegistryKey, IReadOnlyList<AuthoredSlotInfo> Slots);
public sealed record AuthoredSquadInfo(uint ConfigTag, AuthoredSlotIdentity Slot, uint BubbleHash,
    uint EntryIndex, int MemberCount, int AnchorCount, int UnresolvedPointCount, bool Ambiguous,
    bool HasResolvedRule = false);
