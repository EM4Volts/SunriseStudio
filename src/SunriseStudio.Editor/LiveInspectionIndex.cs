using SunriseStudio.Live;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

internal sealed class LiveTreeNode(string id, string name, LiveTreeNode? parent, object? source = null)
{
    public string Id { get; } = id;
    public string Name { get; set; } = name;
    public LiveTreeNode? Parent { get; } = parent;
    public object? Source { get; } = source;
    public int Depth => Parent is null ? 0 : Parent.Depth + 1;
    public List<LiveTreeNode> Children { get; } = [];
    public bool Visible { get; set; } = true;
}

internal sealed class LiveObjectInfo(string key, string name, LivePlacement observation,
    IReadOnlyList<MeshInstance> meshes, LiveTreeNode node)
{
    public string Key { get; } = key;
    public string Name { get; set; } = name;
    public LivePlacement Observation { get; set; } = observation;
    public IReadOnlyList<MeshInstance> Meshes { get; } = meshes;
    public LiveTreeNode Node { get; } = node;
}

/** Runtime rows and local hiding stay outside the authored world and delta project. */
internal sealed class LiveInspectionIndex
{
    public const string RootId = "live-runtime";
    private readonly Dictionary<string, LiveObjectInfo> actors = new(StringComparer.Ordinal);
    private readonly Dictionary<object, (LiveObjectInfo Actor, LiveTreeNode Part)> bySource = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, LiveTreeNode> nodes = new(StringComparer.Ordinal);
    private readonly HashSet<string> hidden = new(StringComparer.Ordinal);
    private SceneDocument? scene;
    private LivePlacements? lastPlacements;
    private long runtimeRevision = -1;
    private int nativeRevision = -1, nameRevision = -1;
    public LiveTreeNode Root { get; private set; } = new(RootId, "Live entities", null);
    public long TreeRevision { get; private set; }
    public IReadOnlyCollection<LiveObjectInfo> Objects => actors.Values;
    public IReadOnlyCollection<LiveTreeNode> Nodes => nodes.Values;
    public LiveTreeNode? Find(string id) => nodes.GetValueOrDefault(id);
    public LiveObjectInfo? Find(object source) => bySource.TryGetValue(source, out var item) ? item.Actor : null;
    public LiveTreeNode? FindNode(object source, SceneSelectionMode mode) => bySource.TryGetValue(source, out var item)
        ? mode == SceneSelectionMode.MeshPart ? item.Part : item.Actor.Node : null;
    public bool IsVisible(object source) => !bySource.TryGetValue(source, out var item) || item.Part.Visible;
    public bool IsHidden(string id) => hidden.Contains(id);

    /** Pose updates replace observations without rebuilding mesh membership or tree order. */
    public bool Update(AssetViewport viewport, LivePlacements? snapshot, IReadOnlyCollection<LivePlacement> runtime,
        long revision, int labels, Func<uint, string> name)
    {
        bool rebuilt = !ReferenceEquals(scene, viewport.Scene);
        if (rebuilt)
        {
            scene = viewport.Scene; actors.Clear(); bySource.Clear(); nodes.Clear(); hidden.Clear();
            Root = new(RootId, "Live entities", null); nodes.Add(RootId, Root);
        }
        if (!rebuilt && ReferenceEquals(snapshot, lastPlacements) && runtimeRevision == revision
            && nativeRevision == viewport.LivePrototypeRevision && nameRevision == labels) return false;
        lastPlacements = snapshot; runtimeRevision = revision; nativeRevision = viewport.LivePrototypeRevision; nameRevision = labels;
        // An owner's class instance is keyed by its row; each drawn model adds "/model" to that key.
        var views = viewport.LiveInstances.GroupBy(view => view.Key.Split('/')[0], StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(view => view.Key, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var entries = runtime.Select(row => (Row: row, Key: LiveDynamicBinding.Key(row), Views: views.GetValueOrDefault(LiveDynamicBinding.Key(row))))
            .Where(item => item.Views is not null && item.Views.All(view => view.Key != item.Key || view.ClassTag == item.Row.ClassTag))
            .Select(item => (item.Row, item.Key, Meshes: item.Views!.SelectMany(view => view.Meshes).ToArray())).ToArray();
        var active = entries.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        foreach (string key in actors.Keys.Where(key => !active.Contains(key)).ToArray()) { Remove(key); rebuilt = true; }
        foreach (var item in entries)
        {
            var row = item.Row; string key = item.Key; var meshes = item.Meshes;
            // A placed row shown as a marker has no class; its object list entry names it.
            string title = row.ClassTag is not (0 or uint.MaxValue) ? name(row.ClassTag)
                : !row.Dynamic ? $"{name(row.ObjectListTag)} / {row.EntryIndex}" : name(meshes[0].Mesh.TagHash);
            if (actors.TryGetValue(key, out var actor) && actor.Meshes.SequenceEqual(meshes))
            {
                actor.Observation = row;
                if (actor.Name != title) { actor.Name = title; actor.Node.Name = Label(title, row); rebuilt = true; }
                continue;
            }
            Remove(key);
            var branch = new LiveTreeNode(RootId + "/" + key, Label(title, row), Root, meshes.FirstOrDefault());
            actor = new(key, title, row, meshes, branch);
            actors.Add(key, actor); Root.Children.Add(branch); nodes.Add(branch.Id, branch);
            for (int index = 0; index < meshes.Length; index++)
            {
                var mesh = meshes[index];
                var part = new LiveTreeNode(branch.Id + "/part/" + index,
                    $"Part {mesh.Mesh.PartIndex}, LOD {mesh.Mesh.LodLevel}", branch, mesh);
                branch.Children.Add(part); nodes.Add(part.Id, part); bySource.Add(mesh, (actor, part));
            }
            ApplyVisibility(branch); rebuilt = true;
        }
        if (rebuilt)
        {
            Root.Children.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
            hidden.RemoveWhere(id => !nodes.ContainsKey(id)); ApplyVisibility(Root); TreeRevision++;
        }
        return rebuilt;
    }

    public bool SetHidden(string id, bool value)
    {
        if (!nodes.TryGetValue(id, out var node) || !(value ? hidden.Add(id) : hidden.Remove(id))) return false;
        ApplyVisibility(node); TreeRevision++; return true;
    }

    public void ShowAll()
    { if (hidden.Count != 0) { hidden.Clear(); ApplyVisibility(Root); TreeRevision++; } }

    public void Reveal(LiveTreeNode node)
    { foreach (var ancestor in PathTo(node)) SetHidden(ancestor.Id, false); }

    public IReadOnlyList<LiveTreeNode> PathTo(LiveTreeNode node)
    {
        var result = new List<LiveTreeNode>();
        for (var current = node; current is not null; current = current.Parent) result.Add(current);
        result.Reverse(); return result;
    }

    private void Remove(string key)
    {
        if (!actors.Remove(key, out var actor)) return;
        Root.Children.Remove(actor.Node); nodes.Remove(actor.Node.Id);
        foreach (var part in actor.Node.Children) { nodes.Remove(part.Id); bySource.Remove(part.Source!); }
    }

    private void ApplyVisibility(LiveTreeNode node)
    {
        node.Visible = (node.Parent?.Visible ?? true) && !hidden.Contains(node.Id);
        foreach (var child in node.Children) ApplyVisibility(child);
    }

    private static string Label(string name, LivePlacement row) => $"{name} ({row.Handle:X8})";
}
