using System.Buffers.Binary;
using SunriseStudio.Animation;
using SunriseStudio.Audio;
using SunriseStudio.Authoring;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

namespace SunriseStudio.Editor;

/**
 * One sound event the weapon plays, the records from an owned one down to its holder, and whether the event is the project's.
 * Detail names the holder, offset and event in hex.
 */
public sealed record WeaponSoundSlot(uint EventTag, string Label, IReadOnlyList<uint> Path, bool Owned, string Detail = "");

/** New assets, edits to base records, and edits to this project's own earlier sound assets by asset ID. */
public sealed record PreparedWeaponSounds(IReadOnlyList<ProjectAssetAddition> Assets,
    IReadOnlyDictionary<uint, byte[]> Changes, IReadOnlyList<string> Summary, IReadOnlyDictionary<string, byte[]> AddedChanges);

/** A weapon sound is replaced by a new bank and event; the records naming the old event are copied. */
public static class WeaponSoundAuthoring
{
    private const uint EntityClass = 0x80809C0F, ConfigClass = 0x80809C36, WrapperClass = 0x8080744A, TableClass = 0x8080902D;
    // Event tag: +8 event ID, +12 paired event ID, +20 bank tag, +24 media count, +32 media array pointer.
    private const int EventId = 8, RelatedId = 12, BankTag = 20, MediaCount = 24, MediaArray = 32;
    private static readonly HashSet<uint> Followed = [EntityClass, ConfigClass, WrapperClass, TableClass];

    /**
     * Lists the sound events reachable from the weapon's own records through the base weapon's
     * package. In a selector config only the weapon's own row is followed. Each slot keeps one path.
     */
    public static IReadOnlyList<WeaponSoundSlot> Find(StudioProject project, PackageCatalog catalog, uint weaponTag)
    {
        var owned = OwnedTags(project);
        var art = WeaponArchetypeAuthoring.Read(catalog, catalog.Read(weaponTag));
        uint gearKey = WeaponArchetypeAuthoring.GearKey(catalog, art);
        if (!catalog.TryGetEntry(WeaponPresentationAuthoring.GameplayTemplate, out var template))
            throw new InvalidDataException("The template weapon is missing.");
        var seeds = art.Variants.Select(part => part.EntityTag).Prepend(art.EntityTag).ToList();
        foreach (uint entity in seeds.ToArray())
            if (catalog.TryGetEntry(entity, out var seed) && seed.Reference == EntityClass)
                seeds.AddRange(WeaponConfigEdits.ConstructionConfigs(catalog.Read(entity)));
        var paths = new Dictionary<uint, List<uint>>();
        var queue = new Queue<uint>();
        foreach (uint tag in seeds.Where(owned.Contains).Distinct()) { paths[tag] = [tag]; queue.Enqueue(tag); }
        var slots = new List<WeaponSoundSlot>();
        while (queue.TryDequeue(out uint holder))
        {
            if (!catalog.TryGetEntry(holder, out var entry) || !Followed.Contains(entry.Reference)) continue;
            byte[] bytes = catalog.Read(holder);
            var seen = new HashSet<uint>();
            var foreign = ForeignRows(bytes, gearKey);
            int next = 0;
            for (int at = 0; at + 4 <= bytes.Length; at += 4)
            {
                while (next < foreign.Count && foreign[next].End <= at) next++;
                if (next < foreign.Count && at >= foreign[next].Start) continue;
                uint value = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
                if (value < 0x80800000 || !seen.Add(value) || !catalog.TryGetEntry(value, out var target)) continue;
                if (target.Reference == AudioCatalog.EventClass)
                    slots.Add(new(value, Label(catalog, bytes, holder, at) ?? "", paths[holder], owned.Contains(value),
                        $"{holder:X8} +0x{at:X} event {value:X8}"));
                else if (!paths.ContainsKey(value) && Followed.Contains(target.Reference)
                    && (owned.Contains(value) || target.PackageId == template.PackageId))
                {
                    // An owned record starts a new path; only base records on a path are copied.
                    paths[value] = owned.Contains(value) ? [value] : [.. paths[holder], value];
                    queue.Enqueue(value);
                }
            }
        }
        var numbers = new Dictionary<uint, int>();
        string Number(uint tag)
        {
            if (!numbers.TryGetValue(tag, out int number)) numbers[tag] = number = numbers.Count + 1;
            return $"Sound {number}";
        }
        return slots.DistinctBy(slot => (slot.EventTag, slot.Path[^1]))
            .Select(slot => slot.Label.Length != 0 ? slot : slot with { Label = Number(slot.EventTag) }).ToArray();
    }

    /**
     * Rows of a presentation or HUD selector config that another gear key selects, sorted by start.
     * A config with no row for the key falls back to its default row, so nothing is excluded then.
     */
    private static List<(int Start, int End)> ForeignRows(byte[] bytes, uint key)
    {
        var result = new List<(int, int)>();
        void Rows(int field, uint rowClass, int stride, int keyOffset)
        {
            if (field < 0 || field > bytes.Length - 16) return;
            long count = I64(bytes, field), header = field + 8 + I64(bytes, field + 8);
            if (count is <= 0 or > 1024 || header < 4 || header > bytes.Length - 16 || U32(bytes, (int)header + 8) != rowClass) return;
            int data = (int)header + 16;
            if (data + count * stride > bytes.Length) return;
            var rows = Enumerable.Range(0, (int)count).Select(index => data + index * stride).ToArray();
            if (!rows.Any(row => U32(bytes, row + keyOffset) == key)) return;
            result.AddRange(rows.Where(row => U32(bytes, row + keyOffset) != key).Select(row => (row, row + stride)));
        }
        // Presentation config: rows at secondary +1416, the secondary at +0x598.
        Rows(0x598 + 1416, PresentationRowClass, 232, 0);
        // HUD config: rows at resource +0x240, the resource named at +24.
        if (bytes.Length >= 32 && 24 + I64(bytes, 24) is var resource and >= 4 and < int.MaxValue && resource <= bytes.Length - 4
            && U32(bytes, (int)resource - 4) == HudResourceClass)
            Rows((int)resource + 0x240, HudRowClass, 448, 16);
        result.Sort();
        return result;
    }

    private const uint PresentationRowClass = 0x80803E72, HudResourceClass = 0x80803AC9, HudRowClass = 0x80803ACF;

    /** A table row's event path names the sound; null when the holder has no path. */
    private static string? Label(PackageCatalog catalog, byte[] holder, uint holderTag, int at)
    {
        if (catalog.TryGetEntry(holderTag, out var entry) && entry.Reference == TableClass && at >= 8)
        {
            long relative = BinaryPrimitives.ReadInt64LittleEndian(holder.AsSpan(at - 8));
            long start = at - 8 + relative;
            if (relative != 0 && start >= 0 && start < holder.Length)
            {
                int end = Array.IndexOf(holder, (byte)0, (int)start);
                if (end > start && end - start < 400)
                {
                    string path = System.Text.Encoding.ASCII.GetString(holder, (int)start, end - (int)start);
                    return Path.GetFileNameWithoutExtension(path.Replace('\\', '/'));
                }
            }
        }
        return null;
    }

    /**
     * Replaces each chosen event with a new bank and event of the weapon's own.
     * @param slots The weapon's slots from Find; only records on their paths are retargeted.
     * @param waves Template event tag to the wave that replaces it.
     * @param name Prefix for the new event names; lower case, unique to the weapon.
     * @param releases Template event tag to the wave its paired event plays, such as an automatic tail.
     */
    public static PreparedWeaponSounds Prepare(StudioProject project, PackageCatalog catalog, uint weaponTag,
        IReadOnlyList<WeaponSoundSlot> slots, IReadOnlyDictionary<uint, PcmWave> waves, string name,
        ushort packageId, int firstEntry, IReadOnlyDictionary<uint, PcmWave>? releases = null)
    {
        var owned = OwnedTags(project);
        var assets = new List<ProjectAssetAddition>();
        var changes = new Dictionary<uint, byte[]>();
        var addedChanges = new Dictionary<string, byte[]>();
        var summary = new List<string>();
        var clones = new Dictionary<uint, uint>();
        int cursor = firstEntry;
        uint Next()
        {
            uint tag = ProjectTags.Tag(packageId, cursor);
            if (cursor > 8191 || catalog.ContainsTag(tag)) throw new InvalidDataException("The weapon sounds need free package entries.");
            cursor++;
            return tag;
        }
        int Entry(uint tag) => (int)(tag - 0x80800000u - ((uint)packageId << 13));
        byte[] Read(uint tag) => changes.TryGetValue(tag, out var bytes) ? bytes
            : assets.FirstOrDefault(asset => asset.Native is { } native && native.Tag() == tag)?.Data ?? catalog.Read(tag);
        void Write(uint tag, byte[] bytes)
        {
            int index = assets.FindIndex(asset => asset.Native is { } native && native.Tag() == tag);
            if (index >= 0) assets[index] = assets[index] with { Data = bytes };
            else changes[tag] = bytes;
        }

        // Only this weapon's own records and clips switch to a copy; other weapons keep theirs.
        var candidates = slots.SelectMany(slot => slot.Path).Where(owned.Contains)
            .Concat(WeaponClips(catalog, weaponTag).Where(owned.Contains)).Distinct().ToList();
        void Retarget(uint from, uint to)
        {
            bool constructed = catalog.TryGetEntry(from, out var fromEntry) && fromEntry.Reference == ConfigClass;
            foreach (uint tag in candidates.Concat(clones.Values).Distinct())
            {
                byte[] bytes = Read(tag);
                if (Count(bytes, from) == 0) continue;
                bool entity = catalog.TryGetEntry(tag, out var entry) ? entry.Reference == EntityClass
                    : assets.Any(asset => asset.Native is { } native && native.Tag() == tag && native.Reference == EntityClass);
                Write(tag, constructed && entity && WeaponConfigEdits.ConstructionConfigs(bytes).Contains(from)
                    ? WeaponConfigEdits.RetagRoot(bytes, from, to) : Replace(bytes, from, to));
                summary.Add($"retargeted {tag:X8}: {from:X8} -> {to:X8}");
            }
        }

        // Builds a template event's new bank, and the event bytes that name it at bankTag.
        (byte[] Event, AuthoredSoundBank Bank, uint BankSource, PackageEntry BankEntry, PackageEntry EventEntry) Author(
            uint source, PcmWave wave, PcmWave? release, uint bankTag)
        {
            byte[] template = catalog.Read(source);
            if (!catalog.TryGetEntry(source, out var eventEntry) || eventEntry.Reference != AudioCatalog.EventClass
                || template.Length < 40 || BinaryPrimitives.ReadUInt64LittleEndian(template) != (ulong)template.Length)
                throw new InvalidDataException($"{source:X8} is not an audio event.");
            uint bankSource = U32(template, BankTag);
            if (!catalog.TryGetEntry(bankSource, out var bankEntry)) throw new InvalidDataException("The event's bank is missing.");
            var bank = AudioCatalog.ReadBank(bankSource, catalog.Read(bankSource));
            var built = WwiseSoundBank.Build(bank, U32(template, EventId), U32(template, RelatedId), $"{name}/{source:x8}", wave, release);
            byte[] authored = template.ToArray();
            Put(authored, EventId, built.EventId);
            Put(authored, RelatedId, built.RelatedEventId);
            Put(authored, BankTag, bankTag);
            // The new media sits inside the bank, so the event lists no streamed media.
            BinaryPrimitives.WriteInt64LittleEndian(authored.AsSpan(MediaCount), 0);
            BinaryPrimitives.WriteInt64LittleEndian(authored.AsSpan(MediaArray), 0);
            return (authored, built, bankSource, bankEntry, eventEntry);
        }

        // An event this weapon already replaced is rebuilt in place from its template; records keep naming it.
        var replaced = project.AddedAssets.Where(asset => asset.Native?.Reference == AudioCatalog.EventClass && asset.BaseTagHash is not null).ToList();
        foreach (var (source, wave) in waves)
        {
            var release = releases?.GetValueOrDefault(source);
            string seconds = $"({wave.DurationSeconds:F2} s" + (release is null ? ")" : $", release {release.DurationSeconds:F2} s)");
            var previous = replaced.FirstOrDefault(asset => asset.Native!.Tag() == source);
            if (previous is not null)
            {
                uint ownedEvent = previous.Native!.Tag(), ownedBank = U32(catalog.Read(ownedEvent), BankTag);
                var bankAsset = project.AddedAssets.FirstOrDefault(asset => asset.Native is { } native && native.Tag() == ownedBank)
                    ?? throw new InvalidDataException($"{ownedEvent:X8} does not name a bank of this project.");
                var rebuilt = Author(previous.BaseTagHash!.Value, wave, release, ownedBank);
                if (rebuilt.Bank.BankId != bankAsset.Native!.Reference || bankAsset.BaseTagHash != rebuilt.BankSource)
                    throw new InvalidDataException($"{ownedBank:X8} is not the bank built from {previous.BaseTagHash:X8}.");
                addedChanges[previous.Id] = rebuilt.Event;
                addedChanges[bankAsset.Id] = rebuilt.Bank.Bytes;
                summary.Add($"rebuilt event {ownedEvent:X8} and bank {ownedBank:X8} from {previous.BaseTagHash:X8} {seconds}");
                continue;
            }
            uint bankTag = Next(), eventTag = Next();
            var (authored, built, bankSource, bankEntry, eventEntry) = Author(source, wave, release, bankTag);
            assets.Add(new($"{name}.{source:X8}.bank", built.Bytes,
                new(packageId, built.BankId, bankEntry.TypeInfo, null, Entry(bankTag)), bankSource));
            assets.Add(new($"{name}.{source:X8}.event", authored,
                new(packageId, AudioCatalog.EventClass, eventEntry.TypeInfo, null, Entry(eventTag)), source));
            summary.Add($"event {source:X8} -> {eventTag:X8}, bank {bankSource:X8} -> {bankTag:X8} {seconds}");

            // Copy the base-weapon records on each path to this event.
            foreach (var slot in slots.Where(slot => slot.EventTag == source))
                for (int index = 1; index < slot.Path.Count; index++)
                {
                    uint record = slot.Path[index];
                    if (owned.Contains(record) || clones.ContainsKey(record)) continue;
                    uint copy = Next();
                    catalog.TryGetEntry(record, out var recordEntry);
                    byte[] bytes = catalog.Read(record);
                    bytes = recordEntry.Reference == ConfigClass ? WeaponConfigEdits.RetagConfig(bytes, record, copy).Bytes : bytes.ToArray();
                    if (recordEntry.Reference != ConfigClass && Count(bytes, record) != 0)
                        throw new InvalidDataException($"{record:X8} names itself and has no checked copy rule.");
                    assets.Add(new($"{name}.{record:X8}", bytes, new(packageId, recordEntry.Reference, recordEntry.TypeInfo, null, Entry(copy)), record));
                    clones[record] = copy;
                    summary.Add($"copied {record:X8} -> {copy:X8}");
                    Retarget(record, copy);
                }
            Retarget(source, eventTag);
        }
        return new(assets, changes, summary, addedChanges);
    }

    /** The clips the weapon's presentation entity plays. */
    private static IEnumerable<uint> WeaponClips(PackageCatalog catalog, uint weaponTag)
    {
        var art = WeaponArchetypeAuthoring.Read(catalog, catalog.Read(weaponTag));
        uint owner = WeaponPresentationReader.Read(catalog, art.EntityTag, WeaponArchetypeAuthoring.GearKey(catalog, art));
        return new EntityAnimationCatalog(catalog).Read(owner).Select(clip => clip.ClipTag);
    }

    private static HashSet<uint> OwnedTags(StudioProject project) =>
        project.AddedAssets.Where(asset => asset.Native is not null).Select(asset => asset.Native!.Tag()).ToHashSet();

    private static int Count(byte[] bytes, uint value)
    {
        int hits = 0;
        for (int at = 0; at + 4 <= bytes.Length; at += 4) if (U32(bytes, at) == value) hits++;
        return hits;
    }

    private static byte[] Replace(byte[] bytes, uint from, uint to)
    {
        if (Count(bytes, from) == 0) throw new InvalidDataException($"No reference to {from:X8} to replace.");
        byte[] output = bytes.ToArray();
        for (int at = 0; at + 4 <= output.Length; at += 4) if (U32(output, at) == from) Put(output, at, to);
        return output;
    }

    private static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
    private static long I64(byte[] bytes, int at) => BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(at));
    private static void Put(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
}
