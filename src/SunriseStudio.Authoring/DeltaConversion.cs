using System.Globalization;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

namespace SunriseStudio.Authoring;

/** A raw delta that stays, and why. */
public sealed record KeptDelta(uint Tag, string Reason);

/**
 * Edits replace the Converted raw deltas. The edited rows compile to the same bytes; Rebuilt lists the
 * derived tags whose bytes the current rules write differently, such as collision and cull data.
 */
public sealed record DeltaConversion(IReadOnlyList<ObjectEdit> Edits, IReadOnlyList<uint> Converted,
    IReadOnlyList<KeptDelta> Kept, IReadOnlyList<uint>? Rebuilt = null)
{
    /** One line of counts for the status bar; KeptDetails has the reasons. */
    public string Summary()
    {
        string text = Edits.Count == 0 ? "No placement delta converts."
            : $"Converted {Count(Converted.Count, "delta")} into {Count(Edits.Count, "object edit")}.";
        if (Rebuilt is { Count: > 0 }) text += $" Rebuilt {Count(Rebuilt.Count, "asset")}.";
        return Kept.Count == 0 ? text : text + $" Kept {Kept.Count.ToString(CultureInfo.InvariantCulture)}.";
    }

    /** One line per kept delta, for a tooltip. */
    public string KeptDetails() => string.Join("\n", Kept.Select(item => $"{item.Tag:X8} {item.Reason}"));

    private static string Count(int value, string noun) =>
        value.ToString(CultureInfo.InvariantCulture) + " " + noun + (value == 1 ? "" : "s");
}

/** Turns raw placement deltas into object edits, only when the edited rows compile to exactly the raw bytes. */
public static class DeltaConverter
{
    /** Where one row's editable fields sit. Transform is -1 for a trigger volume. */
    private sealed record RowFields(ObjectEditTarget Target, int Transform, bool Scale);

    private sealed record Group(uint Tag, ObjectEdit[] Edits);

    // The rule rewrites the instance table aggregate box at +0x80 and +0x90.
    private const int AggregateStart = 0x80, AggregateEnd = 0xA0;

    /**
     * Plans the conversion without changing the project.
     * @param compiler Compiler to prove the bytes with; the project's compiler when null.
     * @return The edits to add and the deltas they replace. Every other delta is listed with a reason.
     */
    public static DeltaConversion Plan(StudioProject project, PackageCatalog catalog, IObjectEditCompiler? compiler = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(catalog);
        compiler ??= project.ObjectEditCompiler ?? ObjectEditCompiler.Default;
        var existing = project.ObjectEdits;
        // Throws when the saved object edits do not compile; fix those before converting.
        var before = project.CompileObjectEdits(catalog);
        var claimed = existing.Select(edit => edit.Target.SourceTag).Concat(before.Assets.Keys)
            .Concat(before.Edits.SelectMany(item => item.Tags)).ToHashSet();
        Dictionary<uint, byte[]> originals = [], raws = [];
        byte[] Original(uint tag) => originals.TryGetValue(tag, out var bytes) ? bytes : originals[tag] = catalog.ReadOriginal(tag);
        byte[] Raw(uint tag) => raws.TryGetValue(tag, out var bytes) ? bytes : raws[tag] = project.ReadAsset(tag, catalog);

        var kept = new Dictionary<uint, string>();
        var groups = new List<Group>();
        var derived = new HashSet<uint>();
        foreach (var change in project.Changes)
        {
            uint tag = change.TagHash;
            if (claimed.Contains(tag)) { kept[tag] = "already has object edits."; continue; }
            if (!catalog.TryGetEntry(tag, out var entry)) { kept[tag] = "is not installed."; continue; }
            if (change.EditedSize != change.OriginalSize) { kept[tag] = "changes the asset size."; continue; }
            if (!IsRowClass(entry.Reference)) { derived.Add(tag); continue; }
            try { groups.Add(new(tag, ReadEdits(catalog, tag, entry.Reference, Original(tag), Raw(tag)))); }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException or ArgumentException)
            { kept[tag] = error.Message; }
        }

        ObjectEditCompilation CompileWith(IEnumerable<ObjectEdit> edits, HashSet<uint> drop) =>
            compiler.Compile(existing.Concat(edits).ToArray(),
                tag => (drop.Contains(tag) ? Original(tag) : Raw(tag)).ToArray(), catalog);
        // The edited row tables must keep the bytes they have today. Derived tags may change, because a
        // rule the raw delta predates (collision, cull ids) now writes them; they are listed as rebuilt.
        var rowTags = groups.Select(group => group.Tag).ToHashSet();
        var rebuilt = new HashSet<uint>();
        string? Mismatch(ObjectEditCompilation after, HashSet<uint> drop, HashSet<uint> changedDerived)
        {
            changedDerived.Clear();
            foreach (uint tag in drop.Concat(before.Assets.Keys).Concat(after.Assets.Keys).Distinct().Order())
            {
                byte[] expected = before.Assets.TryGetValue(tag, out var old) ? old : Raw(tag);
                byte[] actual = after.Assets.TryGetValue(tag, out var next) ? next : drop.Contains(tag) ? Original(tag) : Raw(tag);
                if (expected.Length != actual.Length) return $"would change the size of {tag:X8}.";
                int at = expected.AsSpan().CommonPrefixLength(actual);
                if (at == expected.Length) continue;
                if (rowTags.Contains(tag)) return $"compiles to different bytes in {tag:X8} at +0x{at:X}.";
                changedDerived.Add(tag);
            }
            return null;
        }

        var accepted = new List<ObjectEdit>();
        var dropped = new HashSet<uint>();
        var derivedOf = new Dictionary<uint, uint>();
        foreach (var group in groups)
        {
            var drop = dropped.Append(group.Tag).ToHashSet();
            try
            {
                var trial = CompileWith(accepted.Concat(group.Edits), drop);
                var keys = group.Edits.Select(edit => edit.Key).ToHashSet(StringComparer.Ordinal);
                // The group's rule outputs that are raw deltas today are replaced with it.
                foreach (uint tag in trial.Edits.Where(item => keys.Contains(item.Edit.Key)).SelectMany(item => item.Tags)
                    .Where(derived.Contains))
                { drop.Add(tag); derivedOf[tag] = group.Tag; }
                var after = drop.Count == dropped.Count + 1 ? trial : CompileWith(accepted.Concat(group.Edits), drop);
                var changedDerived = new HashSet<uint>();
                if (Mismatch(after, drop, changedDerived) is { } reason) { kept[group.Tag] = reason; continue; }
                accepted.AddRange(group.Edits);
                dropped.UnionWith(drop);
                rebuilt.UnionWith(changedDerived);
            }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException or KeyNotFoundException)
            { kept[group.Tag] = error.Message; }
        }
        foreach (uint tag in derived.Where(tag => !dropped.Contains(tag)))
            kept[tag] = derivedOf.TryGetValue(tag, out uint owner) ? $"is derived from {owner:X8}, which was kept."
                : catalog.TryGetEntry(tag, out var entry) ? $"has no object edit layout for class {entry.Reference:X8}."
                : "has no object edit layout.";
        return new(accepted, dropped.Order().ToArray(),
            kept.OrderBy(pair => pair.Key).Select(pair => new KeptDelta(pair.Key, pair.Value)).ToArray(),
            rebuilt.Order().ToArray());
    }

    private static bool IsRowClass(uint reference) =>
        reference is 0x8080966D or 0x808099D6 or 0x80809C36 or 0x8080713A or 0x80809162;

    /** One edit for each row whose QTS or bounds bytes changed. Any other changed byte refuses the whole delta. */
    private static ObjectEdit[] ReadEdits(PackageCatalog catalog, uint tag, uint reference, byte[] original, byte[] raw)
    {
        if (original.Length != raw.Length) throw new InvalidDataException("changes the asset size.");
        int[] changed = Enumerable.Range(0, raw.Length).Where(i => raw[i] != original[i]).ToArray();
        if (changed.Length == 0) throw new InvalidDataException("changes no byte.");
        var handled = new bool[changed.Length];
        var source = new NativeRows(raw);
        var edits = new List<ObjectEdit>();
        foreach (var row in Rows(tag, reference, new NativeRows(original)))
        {
            int offset = row.Target.Offset;
            int first = LowerBound(changed, offset), end = LowerBound(changed, offset + row.Target.Length);
            bool rotation = false, position = false, scale = false, bounds = false;
            for (int k = first; k < end; k++)
            {
                int at = changed[k];
                if (row.Transform < 0)
                {
                    if (at >= offset + 0xB0 && at < offset + 0xBC || at >= offset + 0xC0 && at < offset + 0xCC)
                        bounds = handled[k] = true;
                }
                else if (at >= row.Transform && at < row.Transform + 16) rotation = handled[k] = true;
                else if (at >= row.Transform + 16 && at < row.Transform + 28) position = handled[k] = true;
                else if (row.Scale && at >= row.Transform + 28 && at < row.Transform + 32) scale = handled[k] = true;
            }
            if (!(rotation || position || scale || bounds)) continue;
            ObjectRows.Locate(catalog, row.Target, original);
            ObjectRows.Locate(catalog, row.Target, raw);
            int t = row.Transform;
            var edit = row.Transform < 0
                ? new ObjectEdit(ObjectEditKind.TriggerBounds, row.Target,
                    Minimum: ObjectEditCompiler.Floats(source.V3(offset + 0xB0)), Maximum: ObjectEditCompiler.Floats(source.V3(offset + 0xC0)))
                // Stored floats are copied as they are, so the compiled bytes match exactly.
                : new ObjectEdit(ObjectEditKind.PlacementTransform, row.Target,
                    Position: position ? ObjectEditCompiler.Floats(source.V3(t + 16)) : null,
                    Rotation: rotation ? ObjectEditCompiler.Floats(source.Q4(t)) : null,
                    Scale: scale ? source.F32(t + 28) : null);
            edit.Validate();
            edits.Add(edit);
        }
        for (int k = 0; k < changed.Length; k++)
            if (!handled[k] && !(reference == 0x8080966D && changed[k] is >= AggregateStart and < AggregateEnd))
                throw new InvalidDataException($"changes byte +0x{changed[k]:X}, which is not a placement field.");
        if (edits.Count == 0) throw new InvalidDataException("changes only derived bytes.");
        return edits.ToArray();
    }

    /** The typed rows of a tag, read from its installed bytes, as the scene decoder reaches them. */
    private static IEnumerable<RowFields> Rows(uint tag, uint reference, NativeRows source)
    {
        RowFields Row(ObjectEditLayout layout, int offset, int index, ulong identity, int transform, bool scale)
        {
            int length = ObjectRows.Length(layout);
            return new(new(tag, layout, offset, length, index, identity,
                BinaryDelta.Hash(source.Span(offset, length))), transform, scale);
        }
        switch (reference)
        {
            case 0x8080966D:
                int[] instances = source.Rows(0x40, 0x808071A3, 48);
                for (int i = 0; i < instances.Length; i++)
                    yield return Row(ObjectEditLayout.StaticInstance, instances[i], i, 0, instances[i], true);
                break;
            case 0x808099D6:
                foreach (int row in source.Rows(8, 0x808099D8, 144))
                    yield return Row(ObjectEditLayout.Placement, row, -1, source.U64(row + 0x70), row + 16, true);
                break;
            case 0x80809C36:
                foreach (int descriptor in ObjectRows.SlotDescriptors(source, tag))
                    foreach (int row in source.Rows(descriptor + 0x58, 0x808099D8, 144))
                        yield return Row(ObjectEditLayout.SlotPlacement, row, -1, source.U64(row + 0x70), row + 16, true);
                foreach (int row in TriggerRows(source, tag))
                    yield return Row(ObjectEditLayout.TriggerVolume, row, -1, 0, -1, false);
                break;
            case 0x8080713A:
                int[] lights = source.Rows(0x40, 0x80809F75, 32);
                for (int i = 0; i < lights.Length; i++)
                    yield return Row(ObjectEditLayout.Light, lights[i], i, 0, lights[i], true);
                break;
            case 0x80809162:
                foreach (int row in source.Rows(8, 0x80809164, 48))
                    yield return Row(ObjectEditLayout.SpawnPoint, row, -1, source.U32(row + 32), row, false);
                break;
        }
    }

    /** Volume rows of the trigger components a config owns; ObjectRows.Locate checks each one. */
    private static IEnumerable<int> TriggerRows(NativeRows source, uint tag)
    {
        foreach (int component in source.Rows(0x30, 0x808091A4, 16))
        {
            if (source.U32(component + 8) != 0x808099C8) continue;
            int header = checked(component + source.I32(component));
            if (source.U32(header) != tag || source.U32(header + 4) != 0x808099C9) continue;
            foreach (int row in source.Rows(checked((int)source.U64(header + 8)) + 0x38, 0x808099D0, 0x120)) yield return row;
        }
    }

    private static int LowerBound(int[] sorted, int value)
    {
        int index = Array.BinarySearch(sorted, value);
        return index >= 0 ? index : ~index;
    }
}
