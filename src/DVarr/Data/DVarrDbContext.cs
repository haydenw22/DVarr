using DVarr.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DVarr.Data;

public class DVarrDbContext : DbContext
{
    public DVarrDbContext(DbContextOptions<DVarrDbContext> options) : base(options) { }

    public DbSet<ProviderSource> Sources => Set<ProviderSource>();
    public DbSet<TunerLease> TunerLeases => Set<TunerLease>();
    public DbSet<ChannelHealth> ChannelHealth => Set<ChannelHealth>();
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<Programme> Programmes => Set<Programme>();
    public DbSet<League> Leagues => Set<League>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<EventSession> EventSessions => Set<EventSession>();
    public DbSet<SourceLink> SourceLinks => Set<SourceLink>();
    public DbSet<LeagueChannelMap> LeagueChannelMaps => Set<LeagueChannelMap>();
    public DbSet<Recording> Recordings => Set<Recording>();
    public DbSet<RecordingFallback> RecordingFallbacks => Set<RecordingFallback>();
    public DbSet<RecordingSegment> RecordingSegments => Set<RecordingSegment>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<ScheduleTick> ScheduleTicks => Set<ScheduleTick>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<SecretEntry> Secrets => Set<SecretEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Store every enum as TEXT (matches docs/05 — state etc. are TEXT columns).
        foreach (var entityType in b.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                var clr = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                if (clr.IsEnum)
                {
                    var converterType = typeof(EnumToStringConverter<>).MakeGenericType(clr);
                    property.SetValueConverter((ValueConverter)Activator.CreateInstance(converterType)!);
                }
            }
        }

        // --- ProviderSource ---
        b.Entity<ProviderSource>().HasIndex(s => s.Label).IsUnique();

        // --- Channel ---
        b.Entity<Channel>(e =>
        {
            e.HasIndex(c => c.SourceId);
            e.HasIndex(c => c.LogicalKey);
            // (SourceId, StreamId) is the channel's natural identity. NOT unique at the DB layer — the ingest upsert
            // (IngestService) already dedups within a provider response; a unique index would also break the
            // test-recording flow (test channels share StreamId=0) and risk orphaning rows on a dedup migration.
            e.HasIndex(c => new { c.SourceId, c.StreamId });
            e.HasOne(c => c.Source).WithMany().HasForeignKey(c => c.SourceId).OnDelete(DeleteBehavior.Cascade);
        });

        // --- Programme (EPG keyed by per-source XMLTV channel id; joined to channels at render time) ---
        b.Entity<Programme>(e =>
        {
            // NOCASE collation: provider get_live_streams vs xmltv (or external EPG) differ in tvg-id casing
            // ("FoxSports3.au" vs "foxsports3.au"). NOCASE makes the join case-insensitive AND index-seekable,
            // so the guide/resolver never full-scan a source's hundreds of thousands of programmes.
            e.Property(p => p.EpgChannelId).UseCollation("NOCASE");
            e.HasIndex(p => new { p.SourceId, p.EpgChannelId, p.StartUtc });
            e.HasIndex(p => p.EpgUid);
        });

        // --- TunerLease ---
        b.Entity<TunerLease>(e =>
        {
            e.HasIndex(l => new { l.SourceId, l.State });
            e.HasOne(l => l.Source).WithMany().HasForeignKey(l => l.SourceId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ChannelHealth>().HasIndex(h => h.ChannelId).IsUnique();

        // --- Event / EventSession: immutable, source-independent natural keys (bug #4) ---
        b.Entity<Event>(e =>
        {
            e.HasIndex(x => x.NaturalKey).IsUnique();
            e.HasIndex(x => x.StartUtc);
            e.HasOne(x => x.League).WithMany(l => l.Events).HasForeignKey(x => x.LeagueId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<EventSession>(e =>
        {
            e.HasIndex(x => x.NaturalKey).IsUnique();
            e.HasOne(x => x.Event).WithMany(ev => ev.Sessions).HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<SourceLink>().HasIndex(x => new { x.Provider, x.ProviderEventId });

        b.Entity<LeagueChannelMap>(e =>
        {
            e.HasIndex(m => new { m.LeagueId, m.Rank });
            e.HasOne(m => m.League).WithMany(l => l.ChannelMaps).HasForeignKey(m => m.LeagueId).OnDelete(DeleteBehavior.Cascade);
        });

        // --- Recording ---
        b.Entity<Recording>(e =>
        {
            e.HasIndex(r => r.State);
            e.HasIndex(r => r.StartUtc);
            e.HasIndex(r => new { r.EventId, r.State });
            // Alternate key needed as the principal of the composite FK that pins
            // fallbacks to the same credential (bug #7 structural fix).
            e.HasAlternateKey(r => new { r.Id, r.SourceId });
        });

        // --- RecordingFallback: cross-credential fallback is UNREPRESENTABLE (bug #7) ---
        // The composite FK (RecordingId, SourceId) -> Recording(Id, SourceId) forces
        // fallback.SourceId == recording.SourceId at the DB layer.
        b.Entity<RecordingFallback>(e =>
        {
            e.HasIndex(f => new { f.RecordingId, f.Rank });
            e.HasOne(f => f.Recording)
                .WithMany(r => r.Fallbacks)
                .HasForeignKey(f => new { f.RecordingId, f.SourceId })
                .HasPrincipalKey(r => new { r.Id, r.SourceId })
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RecordingSegment>(e =>
        {
            e.HasIndex(s => new { s.RecordingId, s.Capture, s.Seq });
            e.HasOne<Recording>().WithMany(r => r.Segments).HasForeignKey(s => s.RecordingId).OnDelete(DeleteBehavior.Cascade);
        });

        // --- Ops ---
        b.Entity<Job>().HasIndex(j => new { j.State, j.RunAtUtc });
        b.Entity<ScheduleTick>().HasIndex(t => t.TickUtc);
        b.Entity<Setting>().HasKey(s => s.Key);
        b.Entity<Notification>().HasIndex(n => n.TsUtc);
        b.Entity<SecretEntry>().HasIndex(s => s.Name).IsUnique();
    }
}
