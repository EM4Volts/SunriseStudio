using SunriseStudio.Packages;

namespace SunriseStudio.Animation;

/** A stand pick: the stand set, the definition name row its first weighted row names, that row's slot and clip. */
public sealed record AnimationStandChoice(uint DefinitionTag, int Set, int NameRow, int Slot, uint ClipTag);

/**
 * The standing clip a body's sequence selector picks for a weapon's third-person tag.
 * The tag tree picks a stand set; the set's first weighted row names the clip.
 */
public static class AnimationStandSelector
{
    private const uint EntityClass = 0x80809C0F, ComponentRowClass = 0x80809C04, ConfigClass = 0x80809C36;
    private const uint SelectorClass = 0x8080344B, DefinitionClass = 0x808036F6;
    private const uint GroupClass = 0x80808EE5, TagRowClass = 0x80808EE8, NodeClass = 0x8080347D, BranchClass = 0x8080347C;
    private const uint SetClass = 0x80803378, SetRowClass = 0x8080338C, ClipClass = 0x80808F48;
    // Selector resource fields: the definition, the tag vocabulary, the stand sets and the stand tree.
    private const int DefinitionField = 0x90, VocabularyField = 0x94, SetsField = 0xB4, TreeField = 0xB8;
    // The animator has 24 tag slots; an inline i16 table at vocabulary +24 names each slot's tag group.
    private const int Slots = 24, SlotGroups = 24;
    private const int PostureSlot = 1, WeaponSlot = 3, StanceSlot = 10, SurfaceSlot = 11, ClassSlot = 20, GenderSlot = 21, RaceSlot = 22;
    // Tree layout: nodes at +0x18, branches at +0x28, the fallback leaf at +0x38, leaf to set map at +0x40.
    private const int Nodes = 0x18, Branches = 0x28, FallbackLeaf = 0x38, SetMap = 0x40, NodeStride = 28;
    private const int SetStride = 72, SetRowStride = 12, MaximumDepth = 512;
    private const uint EmptyTag = 0x811C9DC5;
    // Query table at selector +0x9C: {u32 hash, u32 query} keys at +8, {u32 kind, i64 object} queries at +0x18.
    // A set is an array of {u32 name row, f32 weight}.
    private const int QueriesField = 0x9C, QueryKeys = 8, QueryList = 0x18;
    private const uint QueryTableClass = 0x80803465, QueryKeyClass = 0x8080342E, QueryClass = 0x8080342F;
    private const uint SingleQueryClass = 0x80803434, TreeQueryClass = 0x80803432, QuerySetClass = 0x80803437,
        QueryRowClass = 0x80803439;

    /**
     * Query names whose FNV-1 hash is a key of the body query table. Each is a one or two word action name, or a
     * longer one whose family or alias is also a key.
     */
    public static readonly IReadOnlyList<string> KnownQueries =
    [
        "ready", "put_away", "sprint_ready", "alert_enter", "alert_exit", "reload_enter", "reload_continue",
        "reload_continue_empty", "reload_special", "throw_grenade", "throw_knife", "sword_throw", "melee_lunge",
        "hunter_melee", "titan_melee", "melee_warlock", "hunter_melee_recover", "titan_melee_recover",
        "warlock_melee_recover", "melee_airborne_heavy", "knife_melee_airborne", "finisher_melee", "guard_enter",
        "guard_idle", "guard_exit", "sword_guard_enter", "sword_guard_exit", "double_jump", "glide", "glide_activate",
        "glide_deactivate", "lift", "airborne", "land_hard", "land_dead_front", "land_dead_back", "crouch_slide",
        "crouch_slide_long", "juggernaut_slide", "dodge_front", "dodge_back", "dodge_left", "dodge_right",
        "dodge_front_air", "dodge_back_air", "dodge_left_air", "dodge_right_air", "cast_blink", "cast_exit",
        "arc_beam", "novabomb", "hunter_smoke_bomb", "bow_cast_ground", "bow_cast_air", "radiance", "radiance_exit",
        "radiance_air", "radiance_air_exit", "respawn", "self_resurrection", "ghost_idle", "ghost_exit",
        "interact_ghost_cast", "level_up", "driver_emote_front", "driver_emote_back", "driver_emote_left",
        "driver_emote_right", "driver_land_hard"
    ];

    /** The hash a tag or query of this spelling has. */
    public static uint TagHash(string spelling) => AnimationClipNames.Hash(spelling);

    /** Weapon tags of the stand tree's weapon slot, by their spelling. */
    public static readonly IReadOnlyList<string> WeaponTags =
    [
        "unarmed", "rifle", "pistol", "sidearm", "smg", "shotgun", "sniper_rifle", "fusion_rifle", "machine_gun",
        "rocket_launcher", "grenade_launcher", "sword", "bow"
    ];

    /** The weapon tag the game sets when a weapon names none. */
    public static readonly uint Unarmed = AnimationClipNames.Hash("unarmed");
    // The game's defaults: standing on the ground, in the combat stance.
    private static readonly uint Stand = AnimationClipNames.Hash("stand"), Ground = AnimationClipNames.Hash("ground"),
        Combat = AnimationClipNames.Hash("combat");

    /**
     * Reads the body's selector component and picks its stand clip for one weapon tag.
     * @param weaponTag The weapon's third-person tag; 0 or the empty hash means unarmed.
     * @param guardianClass Lower-case class name, such as titan; empty leaves the slot unset. Gender and race likewise.
     * @return Null when the body has no selector component.
     */
    public static AnimationStandChoice? Select(PackageCatalog catalog, uint entityTag, uint weaponTag,
        string guardianClass = "", string gender = "", string race = "")
    {
        if (Selector(catalog, entityTag) is not var (config, resource, definition)) return null;
        var tags = new uint[Slots];
        Array.Fill(tags, EmptyTag);
        tags[PostureSlot] = Stand;
        tags[WeaponSlot] = weaponTag is 0 or EmptyTag ? Unarmed : weaponTag;
        tags[StanceSlot] = Combat;
        tags[SurfaceSlot] = Ground;
        tags[ClassSlot] = AnimationClipNames.Hash(guardianClass);
        tags[GenderSlot] = AnimationClipNames.Hash(gender);
        tags[RaceSlot] = AnimationClipNames.Hash(race);
        return Pick(catalog, definition, config.U32(resource + VocabularyField), config.U32(resource + SetsField),
            config.U32(resource + TreeField), tags);
    }

    /**
     * Names of the action queries that can play each clip, over every branch of each query.
     * @param names Query names to look for; a name whose hash the table lacks is skipped.
     * @return Names by clip tag, in the order of names; empty when the body has no selector.
     */
    public static IReadOnlyDictionary<uint, IReadOnlyList<string>> QueryNames(PackageCatalog catalog, uint entityTag,
        IReadOnlyList<string> names)
    {
        var result = new Dictionary<uint, IReadOnlyList<string>>();
        if (Selector(catalog, entityTag) is not var (config, resource, definitionTag)) return result;
        uint tableTag = config.U32(resource + QueriesField);
        if (!IsClass(catalog, tableTag, QueryTableClass)) throw new InvalidDataException("The stand selector names no query table.");
        var table = new AnimationControlReader.Data(catalog.Read(tableTag));
        int[] queries = table.Rows(QueryList, QueryClass, 16);
        var byHash = new Dictionary<uint, int>();
        foreach (int row in table.Rows(QueryKeys, QueryKeyClass, 8)) byHash.TryAdd(table.U32(row), checked((int)table.U32(row + 4)));
        byte[] definition = catalog.Read(definitionTag);
        var rows = AnimationNameRows.Read(definition);
        var data = new AnimationControlReader.Data(definition);
        int[] clips = data.Rows(8, ClipClass, 4);
        var found = new Dictionary<uint, List<string>>();
        foreach (string name in names)
        {
            if (!byHash.TryGetValue(AnimationClipNames.Hash(name), out int query)) continue;
            if ((uint)query >= (uint)queries.Length) throw new InvalidDataException($"Query {name} is outside its table.");
            foreach (int nameRow in QueryRows(table, queries[query]))
            {
                if ((uint)nameRow >= (uint)rows.Length) throw new InvalidDataException($"Query {name} names row {nameRow}, past the definition.");
                int slot = rows[nameRow].Slot;
                if ((uint)slot >= (uint)clips.Length) continue;
                uint clip = data.U32(clips[slot]);
                if (!found.TryGetValue(clip, out var list)) found.Add(clip, list = []);
                if (!list.Contains(name)) list.Add(name);
            }
        }
        foreach (var (clip, list) in found) result.Add(clip, list.AsReadOnly());
        return result;
    }

    /** Name rows of one query: kind 1 is one weighted set; kind 2 is a tag tree whose sets are all taken. */
    private static IEnumerable<int> QueryRows(AnimationControlReader.Data table, int query)
    {
        uint kind = table.U32(query);
        int target = table.Pointer(query + 8);
        int[] sets = kind switch
        {
            1 when table.U32(target - 4) == SingleQueryClass => [target],
            2 when table.U32(target - 4) == TreeQueryClass => table.Rows(target, QuerySetClass, 16),
            _ => throw new InvalidDataException($"Query kind {kind} is not read.")
        };
        return sets.SelectMany(set => table.Rows(set, QueryRowClass, 8)).Select(row => checked((int)table.U32(row)));
    }

    /** The body's selector config, its resource offset and its animation definition; null when it has none. */
    private static (AnimationControlReader.Data Config, int Resource, uint Definition)? Selector(PackageCatalog catalog, uint entityTag)
    {
        if (!IsClass(catalog, entityTag, EntityClass)) throw new NotSupportedException("A stand pick needs an entity.");
        var entity = new AnimationControlReader.Data(catalog.Read(entityTag));
        foreach (int row in entity.Rows(0x10, ComponentRowClass, 12))
        {
            uint configTag = entity.U32(row);
            if (!IsClass(catalog, configTag, ConfigClass)) continue;
            var config = new AnimationControlReader.Data(catalog.Read(configTag));
            int resource = config.Pointer(0x18);
            if (config.U32(resource - 4) != SelectorClass) continue;
            uint definition = config.U32(resource + DefinitionField);
            if (!IsClass(catalog, definition, DefinitionClass))
                throw new InvalidDataException("The stand selector names no animation definition.");
            return (config, resource, definition);
        }
        return null;
    }

    /** Evaluates the stand tree for the tags and returns the clip its set's first weighted row names. */
    private static AnimationStandChoice Pick(PackageCatalog catalog, uint definitionTag, uint vocabularyTag, uint setsTag,
        uint treeTag, uint[] tags)
    {
        var vocabulary = new AnimationControlReader.Data(catalog.Read(vocabularyTag));
        var context = new (uint[] Mask, uint Hash)[Slots];
        for (int slot = 0; slot < Slots; slot++) context[slot] = (Mask(vocabulary, slot, tags[slot]), tags[slot]);
        var tree = new StandTree(new AnimationControlReader.Data(catalog.Read(treeTag)));
        int leaf = tree.Evaluate(context);
        short[] sets = tree.Data.SignedWords(SetMap);
        if ((uint)leaf >= (uint)sets.Length || sets[leaf] < 0) throw new InvalidDataException($"Stand leaf {leaf} names no set.");
        int set = sets[leaf];
        var setData = new AnimationControlReader.Data(catalog.Read(setsTag));
        int[] setRows = setData.Rows(0x18, SetClass, SetStride);
        if (set >= setRows.Length) throw new InvalidDataException($"Stand set {set} is outside its table.");
        int[] weighted = setData.Rows(setRows[set], SetRowClass, SetRowStride);
        if (weighted.Length == 0) throw new InvalidDataException($"Stand set {set} is empty.");
        int nameRow = checked((int)setData.U32(weighted[0]));
        byte[] definition = catalog.Read(definitionTag);
        var rows = AnimationNameRows.Read(definition);
        if ((uint)nameRow >= (uint)rows.Length) throw new InvalidDataException($"Stand set {set} names row {nameRow}, past the definition.");
        int slotIndex = rows[nameRow].Slot;
        var data = new AnimationControlReader.Data(definition);
        int[] clips = data.Rows(8, ClipClass, 4);
        if ((uint)slotIndex >= (uint)clips.Length) throw new InvalidDataException($"Name row {nameRow} names slot {slotIndex}, past the clips.");
        return new(definitionTag, set, nameRow, slotIndex, data.U32(clips[slotIndex]));
    }

    /** The tag's mask in the slot's group; all clear when the slot has no group or the group lacks the tag. */
    private static uint[] Mask(AnimationControlReader.Data vocabulary, int slot, uint hash)
    {
        int group = vocabulary.I16(SlotGroups + 2 * slot);
        int[] groups = vocabulary.Rows(8, GroupClass, 32);
        if (group < 0 || group >= groups.Length) return new uint[4];
        foreach (int row in vocabulary.Rows(groups[group] + 16, TagRowClass, 24))
            if (vocabulary.U32(row + 16) == hash)
                return [vocabulary.U32(row), vocabulary.U32(row + 4), vocabulary.U32(row + 8), vocabulary.U32(row + 12)];
        return new uint[4];
    }

    private static bool IsClass(PackageCatalog catalog, uint tag, uint type) =>
        catalog.TryGetEntry(tag, out var entry) && entry.Reference == type;

    /**
     * The stand tree. A branch is {u8 slot, u8, i16 node}; a node is {u32 mask[4], u32 hash, i16 child,
     * i16 sibling, i16 result}. A result of 0 or more continues at branch result - 1; a negative one is leaf ~result.
     */
    private sealed class StandTree(AnimationControlReader.Data data)
    {
        private readonly int[] nodes = data.Rows(Nodes, NodeClass, NodeStride);
        private readonly int[] branches = data.Rows(Branches, BranchClass, 4);
        public AnimationControlReader.Data Data { get; } = data;

        /** The leaf for this context; the tree's fallback leaf when no branch decides. */
        public int Evaluate((uint[] Mask, uint Hash)[] context)
        {
            int leaf = Branch(0, context, 0);
            return leaf == -1 ? Data.I16(FallbackLeaf) : leaf;
        }

        /** Walks the slot's tag from one node; when the walk decides nothing, the start node's result applies. */
        private int Branch(int index, (uint[] Mask, uint Hash)[] context, int depth)
        {
            if (depth > MaximumDepth) throw new InvalidDataException("The stand tree does not end.");
            int at = branches[Index(index, branches.Length)];
            int slot = Data.U8(at);
            if (slot >= Slots) throw new InvalidDataException($"A stand branch names slot {slot}.");
            int start = nodes[Index(Data.I16(at + 2), nodes.Length)];
            int found = Walk(start, context[slot], context, depth + 1);
            if (found != -1) return found;
            return Result(start, context, depth + 1);
        }

        /**
         * Scans siblings for the tag's hash. A node whose mask the tag's mask holds is searched first;
         * when that search finds nothing the scan stops on that node.
         */
        private int Walk(int node, (uint[] Mask, uint Hash) tag, (uint[] Mask, uint Hash)[] context, int depth)
        {
            if (depth > MaximumDepth) throw new InvalidDataException("The stand tree does not end.");
            int found = -1;
            while (Data.U32(node + 16) != tag.Hash)
            {
                if (Holds(tag.Mask, node))
                {
                    short child = Data.I16(node + 20);
                    if (child != -1) found = Walk(nodes[Index(child, nodes.Length)], tag, context, depth + 1);
                    if (found == -1) break;
                }
                short sibling = Data.I16(node + 22);
                if (sibling == -1) return found;
                node = nodes[Index(sibling, nodes.Length)];
            }
            return Result(node, context, depth + 1);
        }

        private int Result(int node, (uint[] Mask, uint Hash)[] context, int depth)
        {
            short result = Data.I16(node + 24);
            return result >= 0 ? Branch(result - 1, context, depth) : ~result;
        }

        /** Bit 31 of word 3 marks a real mask on both sides. */
        private bool Holds(uint[] mask, int node)
        {
            if ((Data.U32(node + 12) & mask[3] & 0x80000000) == 0) return false;
            for (int word = 0; word < 4; word++)
                if ((Data.U32(node + 4 * word) & ~mask[word]) != 0) return false;
            return true;
        }

        private static int Index(int value, int count) =>
            (uint)value < (uint)count ? value : throw new InvalidDataException($"Stand tree index {value} is outside its array.");
    }
}
