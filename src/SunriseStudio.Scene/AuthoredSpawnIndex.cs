using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using SunriseStudio.Packages;

namespace SunriseStudio.Scene;

/** One 0x808099D8 spawn row: the tag that holds it and the row's offset in that tag. */
public readonly record struct SpawnInput(uint Tag, int Offset);

/**
 * Rows of one entity that pass the same initial-state inputs: the damage override at +100
 * and the override chain at +120. First is the group's lowest (tag, offset) row.
 */
public sealed record AuthoredSpawn(SpawnInput First, int RowCount);

/** Every authored spawn row in the installed packages, grouped per entity by spawn input. */
public sealed class AuthoredSpawnIndex
{
    private const uint EntityClass = 0x80809C0F, ConfigClass = 0x80809C36, PlacementList = 0x808099D6;
    private const uint SpawnRow = 0x808099D8, SlotDescriptor = 0x80809927, VariantOverride = 0x808038A0;
    private const uint SquadPrimary = 0x80809A3B, SquadSecondary = 0x8080948F;
    private const uint SquadMember = 0x80808356, SquadCandidate = 0x80808358;
    private const int RowSize = 144;
    // A member holds six candidate arrays, 16 bytes apart from +8.
    private const int CandidateArrays = 6;
    private readonly Dictionary<uint, AuthoredSpawn[]> spawns;

    private AuthoredSpawnIndex(Dictionary<uint, AuthoredSpawn[]> spawns, int rows, int unreadTags, TimeSpan time)
    {
        this.spawns = spawns;
        RowCount = rows;
        UnreadTagCount = unreadTags;
        BuildTime = time;
    }

    public int RowCount { get; }
    /** Tags whose arrays did not decode; their rows are missing from the index. */
    public int UnreadTagCount { get; }
    public TimeSpan BuildTime { get; }
    public int EntityCount => spawns.Count;

    /** Groups by first row; element 0 has the lowest (tag, offset), first in package order. */
    public IReadOnlyList<AuthoredSpawn> For(uint entity) => spawns.GetValueOrDefault(entity, []);

    /**
     * Reads every placement list and config of the installed packages.
     * @param progress Receives (tags read, tags to read).
     */
    public static AuthoredSpawnIndex Build(PackageCatalog catalog, CancellationToken token = default,
        Action<int, int>? progress = null)
    {
        var timer = Stopwatch.StartNew();
        var sources = catalog.Entries.Where(entry => entry.Type is 8 or 16
            && entry.Reference is PlacementList or ConfigClass).Select(entry => entry.TagHash).Order().ToArray();
        var groups = new ConcurrentDictionary<(uint Entity, string Input), Group>();
        int rows = 0, unread = 0, done = 0;
        var options = new ParallelOptions { CancellationToken = token };
        Parallel.ForEach(Partitioner.Create(0, sources.Length, 256), options, range =>
        {
            for (int i = range.Item1; i < range.Item2; i++)
            {
                token.ThrowIfCancellationRequested();
                uint tag = sources[i];
                try
                {
                    var data = new TagData(catalog.Read(tag));
                    foreach (int row in Rows(catalog, tag, data))
                    {
                        uint entity = data.U32(row);
                        if (!catalog.TryGetEntry(entity, out var entry) || entry.Reference != EntityClass) continue;
                        Interlocked.Increment(ref rows);
                        var input = new SpawnInput(tag, row);
                        groups.AddOrUpdate((entity, Input(data, tag, row)), _ => new(input, 1),
                            (_, group) => new(Lower(group.First, input), group.Count + 1));
                    }
                }
                catch (Exception error) when (error is InvalidDataException or ArgumentException
                    or OverflowException or IndexOutOfRangeException or IOException)
                { Interlocked.Increment(ref unread); }
                int count = Interlocked.Increment(ref done);
                if ((count & 4095) == 0) progress?.Invoke(count, sources.Length);
            }
        });
        progress?.Invoke(sources.Length, sources.Length);
        var result = groups.GroupBy(pair => pair.Key.Entity).ToDictionary(entity => entity.Key,
            entity => entity.Select(pair => new AuthoredSpawn(pair.Value.First, pair.Value.Count))
                .OrderBy(spawn => spawn.First.Tag).ThenBy(spawn => spawn.First.Offset).ToArray());
        return new(result, rows, unread, timer.Elapsed);
    }

    private sealed record Group(SpawnInput First, int Count);

    private static SpawnInput Lower(SpawnInput a, SpawnInput b) =>
        a.Tag < b.Tag || a.Tag == b.Tag && a.Offset <= b.Offset ? a : b;

    /** Spawn rows of one tag: list rows, type-4 slot rows and squad candidate targets. */
    private static IEnumerable<int> Rows(PackageCatalog catalog, uint tag, TagData data)
    {
        if (!catalog.TryGetEntry(tag, out var entry)) return [];
        if (entry.Reference == PlacementList) return data.Rows(8, SpawnRow, RowSize);
        var rows = new HashSet<int>();
        for (int descriptor = 0; descriptor <= data.Length - 128; descriptor += 4)
            if (data.U32(descriptor) == tag && data.U32(descriptor + 4) == SlotDescriptor
                && data.U32(descriptor + 8) == 0x70 && data.U16(descriptor + 52) == 4)
                rows.UnionWith(data.Rows(descriptor + 0x58, SpawnRow, RowSize));
        rows.UnionWith(SquadCandidates(tag, data));
        return rows;
    }

    /** Candidate rows point at embedded spawn rows whose class word sits just before them. */
    private static IEnumerable<int> SquadCandidates(uint tag, TagData data)
    {
        if (data.Length < 0x20 || data.I64(0x10) == 0 || data.I64(0x18) == 0) yield break;
        int primary = data.Relative(0x10), secondary = data.Relative(0x18);
        if (primary < 4 || secondary < 4 || !data.Has(primary, 16) || !data.Has(secondary, 0xB8)
            || data.U32(primary - 4) != SquadPrimary || data.U32(secondary - 4) != SquadSecondary
            || data.U32(primary) != tag || data.U32(secondary) != tag) yield break;
        foreach (int member in data.Rows(secondary + 0xA8, SquadMember, 0x68))
            for (int array = 0; array < CandidateArrays; array++)
                foreach (int candidate in data.Rows(member + 8 + array * 16, SquadCandidate, 0x18))
                {
                    if (data.I64(candidate) == 0) continue;
                    int row = data.Relative(candidate);
                    if (data.Has(row - 4, RowSize + 4) && data.U32(row - 4) == SpawnRow) yield return row;
                }
    }

    /** Keys the inputs the initial-state reader takes; a broken chain keys its row alone. */
    private static string Input(TagData data, uint tag, int row)
    {
        try { return ReadInput(data, row); }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or OverflowException)
        { return $"invalid {tag:X8}+{row:X}"; }
    }

    private static string ReadInput(TagData data, int row)
    {
        var text = new StringBuilder();
        text.Append(data.U32(row + 100).ToString("X8"));
        var seen = new HashSet<int>();
        int link = row + 120;
        while (data.I64(link) != 0)
        {
            int node = data.Relative(link);
            if (!data.Has(node - 4, 40) || !seen.Add(node) || seen.Count > 64)
                throw new InvalidDataException("Spawn override chain is invalid.");
            uint type = data.U32(node - 4);
            text.Append($" {data.I32(node + 8)}:{type:X8}");
            if (type == VariantOverride)
            {
                foreach (int pair in data.RowsOfAnyClass(node + 16, 8))
                    text.Append($" {data.U32(pair):X8}={data.U32(pair + 4):X8}");
                text.Append($" >{data.U32(node + 32):X8}");
            }
            link = node;
        }
        return text.ToString();
    }
}
