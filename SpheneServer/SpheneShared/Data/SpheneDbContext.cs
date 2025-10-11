using SpheneShared.Models;
using Microsoft.EntityFrameworkCore;

namespace SpheneShared.Data;

public class SpheneDbContext : DbContext
{
#if DEBUG
    public SpheneDbContext() { }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (optionsBuilder.IsConfigured)
        {
            base.OnConfiguring(optionsBuilder);
            return;
        }

        var host = Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "localhost";
        var port = Environment.GetEnvironmentVariable("POSTGRES_PORT") ?? "5432";
        var database = Environment.GetEnvironmentVariable("POSTGRES_DB") ?? "sphene";
        var username = Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "sphene";
        var password = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? "secretdevpassword";
        
        var connectionString = $"Host={host};Port={port};Database={database};Username={username};Password={password}";
        optionsBuilder.UseNpgsql(connectionString, builder =>
        {
            builder.MigrationsHistoryTable("_efmigrationshistory", "public");
            builder.MigrationsAssembly("SpheneShared");
        }).UseSnakeCaseNamingConvention();
        optionsBuilder.EnableThreadSafetyChecks(false);
        optionsBuilder.ConfigureWarnings(warnings => warnings.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));

        base.OnConfiguring(optionsBuilder);
    }
#endif

    public SpheneDbContext(DbContextOptions<SpheneDbContext> options) : base(options)
    {
    }

    public DbSet<Auth> Auth { get; set; }
    public DbSet<BannedRegistrations> BannedRegistrations { get; set; }
    public DbSet<Banned> BannedUsers { get; set; }
    public DbSet<ClientPair> ClientPairs { get; set; }
    public DbSet<FileCache> Files { get; set; }
    public DbSet<ForbiddenUploadEntry> ForbiddenUploadEntries { get; set; }
    public DbSet<GroupBan> GroupBans { get; set; }
    public DbSet<GroupPair> GroupPairs { get; set; }
    public DbSet<Group> Groups { get; set; }
    public DbSet<GroupTempInvite> GroupTempInvites { get; set; }
    public DbSet<LodeStoneAuth> LodeStoneAuth { get; set; }
    public DbSet<UserProfileData> UserProfileData { get; set; }
    public DbSet<User> Users { get; set; }
    public DbSet<UserPermissionSet> Permissions { get; set; }
    public DbSet<GroupPairPreferredPermission> GroupPairPreferredPermissions { get; set; }
    public DbSet<UserDefaultPreferredPermission> UserDefaultPreferredPermissions { get; set; }
    public DbSet<CharaData> CharaData { get; set; }
    public DbSet<CharaDataFile> CharaDataFiles { get; set; }
    public DbSet<CharaDataFileSwap> CharaDataFileSwaps { get; set; }
    public DbSet<CharaDataOriginalFile> CharaDataOriginalFiles { get; set; }
    public DbSet<CharaDataPose> CharaDataPoses { get; set; }
    public DbSet<CharaDataAllowance> CharaDataAllowances { get; set; }
    public DbSet<CharaDataHash> CharaDataHashes { get; set; }
    public DbSet<AreaBoundSyncshell> AreaBoundSyncshells { get; set; }
    public DbSet<AreaBoundLocation> AreaBoundLocations { get; set; }
    public DbSet<AreaBoundSyncshellConsent> AreaBoundSyncshellConsents { get; set; }
    public DbSet<SyncshellWelcomePage> SyncshellWelcomePages { get; set; }
    public DbSet<UserHousingProperty> UserHousingProperties { get; set; }

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<Auth>().ToTable("auth");
        mb.Entity<Auth>().HasOne(a => a.User).WithMany().HasForeignKey(a => a.UserUID).HasConstraintName("fk_auth_users_user_uid");
        mb.Entity<Auth>().HasOne(a => a.PrimaryUser).WithMany().HasForeignKey(a => a.PrimaryUserUID).HasConstraintName("fk_auth_users_primary_user_uid");
        mb.Entity<User>().ToTable("users");
        mb.Entity<FileCache>().ToTable("file_caches");
        mb.Entity<FileCache>().HasIndex(c => c.UploaderUID);
        mb.Entity<ClientPair>().ToTable("client_pairs");
        mb.Entity<ClientPair>().HasKey(u => new { u.UserUID, u.OtherUserUID });
        mb.Entity<ClientPair>().HasIndex(c => c.UserUID);
        mb.Entity<ClientPair>().HasIndex(c => c.OtherUserUID);
        mb.Entity<ForbiddenUploadEntry>().ToTable("forbidden_upload_entries");
        mb.Entity<Banned>().ToTable("banned_users");
        mb.Entity<LodeStoneAuth>().ToTable("lodestone_auth");
        mb.Entity<BannedRegistrations>().ToTable("banned_registrations");
        mb.Entity<Group>().ToTable("groups");
        mb.Entity<Group>().HasIndex(c => c.OwnerUID);
        mb.Entity<GroupPair>().ToTable("group_pairs");
        mb.Entity<GroupPair>().HasKey(u => new { u.GroupGID, u.GroupUserUID });
        mb.Entity<GroupPair>().HasIndex(c => c.GroupUserUID);
        mb.Entity<GroupPair>().HasIndex(c => c.GroupGID);
        mb.Entity<GroupBan>().ToTable("group_bans");
        mb.Entity<GroupBan>().HasKey(u => new { u.GroupGID, u.BannedUserUID });
        mb.Entity<GroupBan>().HasIndex(c => c.BannedUserUID);
        mb.Entity<GroupBan>().HasIndex(c => c.GroupGID);
        mb.Entity<GroupTempInvite>().ToTable("group_temp_invites");
        mb.Entity<GroupTempInvite>().HasKey(u => new { u.GroupGID, u.Invite });
        mb.Entity<GroupTempInvite>().HasIndex(c => c.GroupGID);
        mb.Entity<GroupTempInvite>().HasIndex(c => c.Invite);
        mb.Entity<UserProfileData>().ToTable("user_profile_data");
        mb.Entity<UserProfileData>().HasKey(c => c.UserUID);
        mb.Entity<UserPermissionSet>().ToTable("user_permission_sets");
        mb.Entity<UserPermissionSet>().HasKey(u => new { u.UserUID, u.OtherUserUID });
        mb.Entity<UserPermissionSet>().HasIndex(c => c.UserUID);
        mb.Entity<UserPermissionSet>().HasIndex(c => c.OtherUserUID);
        mb.Entity<CharaDataHash>().ToTable("chara_data_hashes");
        mb.Entity<CharaDataHash>().HasKey(c => c.Id);
        mb.Entity<CharaDataHash>().HasIndex(c => c.ParentId);
        mb.Entity<UserPermissionSet>().HasIndex(c => new { c.UserUID, c.OtherUserUID, c.IsPaused });
        mb.Entity<GroupPairPreferredPermission>().ToTable("group_pair_preferred_permissions");
        mb.Entity<GroupPairPreferredPermission>().HasKey(u => new { u.UserUID, u.GroupGID });
        mb.Entity<GroupPairPreferredPermission>().HasIndex(c => c.UserUID);
        mb.Entity<GroupPairPreferredPermission>().HasIndex(c => c.GroupGID);
        mb.Entity<UserDefaultPreferredPermission>().ToTable("user_default_preferred_permissions");
        mb.Entity<UserDefaultPreferredPermission>().HasKey(u => u.UserUID);
        mb.Entity<UserDefaultPreferredPermission>().HasIndex(u => u.UserUID);
        mb.Entity<UserDefaultPreferredPermission>().HasOne(u => u.User);
        mb.Entity<CharaData>().ToTable("chara_data");
        mb.Entity<CharaData>()
            .HasMany(p => p.Poses)
            .WithOne(c => c.Parent)
            .HasForeignKey(c => new { c.ParentId, c.ParentUploaderUID });
        mb.Entity<CharaData>()
            .HasMany(p => p.Files)
            .WithOne(c => c.Parent)
            .HasForeignKey(c => new { c.ParentId, c.ParentUploaderUID });
        mb.Entity<CharaData>()
            .HasMany(p => p.OriginalFiles)
            .WithOne(p => p.Parent)
            .HasForeignKey(p => new { p.ParentId, p.ParentUploaderUID });
        mb.Entity<CharaData>()
            .HasMany(p => p.AllowedIndividiuals)
            .WithOne(p => p.Parent)
            .HasForeignKey(p => new { p.ParentId, p.ParentUploaderUID });
        mb.Entity<CharaData>()
            .HasMany(p => p.FileSwaps)
            .WithOne(p => p.Parent)
            .HasForeignKey(p => new { p.ParentId, p.ParentUploaderUID });
        mb.Entity<CharaData>().HasKey(p => new { p.Id, p.UploaderUID });
        mb.Entity<CharaData>().HasIndex(p => p.UploaderUID);
        mb.Entity<CharaData>().HasIndex(p => p.Id);
        mb.Entity<CharaDataFile>().ToTable("chara_data_files");
        mb.Entity<CharaDataFile>().HasKey(c => new { c.ParentId, c.ParentUploaderUID, c.GamePath });
        mb.Entity<CharaDataFile>().HasIndex(c => c.ParentId);
        mb.Entity<CharaDataFile>().HasOne(f => f.FileCache).WithMany().HasForeignKey(f => f.FileCacheHash).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<CharaDataFileSwap>().ToTable("chara_data_file_swaps");
        mb.Entity<CharaDataFileSwap>().HasKey(c => new { c.ParentId, c.ParentUploaderUID, c.GamePath });
        mb.Entity<CharaDataFileSwap>().HasIndex(c => c.ParentId);
        mb.Entity<CharaDataPose>().ToTable("chara_data_poses");
        mb.Entity<CharaDataPose>().Property(p => p.Id).ValueGeneratedOnAdd();
        mb.Entity<CharaDataPose>().HasKey(c => new { c.ParentId, c.ParentUploaderUID, c.Id });
        mb.Entity<CharaDataPose>().HasIndex(c => c.ParentId);
        mb.Entity<CharaDataOriginalFile>().ToTable("chara_data_orig_files");
        mb.Entity<CharaDataOriginalFile>().HasKey(c => new { c.ParentId, c.ParentUploaderUID, c.GamePath });
        mb.Entity<CharaDataOriginalFile>().HasIndex(c => c.ParentId);
        mb.Entity<CharaDataAllowance>().ToTable("chara_data_allowance");
        mb.Entity<CharaDataAllowance>().HasKey(c => new { c.ParentId, c.ParentUploaderUID, c.Id });
        mb.Entity<CharaDataAllowance>().Property(p => p.Id).ValueGeneratedOnAdd();
        mb.Entity<CharaDataAllowance>().HasIndex(c => c.ParentId);
        mb.Entity<CharaDataAllowance>().HasOne(u => u.AllowedGroup).WithMany().HasForeignKey(u => u.AllowedGroupGID).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<CharaDataAllowance>().HasOne(u => u.AllowedUser).WithMany().HasForeignKey(u => u.AllowedUserUID).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<AreaBoundSyncshell>().ToTable("area_bound_syncshells");
        mb.Entity<AreaBoundSyncshell>().HasKey(a => a.GroupGID);
        mb.Entity<AreaBoundSyncshell>().HasOne(a => a.Group).WithMany().HasForeignKey(a => a.GroupGID).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<AreaBoundSyncshell>().HasMany(a => a.Locations).WithOne(l => l.AreaBoundSyncshell).HasForeignKey(l => l.GroupGID).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<AreaBoundSyncshell>().HasIndex(a => a.AutoBroadcastEnabled);
        
        mb.Entity<AreaBoundLocation>().ToTable("area_bound_locations");
        mb.Entity<AreaBoundLocation>().HasKey(l => l.Id);
        mb.Entity<AreaBoundLocation>().HasIndex(l => new { l.ServerId, l.TerritoryId });
        mb.Entity<AreaBoundLocation>().HasIndex(l => new { l.ServerId, l.MapId });
        mb.Entity<AreaBoundLocation>().HasIndex(l => l.GroupGID);
        
        mb.Entity<AreaBoundSyncshellConsent>().ToTable("area_bound_syncshell_consents");
        mb.Entity<AreaBoundSyncshellConsent>().HasKey(c => c.Id);
        mb.Entity<AreaBoundSyncshellConsent>().HasOne(c => c.User).WithMany().HasForeignKey(c => c.UserUID).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<AreaBoundSyncshellConsent>().HasOne(c => c.Syncshell).WithMany().HasForeignKey(c => c.SyncshellGID).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<AreaBoundSyncshellConsent>().HasIndex(c => new { c.UserUID, c.SyncshellGID }).IsUnique();
        
        mb.Entity<SyncshellWelcomePage>().ToTable("syncshell_welcome_pages");
        mb.Entity<SyncshellWelcomePage>().HasKey(w => w.GroupGID);
        mb.Entity<SyncshellWelcomePage>().HasOne(w => w.Group).WithMany().HasForeignKey(w => w.GroupGID).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<SyncshellWelcomePage>().HasIndex(w => w.IsEnabled);
        
        mb.Entity<UserHousingProperty>().ToTable("user_housing_properties");
        mb.Entity<UserHousingProperty>().HasKey(p => p.Id);
        mb.Entity<UserHousingProperty>().HasOne(p => p.User).WithMany().HasForeignKey(p => p.UserUID).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<UserHousingProperty>().HasIndex(p => p.UserUID);
        mb.Entity<UserHousingProperty>().HasIndex(p => new { p.UserUID, p.ServerId, p.TerritoryId, p.WardId, p.HouseId, p.RoomId }).IsUnique();
    }
}
