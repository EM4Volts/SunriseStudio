namespace SunriseStudio.Packages;

public sealed record PackageInstanceOrigin(uint SourceTag, int SourceIndex);
public sealed record PackagePlacementOrigin(uint SourceTag, int SourceOffset, int SourceIndex);
public sealed record PackageInstanceReplacement(uint SourceTag, int SourceIndex, uint PlacementSourceTag, int PlacementSourceOffset);

/** Overlay metadata describes project bytes; installed entries remain the baseline. */
public sealed record PackageOverlayEntry(PackageEntry Entry, string? Name = null)
{
    public WideHashAliases WideHashes { get; init; } = WideHashAliases.Empty;
    public bool Generated { get; init; }
    public uint? GeneratedSourceTag { get; init; }
    public IReadOnlyDictionary<int, PackageInstanceOrigin> InstanceOrigins { get; init; } = new Dictionary<int, PackageInstanceOrigin>();
    public IReadOnlyDictionary<int, PackagePlacementOrigin> PlacementOrigins { get; init; } = new Dictionary<int, PackagePlacementOrigin>();
    public IReadOnlyList<PackageInstanceReplacement> Replaces { get; init; } = [];
}
