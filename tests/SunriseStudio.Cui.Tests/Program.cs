using System.Buffers.Binary;
using System.Numerics;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

try
{
if (args is ["--binding-mutations"]) { CuiBindingMutationChecks.Run(); return 0; }
if (args is ["--fonts", var fontRoot]) { CuiFontInspection.Run(fontRoot); return 0; }
if (args is not ([] or ["--game-root", _]))
{
    Console.Error.WriteLine("usage: [--game-root <dir> | --binding-mutations | --fonts <dir>]");
    return 2;
}
var original = Fixture();
var document = CuiDocument.Read(1, 0x80804825, original);
var rectangles = CuiLayout.Read(document);
Check(document.Objects.Count == 2 && document.Components.Count() == 2, "Nested component tables missing.");
Check(rectangles.Count == 2, "Layout rectangles missing.");
Check(rectangles[0].Rectangle == new Vector4(0, 0, 1920, 1080), "Self-relative property address or height scale differs.");
Check(CuiLayout.Apply(original, document, rectangles[0], rectangles[0].Rectangle).SequenceEqual(original), "No-op changed bytes.");
var edited = CuiLayout.Apply(original, document, rectangles[0], new(120, 60, 900, 600));
var changed = CuiDocument.Read(1, 0x80804825, edited);
var layouts = CuiLayout.Read(changed);
Check(layouts[0].Rectangle == new Vector4(120, 60, 900, 600), "Independent axis edit failed.");
Check(layouts[1].Rectangle == rectangles[1].Rectangle, "Shared constants changed another object.");
Check(changed.Properties.All(p => changed.Pools.Any(pool => p.ValueOffset >= pool.DataOffset
    && p.ValueOffset < pool.DataOffset + pool.Stride * pool.Count)), "Rewritten reference leaves its pool.");
var second = CuiLayout.Apply(edited, changed, layouts[0], new(121, 61, 901, 601));
Check(second.Length == edited.Length, "Private values should edit in place.");
var delta = BinaryDelta.Create(original, edited);
Check(delta.Apply(original).SequenceEqual(edited) && delta.Operations.All(op => op.Kind != DeltaKind.Add), "Project delta roundtrip failed.");
Reject(() => CuiLayout.Apply(original, changed, layouts[0], Vector4.One));
Reject(() => CuiLayout.Apply(original, document, rectangles[0], new(0, 0, -1, 100)));
var malformed = original.ToArray();
Q(malformed, rectangles[0].Properties[0].RecordOffset + 16, long.MaxValue);
Reject(() => CuiDocument.Read(1, 0x80804825, malformed));
var anchorParent = new Vector2(800, 600);
var anchorPadding = new Vector4(10, 20, 30, 40);
var anchorStored = new Vector4(100, 200, 120, 80);
var margins = new Vector4(5, 6, 7, 8);
Check(CuiLayoutRules.Resolve(anchorParent, anchorPadding, anchorStored, new(Margin: margins)) == new Vector4(15, 26, 120, 80), "Start anchors differ.");
Check(CuiLayoutRules.Resolve(anchorParent, anchorPadding, anchorStored, new(CuiAnchor.Center, CuiAnchor.Center, margins)) == new Vector4(340, 260, 120, 80), "Center anchors differ.");
Check(CuiLayoutRules.Resolve(anchorParent, anchorPadding, anchorStored, new(CuiAnchor.End, CuiAnchor.End, margins)) == new Vector4(643, 472, 120, 80), "End anchors differ.");
Check(CuiLayoutRules.Resolve(anchorParent, anchorPadding, anchorStored, new(CuiAnchor.Stretch, CuiAnchor.Stretch, margins)) == new Vector4(15, 26, 748, 526), "Stretch anchors differ.");
Check(CuiLayoutRules.Resolve(anchorParent, anchorPadding, anchorStored, new(CuiAnchor.Stretch, CuiAnchor.Stretch, margins, new(100, 200), new(300, 400))) == new Vector4(15, 26, 300, 400), "Anchor size constraints differ.");
CompositionFixture(document);
CuiVisualChecks.Run();
CuiOverrideChecks.Run();
CuiWidgetChecks.Run();
CuiBindingMutationChecks.Run();
BindingPreviewChecks();
BuiltInTypeChecks();
Console.WriteLine("cui synthetic=pass rectangles=2 shared_constants=detached delta=pass malformed=rejected");

if (args is ["--game-root", var gameRoot])
{
    using var catalog = PackageCatalog.Open(gameRoot);
    var nativeTypes = CuiTypeRegistry.BuiltIn;
    Check(nativeTypes.Count == 390 && nativeTypes.DefaultSize == new Vector2(360, 240), "Owned type registry or constructor defaults failed.");
    CuiOverrideChecks.Installed(catalog, nativeTypes);
    CuiWidgetChecks.Installed(catalog, nativeTypes);
    int resources = 0, components = 0, properties = 0, layoutsRead = 0, bindings = 0;
    var bindingErrors = new List<string>();
    bool visualFixture = false;
    foreach (var entry in catalog.Entries.Where(entry => entry.Reference == 0x80804825))
    {
        var bytes = catalog.Read(entry.TagHash);
        var read = CuiDocument.Read(entry.TagHash, entry.Reference, bytes, nativeTypes: nativeTypes);
        if (!visualFixture) visualFixture = CuiVisualChecks.Installed(catalog, read);
        resources++; components += read.Components.Count(); properties += read.Properties.Count();
        var frames = CuiLayout.Read(read);
        layoutsRead += frames.Count;
        var links = CuiBindings.Read(bytes);
        bindings += links.Count;
        bindingErrors.AddRange(links.Where(row => row.Error.Length != 0).Select(row => $"{entry.TagHash:X8}#{row.Index}: {row.Error}"));
        if (entry.TagHash != 0x80BC722A) continue;
        var frameVisibility = links[29];
        Check(frameVisibility.Offset == 0x68E8 && frameVisibility.Source.Node == 0x552 && frameVisibility.Source.Path[^1] == 0x202
            && frameVisibility.Target.Node == 0x51C && frameVisibility.Target.Path[^1] == 0x201
            && frameVisibility.Transform is { Kind: CuiBindingTransformKind.Not, Offset: 0x7490 },
            "Waiting-HUD visibility binding differs from its native record.");
        var compare = links.First(row => row.Error.Length == 0 && row.Transform.Kind == CuiBindingTransformKind.CompareInteger);
        Check(CuiBindings.EditComparison(bytes, compare, compare.Transform.Comparison, compare.Transform.Operand).SequenceEqual(bytes),
            "A no-op binding edit changed bytes.");
        byte[] retuned = CuiBindings.EditComparison(bytes, compare, CuiComparison.GreaterOrEqual, 7);
        var relinked = CuiBindings.Read(retuned);
        Check(relinked[compare.Index].Transform is { Kind: CuiBindingTransformKind.CompareInteger, Comparison: CuiComparison.GreaterOrEqual, Operand: 7 }
            && relinked.Where(row => row.Index != compare.Index).Select(row => row.Transform with { Offset = 0 })
                .SequenceEqual(links.Where(row => row.Index != compare.Index).Select(row => row.Transform with { Offset = 0 }))
            && Enumerable.Range(8, bytes.Length - 8).All(at => at - compare.Offset - 48 is >= 0 and < 8 || bytes[at] == retuned[at]),
            "Binding edit changed another binding or an unrelated byte.");
        Reject(() => CuiBindings.EditComparison(retuned, compare, CuiComparison.Less, 1));
        Reject(() => CuiBindings.EditComparison(bytes, frameVisibility, CuiComparison.Less, 1));
        Console.WriteLine($"cui_bindings edit=0x{entry.TagHash:X8}#{compare.Index} detached=pass others=unchanged stale=rejected not=rejected");
        var ready = CuiBindingPreview.Evaluate(links, new Dictionary<(ushort, string), CuiPreviewValue>
        {
            [(0x552, "202")] = CuiPreviewValue.FromBool(true), [(0x552, "203")] = CuiPreviewValue.FromBool(true)
        });
        Check(ready[(0x51C, "201")] == CuiPreviewValue.FromBool(false) && ready[(0x001, "201")] == CuiPreviewValue.FromBool(true)
            && ready[(0x532, "201")] == CuiPreviewValue.FromBool(false), "Waiting-HUD bindings evaluated differently from their native records.");
        Console.WriteLine("cui_bindings preview=0x80BC722A ready_label=hidden direct=true inverted=false");
        var waiting = frames.First(frame => frame.Node.Id == 0x51C);
        Check(waiting.Rectangle == new Vector4(0, 0, 1920, 1080), "Waiting-HUD native pointer fixture differs.");
        var update = CuiLayout.Apply(bytes, read, waiting, waiting.Rectangle with { X = 120, Y = 60 });
        var reread = CuiDocument.Read(entry.TagHash, entry.Reference, update);
        var moved = CuiLayout.Read(reread).First(frame => frame.Component.Offset == waiting.Component.Offset);
        Check(moved.Rectangle == new Vector4(120, 60, 1920, 1080), "Installed rectangle edit did not roundtrip.");
        var before = read.Properties.ToDictionary(p => p.RecordOffset);
        foreach (var p in reread.Properties)
        {
            if (waiting.Properties.Take(2).Any(editedProperty => editedProperty.RecordOffset == p.RecordOffset)) continue;
            Check(p.Fields.Select(f => f.Value).SequenceEqual(before[p.RecordOffset].Fields.Select(f => f.Value)),
                "Installed edit changed an unrelated property value.");
        }
        Check(BinaryDelta.Create(bytes, update).Apply(bytes).SequenceEqual(update), "Installed delta failed.");
    }
    Console.WriteLine($"cui installed_resources={resources} components={components} properties={properties} rectangles={layoutsRead} mutation=memory_only");
    Console.WriteLine($"cui bindings={bindings} unreadable={bindingErrors.Count} {string.Join("; ", bindingErrors.Take(5))}");
    Check(bindingErrors.Count == 0, "Installed bindings did not all read.");
    Check(visualFixture, "No installed nonzero bitmap selection was verified.");
    var textPreview = CuiPreviewData.Read(catalog, 0x80BC722A, 0x80804825, catalog.Read(0x80BC722A),
        hash => hash == 0xF9C88800 ? "Waiting for Other Players" : null)!;
    Check(textPreview.Diagnostics.Count == 0, "Text preview failed.");
    var textComponent = textPreview.Document!.Components.First(component => component.Key == 0x543);
    Check(textPreview.LocalContent[textComponent.Offset].TextHash == 0xF9C88800
        && textPreview.LocalContent[textComponent.Offset].Text == "Waiting for Other Players", "Text was joined by pool index instead of its property owner.");
    var bitmapPreview = CuiPreviewData.Read(catalog, 0x80BC7154, 0x80804825, catalog.Read(0x80BC7154))!;
    Check(bitmapPreview.Diagnostics.Count == 0 && bitmapPreview.Textures.ContainsKey(0x80BC6D7F), "Native bitmap frame did not resolve its texture.");
    Check(bitmapPreview.Layouts.Any(rectangle => rectangle.Node.Id == 8), "Bitmap constructor coordinates missing.");
    Console.WriteLine($"cui text_owner=543 text_hash=F9C88800 bitmap=80BC6D80 texture=80BC6D7F textures={bitmapPreview.Textures.Count}");
    foreach (uint tag in new uint[] { 0x80BC7229, 0x80BC6F5A })
    {
        var program = CuiDocument.Read(tag, 0x8080496A, catalog.Read(tag), nativeTypes: nativeTypes);
        var scene = CuiComposition.Read(program, resource => CuiDocument.Read(resource, 0x80804825, catalog.Read(resource), nativeTypes: nativeTypes));
        Check(scene.Nodes.Count > 100 && scene.Resources.Count > 1, "Installed composition did not follow imports.");
        Console.WriteLine($"cui program={tag:X8} nodes={scene.Nodes.Count} resources={scene.Resources.Count} static_rectangles={scene.Nodes.Count(node => node.Rectangle is not null)} stored_rectangles={scene.Nodes.Count(node => node.StoredRectangle is not null)}");
    }
    // An imported table's node IDs are local when they fit its own range, not the offset composed range.
    int hierarchies = 0, offsetImports = 0, localFit = 0, composedFit = 0, endpoints = 0, mapped = 0, placeholder = 0, beyond = 0;
    foreach (var entry in catalog.Entries.Where(entry => entry.Reference == 0x8080496A))
    {
        CuiComposition scene;
        try
        {
            var program = CuiDocument.Read(entry.TagHash, 0x8080496A, catalog.Read(entry.TagHash), nativeTypes: nativeTypes);
            scene = CuiComposition.Read(program, resource => CuiDocument.Read(resource, 0x80804825, catalog.Read(resource), nativeTypes: nativeTypes));
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or KeyNotFoundException) { continue; }
        hierarchies++; mapped += CuiComposedBindings.Read(scene).Count;
        foreach (var instance in scene.Instances.Where(row => !row.Root && row.Start > 0))
        {
            var links = CuiBindings.Read(scene.Resources[instance.Resource].Data);
            if (links.Count == 0) continue;
            offsetImports++;
            foreach (int node in links.SelectMany(row => new[] { (int)row.Source.Node, row.Target.Node }))
            {
                endpoints++;
                if (node >= 1 && node <= instance.Count) localFit++;
                else if (node == 0) placeholder++;
                else beyond++;
                if (node - instance.Start >= 1 && node - instance.Start <= instance.Count) composedFit++;
            }
        }
    }
    Console.WriteLine($"cui binding_ids hierarchies={hierarchies} offset_imports={offsetImports} endpoints={endpoints} local_fit={localFit} "
        + $"placeholder={placeholder} beyond={beyond} composed_fit={composedFit} mapped={mapped}");
    Check(offsetImports > 0 && localFit + placeholder == endpoints && composedFit * 10 < endpoints, "Imported binding node IDs are not local to their resource.");
}
return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"cui result=fail error={error.Message}");
    return 1;
}

static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

static void BuiltInTypeChecks()
{
    var rows = CuiTypeRegistry.BuiltInTypes;
    var parents = rows.ToDictionary(row => row.Key, row => row.ParentKey);
    Check(rows.Count == 390 && CuiTypeRegistry.BuiltIn.DefaultSize == new Vector2(360, 240), "Built-in table size or defaults differ.");
    Check(rows.Zip(rows.Skip(1)).All(pair => pair.First.Key < pair.Second.Key), "Built-in types are not sorted and unique.");
    foreach (var row in rows)
    {
        HashSet<ulong> path = [];
        for (ulong current = row.Key; current != 0; current = parents[current])
            Check(path.Add(current) && parents.ContainsKey(current), "Built-in inheritance is incomplete or cyclic.");
    }
    var types = CuiTypeRegistry.BuiltIn;
    Check(types.HasLayout(0xAE7E6D99, 0) && types.IsContainer(0x87A7707B, 0x725AD1D8) && types.HasVerifiedDefaults(0xAE7E6D99, 0),
        "Built-in base types differ.");
}

/** Comparisons read value, code, operand; kinds never convert; inputs are never overwritten. */
static void BindingPreviewChecks()
{
    static CuiBinding Link(int index, ushort source, ushort target, CuiBindingTransform transform) =>
        new(index, index * 56, new(source, source, [0x202]), new(target, target, [0x201]), transform, "");
    var links = new List<CuiBinding>
    {
        Link(0, 1, 2, new(CuiBindingTransformKind.CompareInteger, CuiComparison.GreaterOrEqual, 3)),
        Link(1, 2, 3, new(CuiBindingTransformKind.Not)),
        Link(2, 4, 5, new(CuiBindingTransformKind.CompareFloat, CuiComparison.Less, BitConverter.SingleToUInt32Bits(0.5f))),
        Link(3, 6, 7, new(CuiBindingTransformKind.CompareHash, CuiComparison.Greater, 0x80000000)),
        Link(4, 8, 9, new(CuiBindingTransformKind.CompareInteger, CuiComparison.Equal, 1)),
        Link(5, 10, 11, new(CuiBindingTransformKind.None)),
        Link(6, 11, 10, new(CuiBindingTransformKind.Not))
    };
    var inputs = new Dictionary<(ushort, string), CuiPreviewValue>
    {
        [(1, "202")] = new(CuiValueKind.Integer, 3), [(4, "202")] = new(CuiValueKind.Float, BitConverter.SingleToUInt32Bits(0.25f)),
        [(6, "202")] = new(CuiValueKind.Hash, 0x80000001), [(8, "202")] = CuiPreviewValue.FromBool(true),
        [(10, "202")] = CuiPreviewValue.FromBool(true), [(3, "201")] = CuiPreviewValue.FromBool(true)
    };
    var values = CuiBindingPreview.Evaluate(links, inputs);
    Check(values[(2, "201")].AsBool && values[(3, "201")].AsBool && values[(5, "201")].AsBool && values[(7, "201")].AsBool,
        "Binding comparisons or input precedence differ.");
    Check(!values.ContainsKey((9, "201")), "A bool input was compared as an integer.");
    Check(values[(11, "201")].AsBool, "A copy binding lost its value.");
    inputs[(1, "202")] = new(CuiValueKind.Integer, unchecked((uint)-4));
    Check(!CuiBindingPreview.Evaluate(links, inputs)[(2, "201")].AsBool, "Integer comparison is not signed.");
    Console.WriteLine("cui_bindings synthetic=pass compare=value_code_operand kinds=strict inputs=fixed cycle=bounded");
}
static void Reject(Action action)
{
    try { action(); }
    catch (Exception error) when (error is InvalidDataException or OverflowException) { return; }
    throw new InvalidOperationException("Invalid input accepted.");
}
static void Q(byte[] b, int p, long value) => BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(p), value);
static void U(byte[] b, int p, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p), value);
static void S(byte[] b, int p, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p), value);
static void F(byte[] b, int p, float value) => BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(p), value);
static void Array(byte[] b, int p, int h, int count, uint type)
{ Q(b, p, count); Q(b, p + 8, h - p - 8); U(b, h - 4, 0x80809FBD); Q(b, h, count); U(b, h + 8, type); }

/** A native-order hierarchy applies a nested horizontal anchor to its constructed child. */
static void CompositionFixture(CuiDocument source)
{
    var data = source.Data.ToArray();
    System.Array.Resize(ref data, 1120);
    U(data, 1104, 2); F(data, 1108, 960f / 1080); F(data, 1112, .5f);
    var originalProperties = source.Components.First().Properties;
    var childProperties = originalProperties.Select(p => p.Selector switch
    {
        0x208 => p with { ValueOffset = 1108 },
        0x209 => p with { ValueOffset = 1112 },
        _ => p
    }).Append(new(1, 0, 1104, 0x80804696, 152, false, []) { Path = [0x210, 1] }).ToArray();
    var resource = new CuiDocument
    {
        TagHash = 1, ClassHash = 0x80804825, Data = data,
        Nodes = [new(1, 0, 0x87A7707B, 0x725AD1D8, "Container", 0), new(2, 0, 0xAE7E6D99, 0, "Child", 0)],
        Objects = [new(0, 0, [new(1, 0, originalProperties), new(2, 0, childProperties)]) { IsDefaultProfile = true }]
    };
    var program = new CuiDocument { ClassHash = 0x8080496A, RootResource = 1,
        Hierarchy = [new(0, 0x7FFF, 0), new(1, 0, 0), new(2, 1, 0)] };
    var composition = CuiComposition.Read(program, _ => resource);
    Check(composition.Nodes.Single(n => n.Id == 2).Rectangle == new Vector4(480, 0, 960, 540), "Hierarchy anchor composition failed.");
    var cycle = new CuiDocument { TagHash = 1, ClassHash = 0x80804825, Imports = [new(1, 0, 2, 0)] };
    Reject(() => CuiComposition.Read(program, _ => cycle));
}

/** Two objects share all layout constants while their component arrays occupy different offsets. */
static byte[] Fixture()
{
    var b = new byte[1104]; Q(b, 0, b.Length);
    Array(b, 24, 360, 2, 0x80804618); U(b, 376, 10); U(b, 380, 11);
    Array(b, 56, 400, 2, 0x80804622);
    U(b, 416, 0xAE7E6D99); S(b, 426, 7); U(b, 432, 0xAE7E6D99); S(b, 442, 12);
    Array(b, 72, 464, 2, 0x8080462A);
    Array(b, 512, 632, 1, 0x808046D4); Q(b, 648, 7);
    Array(b, 576, 688, 1, 0x808046D4); Q(b, 704, 12);
    Array(b, 656, 736, 4, 0x80804858); Array(b, 712, 880, 4, 0x80804858);
    Array(b, 120, 1024, 1, 0x80804943);
    Array(b, 200, 1072, 3, 0x80804694);
    F(b, 1088, 0); F(b, 1092, 1920f / 1080); F(b, 1096, 1);
    foreach (int rows in new[] { 752, 896 })
        for (int i = 0; i < 4; i++)
        {
            int p = rows + i * 24;
            Q(b, p, 1040 - p); Q(b, p + 8, 0x206 + i);
            Q(b, p + 16, 1088 + Math.Max(0, i - 1) * 4 - p - 16);
        }
    return b;
}
