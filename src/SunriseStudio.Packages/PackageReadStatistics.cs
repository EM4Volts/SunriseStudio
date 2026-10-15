namespace SunriseStudio.Packages;

public sealed record PackageReadStatistics(long EntryReads, long BlockReads, long CacheHits,
    long StoredBytes, long DecodedBytes, long FileOpens, long InheritedLookups,
    TimeSpan ReadTime, TimeSpan DecodeTime);
