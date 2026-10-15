namespace SunriseStudio.Scene.Collision;

/** One part of one placement, by index into the loaded scene. A reload can change both numbers. */
public readonly record struct CollisionPartRef(int Placement, int Part);

/**
 * Finds a placement and a part again after a reload. Ordinal counts earlier placements with the
 * same other fields. ShapeKey is the part's smallest shape key, or CollisionParts.WholeShape.
 */
public sealed record CollisionPartKey(uint EntityTag, uint ListTag, int ListRow, uint CompoundTag, int Instance,
    uint ShapeTag, int ShapeOffset, int Bone, int Ordinal, uint ShapeKey)
{
    /** The keys of the parts, in the same order. */
    public static IReadOnlyList<CollisionPartKey> Of(CollisionScene scene, IEnumerable<CollisionPartRef> parts)
    {
        var ordinals = Ordinals(scene);
        return parts.Select(part =>
        {
            var shape = scene.Pieces[scene.Placements[part.Placement].Piece].Shape;
            return Placement(scene, part.Placement, ordinals[part.Placement]) with
            {
                ShapeKey = CollisionParts.Of(shape).Key(shape, part.Part)
            };
        }).ToArray();
    }

    /** The key of a whole placement. */
    public static CollisionPartKey OfPlacement(CollisionScene scene, int placement) =>
        Placement(scene, placement, Ordinals(scene)[placement]);

    /** The placement this key names in the scene; -1 when it has none. */
    public int FindPlacement(CollisionScene scene)
    {
        var place = this with { Ordinal = 0, ShapeKey = CollisionParts.WholeShape };
        int seen = 0;
        for (int i = 0; i < scene.Placements.Count; i++)
            if (Placement(scene, i, 0) == place && seen++ == Ordinal) return i;
        return -1;
    }

    /**
     * The parts the keys name in the scene. Keys that land in one part give it once.
     * @return Found parts in key order, and the keys that name nothing.
     */
    public static (IReadOnlyList<CollisionPartRef> Found, IReadOnlyList<CollisionPartKey> Missing) Resolve(CollisionScene scene,
        IEnumerable<CollisionPartKey> keys)
    {
        var ordinals = Ordinals(scene);
        var byPlacement = new Dictionary<CollisionPartKey, int>();
        for (int i = 0; i < scene.Placements.Count; i++) byPlacement.TryAdd(Placement(scene, i, ordinals[i]), i);
        var found = new List<CollisionPartRef>();
        var seen = new HashSet<CollisionPartRef>();
        var missing = new List<CollisionPartKey>();
        foreach (var key in keys)
        {
            if (!byPlacement.TryGetValue(key with { ShapeKey = CollisionParts.WholeShape }, out int placement)) { missing.Add(key); continue; }
            var shape = scene.Pieces[scene.Placements[placement].Piece].Shape;
            var parts = CollisionParts.Of(shape);
            int[] named = key.ShapeKey == CollisionParts.WholeShape ? Enumerable.Range(0, parts.Triangles.Count).ToArray()
                : parts.PartOfKey(shape, key.ShapeKey) is >= 0 and var part ? [part] : [];
            if (named.Length == 0) missing.Add(key);
            foreach (int each in named)
                if (seen.Add(new(placement, each))) found.Add(new(placement, each));
        }
        return (found, missing);
    }

    private static CollisionPartKey Placement(CollisionScene scene, int index, int ordinal)
    {
        var placement = scene.Placements[index];
        var entity = scene.Entities[placement.Entity];
        var piece = scene.Pieces[placement.Piece];
        return new(entity.Tag, entity.ListTag, entity.ListRow, placement.CompoundTag, placement.Instance, piece.Tag, piece.Offset,
            placement.Bone, ordinal, CollisionParts.WholeShape);
    }

    /** Each placement's count of earlier placements with the same fields. */
    private static int[] Ordinals(CollisionScene scene)
    {
        var counts = new Dictionary<CollisionPartKey, int>();
        var ordinals = new int[scene.Placements.Count];
        for (int i = 0; i < ordinals.Length; i++)
        {
            var place = Placement(scene, i, 0);
            ordinals[i] = counts.GetValueOrDefault(place);
            counts[place] = ordinals[i] + 1;
        }
        return ordinals;
    }
}
