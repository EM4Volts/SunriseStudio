using System.Buffers.Binary;
using SunriseStudio.Formats;

/** Checks every decoded perk node kind: fresh writes, in-place edits and the shared header rules. */
internal static class PerkKindChecks
{
    private const uint EmptyHash = 0x811C9DC5;
    private const uint StateClass = 0x80803DCE, GatedClass = 0x80803DCC, DamageClass = 0x80802F5F;
    private const uint CounterClass = 0x80803E30;
    private const uint AllGroupsClass = 0x80803E04, ExtendTimersClass = 0x80803E3B, ResistClass = 0x80803E3C;

    /** Builds one program with every new kind, then reads, edits and rewrites it. */
    public static void Run()
    {
        var graph = NativePerkBehavior.New();
        var kill = Node("kill", ("min_share", 0.01), ("slots", 4));
        var damage = Node("damage", ("probability", 0.5), ("this_weapon", 0));
        var unequip = Node("unequip", ("slots", 1));
        var gameEvent = Node("event", ("event", 0xA6288DD1));
        var state = Node("while-state", ("property", Hash("super_active")), ("hold", 2), ("invert", 1));
        var gatedKill = Node("state-kill", ("property", Hash("is_arc")), ("this_weapon_active", 1));
        var gatedDamage = Node("state-damage");
        var gatedUnequip = Node("state-unequip");
        var gatedEvent = Node("state-event");
        Set(gatedEvent.Children[0], "event", 0x936722B1);
        var counter = Node("counter", ("threshold", 2), ("reset", 5));
        counter.Children.Add(Row("kill", ("row_value", 1)));
        counter.Children.Add(Row("state-damage", ("row_mode", 1), ("row_value", 3), ("row_window", 4)));
        counter.Children.Add(Row("event", ("row_mode", 2), ("row_value", 0.5)));
        Set(counter.Children[2], "event", 0x46A2E7BB);
        graph.Activation.AddRange([kill, damage, unequip, gameEvent, state, gatedKill, gatedDamage, gatedUnequip, gatedEvent, counter]);
        graph.Effects.AddRange([
            Node("spawn", ("definition", 0x80C197BA), ("target", 1), ("counter_parameter", 0x16EF9AA5)),
            Node("driven-spawn", ("definition", 0x80C19175), ("input", 1), ("normalize", 1)),
            Node("ability-energy", ("slot", 1), ("gate", 1), ("amount", 0.25), ("cap", 0.75)),
            Node("damage-multiplier", ("bonus", 0.25), ("player_bonus", 0.1)),
            Node("perk-counter", ("mode", 0), ("value", 2)),
            Node("transmat", ("key", 0xF8EFC9A9))]);
        graph.Deactivation.Add(Node("timer"));
        byte[] bytes = NativePerkBehavior.Compile(graph);
        var decoded = NativePerkBehavior.Decompile(bytes);
        Check(Same(graph.Activation, decoded.Activation) && Same(graph.Effects, decoded.Effects)
            && Same(graph.Deactivation, decoded.Deactivation), "A fresh node did not decode to its authored values.");
        Check(NativePerkBehavior.Compile(decoded).SequenceEqual(bytes), "A fresh program with every kind did not round trip.");
        var rules = Rules(bytes);
        Check(rules.Count == 0, "Fresh program broke a header rule: " + string.Join("; ", rules));
        ulong activation = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(136));
        ulong expected = Bit(2) | Bit(4) | Bit(17) | Bit(30) | Bit(20) | Bit(35) | Bit(26);
        Check(activation == expected, $"Activation mask is 0x{activation:X} instead of 0x{expected:X}.");
        int counterAt = decoded.Activation[9].OriginalOffset;
        Check(BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(counterAt + 24)) == (Bit(2) | Bit(35) | Bit(4) | Bit(30)),
            "Counter child mask does not list every row's events.");
        Check(bytes[decoded.Activation[4].OriginalOffset + 6] == 1 && bytes[decoded.Activation[5].OriginalOffset + 6] == 1
            && bytes[decoded.Activation[5].Children[0].OriginalOffset + 6] == 0 && bytes[decoded.Activation[6].OriginalOffset + 6] == 0
            && bytes[decoded.Activation[1].OriginalOffset + 6] == 0, "State-record byte +6 was not derived.");

        // A row edit changes only the row's value; a property edit flips +6 with the hash.
        var edit = NativePerkBehavior.Decompile(bytes);
        Set(edit.Activation[9].Children[1], "row_value", 7);
        byte[] rowEdit = NativePerkBehavior.Compile(edit);
        int rows = Target(bytes, counterAt + 16) + 16;
        Check(OnlyChanged(bytes, rowEdit, rows + 32 + 12, 4), "A row edit changed unrelated bytes.");
        var stateEdit = NativePerkBehavior.Decompile(bytes);
        Set(stateEdit.Activation[6], "property", Hash("reloading"));
        byte[] owned = NativePerkBehavior.Compile(stateEdit);
        int gatedAt = stateEdit.Activation[6].OriginalOffset;
        Check(owned[gatedAt + 6] == 1 && OnlyChanged(bytes, owned, gatedAt + 6, 1, gatedAt + 212, 4),
            "Setting a state property did not set exactly +6 and the hash.");
        var expression = NativePerkBehavior.Decompile(bytes);
        Set(expression.Effects[2], "expression", 1);
        var pass = NativePerkBehavior.Decompile(NativePerkBehavior.Compile(expression)).Effects[2];
        Check(Value(pass, "expression") == 1 && Value(pass, "amount") == 0.25, "An expression form change did not decode.");

        // New conditions get indices after every existing one, cooldown and nested ones included.
        var extended = NativePerkBehavior.Decompile(bytes);
        extended.Deactivation.Add(Node("kill"));
        byte[] more = NativePerkBehavior.Compile(extended);
        Check(Rules(more).Count == 0 && NativePerkBehavior.Decompile(more).Deactivation[1].NativeBytes[7]
            == Indices(bytes).Max() + 1, "An appended condition reused a condition index.");
        byte[] stacked = (byte[])bytes.Clone();
        stacked[104] = 1;
        var stackGraph = NativePerkBehavior.Decompile(stacked);
        Check(NativePerkBehavior.Compile(stackGraph).SequenceEqual(stacked), "A stack-branch program did not round trip.");
        stackGraph.Activation.Add(Node("event", ("event", 0xA6288DD1)));
        Reject(() => NativePerkBehavior.Compile(stackGraph));

        // Refusals: unset required values, wrong row kinds, rows on an installed counter, read-only inputs.
        Reject(() => Compile(Node("spawn")));
        Reject(() => Compile(Node("transmat")));
        Reject(() => Compile(Node("damage-multiplier", ("any_element", 0))));
        Reject(() => Compile(Node("ability-energy", ("slot", 3))));
        RejectCondition(Node("event"));
        RejectCondition(Node("counter"));
        RejectCondition(Node("while-state", ("property", 0x12345678)));
        Reject(() => NativePerkBehavior.CreateRow("always"));
        var installedCounter = NativePerkBehavior.Decompile(bytes);
        installedCounter.Activation[9].Children.Add(Row("kill"));
        Reject(() => NativePerkBehavior.Compile(installedCounter));
        var structure = NativePerkBehavior.Decompile(bytes);
        structure.Activation[5].Children.Clear();
        Reject(() => NativePerkBehavior.Compile(structure));
        byte[] filtered = (byte[])bytes.Clone();
        filtered[decoded.Activation[0].OriginalOffset + 312] = 0;
        // Kind 0 with no rows and no compiled filter is not a form the compiler writes.
        filtered[decoded.Activation[0].OriginalOffset + 88] = 0;
        var readOnly = NativePerkBehavior.Decompile(filtered);
        Check(NativePerkBehavior.Compile(readOnly).SequenceEqual(filtered), "A filtered kill node did not round trip.");
        var info = readOnly.Activation[0].Properties.Single(row => row.Key == "other_filters");
        Check(!info.Editable && info.Value == 1 && !readOnly.Activation[0].LabelFilters[0].Editable,
            "A non-default kill filter was not reported read-only.");
        info.Value = 0;
        Reject(() => NativePerkBehavior.Compile(readOnly));
        var lockedFilter = NativePerkBehavior.Decompile(filtered);
        lockedFilter.Activation[0].LabelFilters[0].AnyOf.Add(0x12345678);
        Reject(() => NativePerkBehavior.Compile(lockedFilter));
        Console.WriteLine("perk_kinds fresh=pass roundtrip=pass rules=pass row_edit=pass state_owner=pass "
            + "expression=pass indices=pass stack=reject refusals=pass readonly=reject");
        Hud();
        TableKinds();
        LabelFilters();
    }

    /** A HUD row in the form Compile writes decodes and round trips; any other form stays preserved. */
    private static void Hud()
    {
        var graph = NativePerkBehavior.New();
        graph.Activation.Add(NativePerkBehavior.CreateNode("always"));
        graph.Effects.Add(NativePerkBehavior.CreateNode("perk-counter"));
        graph.Hud = new() { Label = (uint)NativePerkBehavior.HudLabels[0].Value, ShowTimer = false };
        byte[] bytes = NativePerkBehavior.Compile(graph);
        var decoded = NativePerkBehavior.Decompile(bytes);
        Check(decoded.Hud is { ShowTimer: false } && decoded.Diagnostics.Count == 0
            && NativePerkBehavior.Compile(decoded).SequenceEqual(bytes), "A HUD row did not round trip.");
        decoded.Hud = new() { Label = decoded.Hud!.Label, ShowTimer = true };
        decoded.Deactivation.Add(NativePerkBehavior.CreateNode("timer"));
        byte[] edited = NativePerkBehavior.Compile(decoded);
        Check(BinaryPrimitives.ReadInt64LittleEndian(edited.AsSpan(16)) == 1
            && NativePerkBehavior.Decompile(edited).Hud is { ShowTimer: true }, "A HUD edit added a second row.");
        byte[] foreign = (byte[])bytes.Clone();
        foreign[Target(foreign, Target(foreign, 24) + 16) + 3] = 1;
        var kept = NativePerkBehavior.Decompile(foreign);
        Check(kept.Hud is null && kept.Diagnostics.Count == 1 && NativePerkBehavior.Compile(kept).SequenceEqual(foreign),
            "A HUD row in another form was not preserved.");
        Console.WriteLine("perk_hud roundtrip=pass edit=in_place other_form=preserved");
    }

    /** Fresh label filters in each native form, an edit, and the refusal of an unknown label. */
    private static void LabelFilters()
    {
        var before = PerkLabelTable.Installed;
        try
        {
            byte[] groupMask = new byte[40];
            groupMask[0] = 0x0A;
            uint first = 0x11111111, second = 0x22222222, third = 0x33333333, group = 0x44444444;
            PerkLabelTable.Installed = new PerkLabelTable([first, second, third], [(group, groupMask)]);
            var graph = NativePerkBehavior.New();
            var anyNone = NativePerkBehavior.CreateNode("kill");
            var victim = anyNone.LabelFilters.Single(filter => filter.Key == "victim");
            victim.AnyOf.AddRange([first, group]);
            victim.NoneOf.Add(third);
            var allOf = NativePerkBehavior.CreateNode("kill");
            allOf.LabelFilters.Single(filter => filter.Key == "victim").AllOf.Add(second);
            graph.Activation.AddRange([anyNone, allOf, NativePerkBehavior.CreateNode("kill")]);
            graph.Effects.Add(NativePerkBehavior.CreateNode("perk-counter"));
            byte[] bytes = NativePerkBehavior.Compile(graph);
            var decoded = NativePerkBehavior.Decompile(bytes);
            int a = decoded.Activation[0].OriginalOffset, b = decoded.Activation[1].OriginalOffset, c = decoded.Activation[2].OriginalOffset;
            Check(bytes[a + 88] == 0 && bytes[b + 88] == 1 && bytes[c + 88] == 0xFF && Target(bytes, c + 96) == c + 96,
                "Label filter kinds do not follow the native rule.");
            int body = Target(bytes, a + 96);
            Check(U32(bytes, body - 4) == 0x808093F6 && bytes[body] == 0x0B && bytes[body + 40] == 0x04
                && bytes[body + 80] == 0 && bytes[body + 81] == 0, "A kind 0 filter has the wrong masks or flags.");
            int bodyOne = Target(bytes, b + 96);
            Check(U32(bytes, bodyOne - 4) == 0x808093F5 && bytes[bodyOne + 40] == 0x02
                && bytes.Skip(bodyOne + 160).Take(4).SequenceEqual(new byte[] { 1, 0, 1, 1 }), "A kind 1 filter has the wrong masks or flags.");
            int name = Target(bytes, c + 8 + 64);
            Check(U32(bytes, c + 8 + 72) == PerkLabelTable.Tag && System.Text.Encoding.ASCII.GetString(bytes, name, 13) == "content/commo",
                "A new label set does not name the installed label table.");
            Check(decoded.Activation[0].LabelFilters[0].AnyOf.SequenceEqual([first, group])
                && NativePerkBehavior.Compile(decoded).SequenceEqual(bytes), "A fresh label filter did not round trip.");

            // An edit rewrites rows and compiled filter; clearing every list restores the no-filter form.
            var edit = NativePerkBehavior.Decompile(bytes);
            edit.Activation[0].LabelFilters[0].AnyOf.Clear();
            edit.Activation[0].LabelFilters[0].NoneOf.Clear();
            edit.Activation[2].LabelFilters[1].NotAllOf.Add(second);
            byte[] edited = NativePerkBehavior.Compile(edit);
            Check(edited[a + 88] == 0xFF && Target(edited, a + 96) == a + 96 && edited[c + 296] == 1
                && NativePerkBehavior.Decompile(edited).Activation[2].LabelFilters[1].NotAllOf.Single() == second,
                "A label filter edit did not rewrite its native form.");
            var unknown = NativePerkBehavior.Decompile(bytes);
            unknown.Activation[2].LabelFilters[0].AnyOf.Add(0xDEADBEEF);
            Reject(() => NativePerkBehavior.Compile(unknown));
            PerkLabelTable.Installed = null;
            var noTable = NativePerkBehavior.Decompile(bytes);
            noTable.Activation[2].LabelFilters[0].AnyOf.Add(first);
            Reject(() => NativePerkBehavior.Compile(noTable));
            Console.WriteLine("perk_labels kind0=pass kind1=pass none=pass label_table_ref=pass edit=pass unknown=reject no_table=reject");
        }
        finally { PerkLabelTable.Installed = before; }
    }

    /** Recompiles every installed label filter from its rows, then edits each one away and back through Compile. */
    public static void LabelCorpus(IEnumerable<(uint Tag, byte[] Bytes)> programs, PerkLabelTable table)
    {
        var before = PerkLabelTable.Installed;
        PerkLabelTable.Installed = table;
        int filters = 0, compiled = 0, edited = 0, emptyOnly = 0;
        var failures = new List<string>();
        try
        {
            foreach (var (tag, bytes) in programs)
            {
                var graph = NativePerkBehavior.Decompile(bytes);
                var nodes = graph.Activation.Concat(graph.Effects).Concat(graph.Deactivation).SelectMany(Flatten).ToArray();
                // Kind byte and compiled pointer of each filter, in decode order.
                var sites = new List<(PerkBehaviorNode Node, PerkLabelFilter Filter, int Kind)>();
                foreach (var node in nodes)
                    foreach (var (filter, kind) in node.LabelFilters.Zip(KindOffsets(node.NativeClass)))
                    {
                        filters++;
                        sites.Add((node, filter, node.OriginalOffset + kind));
                        var result = NativePerkBehavior.CompileLabelFilter(table, filter.Arrays());
                        int at = node.OriginalOffset + kind;
                        bool same = result is { } r && r.Kind == bytes[at] && (r.Kind == 0xFF
                            ? BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(at + 8)) == 0
                            : bytes.AsSpan(Target(bytes, at + 8), r.Body.Length).SequenceEqual(r.Body));
                        if (same) compiled++; else failures.Add($"{tag:X8}:{node.Title}:{filter.Key}:compile");
                    }
                if (sites.All(site => site.Filter.Arrays().All(array => array.Count == 0))) { emptyOnly += sites.Count; continue; }
                // Remove the last label of each non-empty filter, compile, then put it back and compile again.
                var removed = NativePerkBehavior.Decompile(bytes);
                var taken = new List<(int Node, string Key, int Array, uint Hash)>();
                foreach (var node in removed.Activation.Concat(removed.Effects).Concat(removed.Deactivation).SelectMany(Flatten))
                    foreach (var filter in node.LabelFilters)
                    {
                        var arrays = filter.Arrays();
                        int last = System.Array.FindLastIndex(arrays, list => list.Count != 0);
                        if (last < 0) continue;
                        taken.Add((node.OriginalOffset, filter.Key, last, arrays[last][^1]));
                        arrays[last].RemoveAt(arrays[last].Count - 1);
                    }
                var restored = NativePerkBehavior.Decompile(NativePerkBehavior.Compile(removed));
                foreach (var (offset, key, array, hash) in taken)
                    restored.Activation.Concat(restored.Effects).Concat(restored.Deactivation).SelectMany(Flatten)
                        .First(node => node.OriginalOffset == offset).LabelFilters.First(filter => filter.Key == key).Arrays()[array].Add(hash);
                byte[] again = NativePerkBehavior.Compile(restored);
                var final = NativePerkBehavior.Decompile(again).Activation.Concat(NativePerkBehavior.Decompile(again).Effects)
                    .Concat(NativePerkBehavior.Decompile(again).Deactivation).SelectMany(Flatten).ToArray();
                foreach (var (node, filter, at) in sites)
                {
                    var after = final.First(row => row.OriginalOffset == node.OriginalOffset).LabelFilters.First(row => row.Key == filter.Key);
                    bool same = after.Arrays().Zip(filter.Arrays()).All(pair => pair.First.SequenceEqual(pair.Second))
                        && again[at] == bytes[at] && (bytes[at] == 0xFF || again.AsSpan(Target(again, at + 8), bytes[at] == 0 ? 84 : 164)
                            .SequenceEqual(bytes.AsSpan(Target(bytes, at + 8), bytes[at] == 0 ? 84 : 164)));
                    if (same) edited++; else failures.Add($"{tag:X8}:{node.Title}:{filter.Key}:edit");
                }
            }
        }
        finally { PerkLabelTable.Installed = before; }
        Console.WriteLine($"perk_label_corpus filters={filters} recompiled={compiled} edit_roundtrip={edited} not_edited_empty_programs={emptyOnly} failures={failures.Count}");
        foreach (string failure in failures.Take(20)) Console.WriteLine(failure);
        Check(failures.Count == 0, "Some installed label filters did not recompile or survive an edit.");
    }

    // Filter kind offsets per class, in the decoder's site order; the compiled pointer is at kind +8.
    private static int[] KindOffsets(uint type) => type switch
    {
        0x80802F16 => [160, 272],
        0x80803DDB or 0x80803DF5 or 0x80803DFA or 0x80803DF7 or 0x80803DDE or 0x80803DDD or 0x80803DE2 or 0x808029E0 => [96],
        0x80803DE7 => [88, 296],
        0x80802F5F or 0x80803E1A or 0x80803DDC => [128],
        0x80803E3F or 0x80803E3E or 0x808029EC or 0x8080281C or 0x80803DF8 => [88],
        0x80803E3C => [136],
        0x80803DE5 => [88, 256],
        0x80803DFC => [88, 184, 320],
        _ => []
    };

    // Overload's label hash is row 303 of the installed label table.
    private const double Overload = 303 * 4294967296.0 + 0x6DC29533;
    private const uint ProgramTag = 0x80BBC7D0;

    /** One program with every writable table-driven kind, plus groups, timer lists, labels and self references. */
    private static void TableKinds()
    {
        var graph = NativePerkBehavior.New();
        graph.ProgramTag = ProgramTag;
        var skip = new HashSet<string>(StringComparer.Ordinal) { "all-groups", "extend-timers" };
        var original = new HashSet<string>(StringComparer.Ordinal)
        {
            "always", "timer", "kill", "damage", "unequip", "while-state", "counter", "event", "state-kill", "state-damage",
            "state-unequip", "state-event", "spawn", "driven-spawn", "ability-energy", "property", "ammo-drop",
            "damage-multiplier", "perk-counter", "transmat"
        };
        foreach (var kind in NativePerkBehavior.Primitives.Where(row => row.CanCreate && !original.Contains(row.Key) && !skip.Contains(row.Key)))
        {
            var node = Filled(NativePerkBehavior.CreateNode(kind.Key));
            (kind.Kind == PerkBehaviorNodeKind.Condition ? graph.Activation : graph.Effects).Add(node);
        }
        var groups = NativePerkBehavior.CreateNode("all-groups");
        var timed = NativePerkBehavior.CreateChild(groups, "group");
        Set(timed, "window", 2);
        timed.Children.Add(NativePerkBehavior.CreateChild(timed, "kill"));
        timed.Children.Add(Filled(NativePerkBehavior.CreateChild(timed, "state-ammo-pickup")));
        var untimed = NativePerkBehavior.CreateChild(groups, "group");
        untimed.Children.Add(NativePerkBehavior.CreateChild(untimed, "aim"));
        groups.Children.AddRange([timed, untimed]);
        graph.Activation.Add(groups);
        var extend = NativePerkBehavior.CreateNode("extend-timers");
        extend.Children.Add(NativePerkBehavior.CreateChild(extend, "kill"));
        extend.Children.Add(Filled(NativePerkBehavior.CreateChild(extend, "ammo-pickup")));
        graph.Effects.Add(extend);
        graph.Deactivation.Add(NativePerkBehavior.CreateNode("timer"));
        byte[] bytes = NativePerkBehavior.Compile(graph);
        var decoded = NativePerkBehavior.Decompile(bytes);
        Check(Same(graph.Activation, decoded.Activation) && Same(graph.Effects, decoded.Effects),
            "A table-driven node did not decode to its authored values.");
        Check(decoded.ProgramTag == ProgramTag && NativePerkBehavior.Compile(decoded).SequenceEqual(bytes),
            "A program with every table-driven kind did not round trip.");
        var rules = Rules(bytes, ProgramTag);
        Check(rules.Count == 0, "Table-driven program broke a rule: " + string.Join("; ", rules));
        int extendIndex = decoded.Effects.FindIndex(node => node.Primitive == "extend-timers");
        Check(BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(160)) == 1UL << extendIndex, "Root +160 lacks the extend-timers bit.");
        int labels = decoded.Effects.Single(node => node.Primitive == "damage-labels").OriginalOffset;
        Check(bytes[labels + 168 + 303 / 8] == 1 << 303 % 8 && bytes.Skip(labels + 168).Take(40).Count(value => value != 0) == 1
            && U32(bytes, Target(bytes, labels + 160) + 16) == 0x6DC29533, "A label row and its bit do not match.");
        foreach (var node in decoded.Activation.Concat(decoded.Effects).SelectMany(Flatten))
            Check(node.Primitive is not ("native" or "native-condition") && node.Properties.All(row => row.Editable),
                $"{node.Title} decoded with read-only parts.");

        // A label edit rewrites the row and the bit; the owner multiplier needs the program tag.
        var edit = NativePerkBehavior.Decompile(bytes);
        Set(edit.Effects.Single(node => node.Primitive == "damage-labels"), "label", 2 * 4294967296.0 + 0xBB8580DB);
        byte[] relabeled = NativePerkBehavior.Compile(edit);
        Check(relabeled[labels + 168] == 4 && relabeled.Skip(labels + 168).Take(40).Count(value => value != 0) == 1,
            "A label edit left the old bit set.");
        var noTag = NativePerkBehavior.New();
        noTag.Activation.Add(NativePerkBehavior.CreateNode("always"));
        noTag.Effects.Add(NativePerkBehavior.CreateNode("damage-resist"));
        Reject(() => NativePerkBehavior.Compile(noTag));
        var order = NativePerkBehavior.Decompile(bytes);
        order.Activation.Add(NativePerkBehavior.CreateNode("all-groups"));
        var late = order.Activation[^1];
        var first = NativePerkBehavior.CreateChild(late, "group");
        first.Children.Add(NativePerkBehavior.CreateChild(first, "kill"));
        var second = NativePerkBehavior.CreateChild(late, "group");
        Set(second, "window", 1);
        second.Children.Add(NativePerkBehavior.CreateChild(second, "kill"));
        late.Children.AddRange([first, second]);
        Reject(() => NativePerkBehavior.Compile(order));
        Reject(() => NativePerkBehavior.CreateChild(extend, "while-state"));
        Reject(() => Compile(NativePerkBehavior.CreateNode("event-labels")));
        int movement = decoded.Effects.Single(node => node.Primitive == "movement").OriginalOffset;
        int block = Target(bytes, movement + 8);
        Check(U32(bytes, block - 4) == 0x8080692C && bytes[block + 4] == 1 && bytes[block + 5] == 1
            && BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(block + 12)) == -1, "A new movement block lost its defaults.");
        var scale = edit.Effects.Single(node => node.Primitive == "stat-scale");
        Set(scale, "expression", 0);
        Set(scale, "constant", 2.5);
        var scaled = NativePerkBehavior.Decompile(NativePerkBehavior.Compile(edit)).Effects.Single(node => node.Primitive == "stat-scale");
        Check(Value(scaled, "expression") == 0 && Value(scaled, "constant") == 2.5 && Value(scaled, "name") == 0x12345678,
            "A component scale expression edit did not decode.");
        Console.WriteLine($"perk_table_kinds nodes={decoded.Activation.Concat(decoded.Effects).SelectMany(Flatten).Count()} "
            + "fresh=pass roundtrip=pass rules=pass root160=pass labels=pass selfref=pass groups=pass refusals=pass");
    }

    /** Sets every required input to a value that passes validation. */
    private static PerkBehaviorNode Filled(PerkBehaviorNode node)
    {
        foreach (var target in Flatten(node))
            foreach (var property in target.Properties)
                if (property.Picker.Length != 0 || property.Kind == PerkBehaviorValueKind.Label)
                    property.Value = property.Kind switch
                    {
                        PerkBehaviorValueKind.Label => Overload,
                        PerkBehaviorValueKind.Tag => 0x80C197BA,
                        _ => property.Key == "property" ? property.Value : 0x12345678
                    };
                else if (property.Kind == PerkBehaviorValueKind.Hash && property.Key is "flag" or "tag") property.Value = 0x5A5BA104;
        return node;
    }

    /** Decodes and rewrites each installed program; counts by node kind and checks the header rules. */
    public static void Corpus(IEnumerable<(uint Tag, byte[] Bytes)> programs)
    {
        var kinds = new SortedDictionary<string, (int Nodes, int Pass, int Fail)>(StringComparer.Ordinal);
        var failures = new List<string>();
        int total = 0, passed = 0, ruleFailures = 0;
        foreach (var (tag, bytes) in programs)
        {
            total++;
            bool ok;
            PerkBehaviorGraph? graph = null;
            try
            {
                graph = NativePerkBehavior.Decompile(bytes);
                ok = NativePerkBehavior.Compile(graph).SequenceEqual(bytes);
                if (!ok) failures.Add($"{tag:X8}:differs");
            }
            catch (Exception error) when (error is InvalidDataException or OverflowException or ArgumentException)
            { ok = false; failures.Add($"{tag:X8}:{error.Message.Split('\n')[0]}"); }
            if (ok) passed++;
            var rules = Rules(bytes, tag);
            if (rules.Count != 0) { ruleFailures++; failures.Add($"{tag:X8}:rule:{rules[0]}"); }
            if (graph is null) continue;
            foreach (var group in graph.Activation.Concat(graph.Effects).Concat(graph.Deactivation).SelectMany(Flatten)
                .GroupBy(node => node.Primitive))
            {
                var row = kinds.GetValueOrDefault(group.Key);
                kinds[group.Key] = (row.Nodes + group.Count(), row.Pass + (ok ? 1 : 0), row.Fail + (ok ? 0 : 1));
            }
        }
        foreach (var (kind, row) in kinds)
            Console.WriteLine($"perk_census kind={kind} nodes={row.Nodes} programs_pass={row.Pass} programs_fail={row.Fail}");
        Console.WriteLine($"perk_census programs={total} roundtrip={passed} rule_failures={ruleFailures} failures={failures.Count}");
        foreach (string failure in failures.Take(20)) Console.WriteLine(failure);
        Check(failures.Count == 0, "Some installed perk programs did not round trip or broke a header rule.");
    }

    /**
     * Rules read from raw bytes, independent of the decoder: +6, +7, event masks, group and timer-list masks,
     * effect byte +1, root +160 and, when the tag is known, damage-resist self references.
     */
    private static List<string> Rules(byte[] bytes, uint tag = 0)
    {
        var problems = new List<string>();
        var indices = new Dictionary<int, int>();
        foreach (var (field, mask) in new[] { (32, 136), (72, 144), (88, 152) })
        {
            ulong bits = 0;
            foreach (int at in Array(bytes, field, 8)) bits |= Visit(at, Role.Indexed, depth: 0);
            if ((BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(mask)) & bits) != bits)
                problems.Add($"phase +{field} mask lacks 0x{bits:X}");
        }
        if (indices.Values.Distinct().Count() != indices.Count) problems.Add("+7 repeats");
        var effects = Array(bytes, 56, 8).ToArray();
        ulong timers = 0;
        for (int i = 0; i < effects.Length; i++)
        {
            int at = effects[i];
            uint type = U32(bytes, at - 4);
            if (bytes[at + 1] != StateFlag(bytes[at])) problems.Add($"effect +1 at {at}");
            if (type == ExtendTimersClass)
            {
                if (i < 64) timers |= 1UL << i;
                ulong children = 0;
                foreach (int child in Array(bytes, at + 16, 8)) children |= Visit(child, Role.Timer, 1);
                if (BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at + 32)) != children) problems.Add($"timer mask at {at}");
            }
            if (type == ResistClass && tag != 0 && (U32(bytes, at + 160) != ResistClass || U32(bytes, at + 168) != tag
                || BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at + 176)) != (ulong)at))
                problems.Add($"self reference at {at}");
        }
        if (BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(160)) != timers) problems.Add("root +160");
        return problems;

        ulong Visit(int at, Role role, int depth)
        {
            if (depth > 8) { problems.Add("nesting too deep"); return 0; }
            uint type = U32(bytes, at - 4);
            if (role == Role.Timer)
            {
                if (bytes[at + 7] != 0xFF || bytes[at + 6] != 0) problems.Add($"timer condition header at {at}");
            }
            else indices.TryAdd(at, bytes[at + 7]);
            bool owns = role == Role.Indexed && (type is StateClass or GatedClass && U32(bytes, at + 212) != EmptyHash
                || type == DamageClass && BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(at + 200)) != 0);
            if (role != Role.Timer && bytes[at + 6] != (owns ? 1 : 0)) problems.Add($"+6 at {at}");
            ulong bits = Bit(bytes[at + 5]);
            var child = role == Role.Timer ? Role.Timer : Role.Indexed;
            if (type == GatedClass) bits |= Visit(Target(bytes, at + 256), role == Role.Timer ? Role.Timer : Role.Inner, depth + 1);
            if (type == CounterClass)
            {
                ulong children = 0;
                foreach (int row in Array(bytes, at + 8, 32)) children |= Visit(row, child, depth + 1);
                if ((BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at + 24)) & children) != children)
                    problems.Add($"counter +24 at {at}");
                bits |= children;
            }
            if (type == AllGroupsClass)
            {
                bool untimed = false;
                foreach (int group in Rows(bytes, at + 8, 32))
                {
                    float window = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(group));
                    if (window > 0 && untimed) problems.Add($"timed group after untimed at {at}");
                    untimed |= window <= 0;
                    ulong members = 0;
                    foreach (int member in Array(bytes, group + 8, 8)) members |= Visit(member, child, depth + 1);
                    if (BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(group + 24)) != members) problems.Add($"group mask at {group}");
                    bits |= members;
                }
            }
            return bits;
        }
    }

    private enum Role { Indexed, Inner, Timer }

    private static byte StateFlag(int opcode) =>
        opcode is 0 or 1 or 2 or 6 or 7 or 10 or 17 or 18 or 22 or 25 or 26 or 28 or 29 or 30 or 33 or 35 or 41 or 52
            ? (byte)1 : (byte)0;

    private static List<int> Indices(byte[] bytes)
    {
        var result = new List<int>();
        var seen = new HashSet<int>();
        foreach (int field in new[] { 32, 72, 88 })
            foreach (int at in Array(bytes, field, 8)) Walk(at);
        return result;

        void Walk(int at)
        {
            if (!seen.Add(at)) return;
            result.Add(bytes[at + 7]);
            uint type = U32(bytes, at - 4);
            if (type == GatedClass) Walk(Target(bytes, at + 256));
            if (type == CounterClass) foreach (int child in Array(bytes, at + 8, 32)) Walk(child);
            if (type == AllGroupsClass)
                foreach (int group in Rows(bytes, at + 8, 32))
                    foreach (int member in Array(bytes, group + 8, 8)) Walk(member);
        }
    }

    private static IEnumerable<int> Rows(byte[] bytes, int field, int stride)
    {
        long count = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(field));
        if (count == 0) yield break;
        int data = Target(bytes, field + 8) + 16;
        for (int i = 0; i < count; i++) yield return data + i * stride;
    }

    private static IEnumerable<int> Array(byte[] bytes, int field, int stride)
    {
        long count = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(field));
        if (count == 0) yield break;
        int data = Target(bytes, field + 8) + 16;
        for (int i = 0; i < count; i++) yield return Target(bytes, data + i * stride);
    }

    private static PerkBehaviorNode Node(string primitive, params (string Key, double Value)[] values)
    {
        var node = NativePerkBehavior.CreateNode(primitive);
        foreach (var (key, value) in values) Set(node, key, value);
        return node;
    }

    private static PerkBehaviorNode Row(string primitive, params (string Key, double Value)[] values)
    {
        var node = NativePerkBehavior.CreateRow(primitive);
        foreach (var (key, value) in values) Set(node, key, value);
        return node;
    }

    private static void Compile(PerkBehaviorNode effect)
    {
        var graph = NativePerkBehavior.New();
        graph.Activation.Add(NativePerkBehavior.CreateNode("always"));
        graph.Effects.Add(effect);
        NativePerkBehavior.Compile(graph);
    }

    private static void RejectCondition(PerkBehaviorNode condition) => Reject(() =>
    {
        var graph = NativePerkBehavior.New();
        graph.Activation.Add(condition);
        graph.Effects.Add(NativePerkBehavior.CreateNode("perk-counter"));
        NativePerkBehavior.Compile(graph);
    });

    private static bool Same(List<PerkBehaviorNode> authored, List<PerkBehaviorNode> decoded) =>
        authored.Count == decoded.Count && authored.Zip(decoded).All(pair => pair.First.Primitive == pair.Second.Primitive
            && pair.First.Properties.Select(row => (row.Key, (float)row.Value))
                .SequenceEqual(pair.Second.Properties.Select(row => (row.Key, (float)row.Value)))
            && Same(pair.First.Children, pair.Second.Children));

    private static IEnumerable<PerkBehaviorNode> Flatten(PerkBehaviorNode node) => node.Children.SelectMany(Flatten).Prepend(node);
    private static uint Hash(string name) => (uint)NativePerkBehavior.StateProperties.Single(row => row.Label == name).Value;
    private static ulong Bit(int opcode) => opcode < 64 ? 1UL << opcode : 0;
    private static void Set(PerkBehaviorNode node, string key, double value) => node.Properties.Single(row => row.Key == key).Value = value;
    private static double Value(PerkBehaviorNode node, string key) => node.Properties.Single(row => row.Key == key).Value;
    private static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
    private static int Target(byte[] bytes, int at) => checked(at + (int)BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(at)));
    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidDataException("Invalid perk input was accepted.");
    }

    /** True when the arrays differ only inside the given (offset, size) ranges. */
    private static bool OnlyChanged(byte[] before, byte[] after, params int[] ranges) => before.Length == after.Length
        && Enumerable.Range(0, before.Length).All(i => before[i] == after[i]
            || Enumerable.Range(0, ranges.Length / 2).Any(r => i >= ranges[r * 2] && i < ranges[r * 2] + ranges[r * 2 + 1]));
}
