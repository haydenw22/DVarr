namespace DVarr.Infrastructure;

/// <summary>Resolved runtime directories (container volumes, or local scratch on Windows dev).</summary>
public sealed record RuntimePaths(string ConfigDir, string MediaDir, string SegmentDir);
