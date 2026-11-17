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
    public DbSet<GroupMembershipApplication> GroupMembershipApplications { get; set; }
    public DbSet<CommunityMedia> CommunityMedia { get; set; }
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
    public DbSet<SyncshellCommunityEvent> SyncshellCommunityEvents { get; set; }
    public DbSet<SyncshellCommunityEventParticipant> CommunityEventParticipants { get; set; }
    public DbSet<SyncshellCommunityEventException> CommunityEventExceptions { get; set; }
    public DbSet<SyncshellCommunityNewsPost> SyncshellCommunityNews { get; set; }
    public DbSet<SyncshellCommunityGalleryImage> SyncshellCommunityGallery { get; set; }
    public DbSet<UserBlock> UserBlocks { get; set; }
    public DbSet<ChatMessage> ChatMessages { get; set; }
    public DbSet<ChatMessageCipher> ChatMessageCiphers { get; set; }
    public DbSet<ChatDeviceKey> ChatDeviceKeys { get; set; }
    public DbSet<ChatKeyBackup> ChatKeyBackups { get; set; }
    public DbSet<SyncshellCommunityGalleryAlbum> SyncshellCommunityGalleryAlbums { get; set; }
    public DbSet<SyncshellCommunityStaffRole> SyncshellCommunityStaffRoles { get; set; }
    public DbSet<SyncshellCommunityStaffAssignment> SyncshellCommunityStaffAssignments { get; set; }
    public DbSet<SyncshellCommunityGalleryImageLike> SyncshellCommunityGalleryImageLikes { get; set; }
    public DbSet<UserHousingProperty> UserHousingProperties { get; set; }
    public DbSet<UserProfileGalleryImage> UserProfileGalleryImages { get; set; }
    public DbSet<UserProfileGalleryAlbum> UserProfileGalleryAlbums { get; set; }
    public DbSet<UserProfileGalleryImageLike> UserProfileGalleryImageLikes { get; set; }
    public DbSet<UserProfileBlogPost> UserProfileBlogPosts { get; set; }
    public DbSet<ModFile> ModFiles { get; set; }
    public DbSet<ModDownloadHistory> ModDownloadHistory { get; set; }
    public DbSet<ModShareHistory> ModShareHistory { get; set; }
    public DbSet<PendingFileTransfer> PendingFileTransfers { get; set; }
    public DbSet<Announcement> Announcements { get; set; }
    public DbSet<UserNotification> UserNotifications { get; set; }
    public DbSet<CommunityComment> CommunityComments { get; set; }
    public DbSet<CommunityReaction> CommunityReactions { get; set; }
    public DbSet<CommunityReport> CommunityReports { get; set; }
    public DbSet<CommunityModerationLog> CommunityModerationLogs { get; set; }
    public DbSet<SyncshellFavorite> SyncshellFavorites { get; set; }

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<ModFile>().ToTable("mod_files");
        mb.Entity<ModFile>().HasKey(m => m.Hash);

        mb.Entity<Auth>().ToTable("auth");
        mb.Entity<Auth>().HasOne(a => a.User).WithMany().HasForeignKey(a => a.UserUID).HasConstraintName("fk_auth_users_user_uid");
        mb.Entity<Auth>().HasOne(a => a.PrimaryUser).WithMany().HasForeignKey(a => a.PrimaryUserUID).HasConstraintName("fk_auth_users_primary_user_uid");
        mb.Entity<User>().ToTable("users");
        mb.Entity<FileCache>().ToTable("file_caches");
        mb.Entity<FileCache>().HasIndex(c => c.UploaderUID);
        mb.Entity<ModDownloadHistory>().ToTable("mod_download_history");
        mb.Entity<ModDownloadHistory>().HasKey(h => h.Id);
        mb.Entity<ModDownloadHistory>().HasIndex(h => h.UserUID);
        mb.Entity<ModDownloadHistory>().HasIndex(h => h.Hash);
        mb.Entity<ModDownloadHistory>().HasIndex(h => h.DownloadedAt);
        mb.Entity<ModShareHistory>().ToTable("mod_share_history");
        mb.Entity<ModShareHistory>().HasKey(h => h.Id);
        mb.Entity<ModShareHistory>().HasIndex(h => h.SenderUID);
        mb.Entity<ModShareHistory>().HasIndex(h => h.RecipientUID);
        mb.Entity<ModShareHistory>().HasIndex(h => h.Hash);
        mb.Entity<ModShareHistory>().HasIndex(h => h.SharedAt);
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
        // Membership applications · one row per (group, applicant) - the unique
        // key is the duplicate/parallel-application guard, not just a lookup.
        mb.Entity<GroupMembershipApplication>().ToTable("group_membership_applications");
        mb.Entity<GroupMembershipApplication>().HasKey(a => a.Id);
        mb.Entity<GroupMembershipApplication>().HasIndex(a => new { a.GroupGID, a.ApplicantUID }).IsUnique();
        mb.Entity<GroupMembershipApplication>().HasIndex(a => a.ApplicantUID);
        mb.Entity<GroupMembershipApplication>().HasIndex(a => new { a.GroupGID, a.Status });
        mb.Entity<GroupMembershipApplication>().HasOne(a => a.Group).WithMany()
            .HasForeignKey(a => a.GroupGID).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<GroupMembershipApplication>().HasOne(a => a.Applicant).WithMany()
            .HasForeignKey(a => a.ApplicantUID).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<GroupMembershipApplication>().HasOne(a => a.ReviewedBy).WithMany()
            .HasForeignKey(a => a.ReviewedByUID).OnDelete(DeleteBehavior.SetNull);
        mb.Entity<GroupTempInvite>().ToTable("group_temp_invites");
        mb.Entity<GroupTempInvite>().HasKey(u => new { u.GroupGID, u.Invite });
        mb.Entity<GroupTempInvite>().HasIndex(c => c.GroupGID);
        mb.Entity<GroupTempInvite>().HasIndex(c => c.Invite);
        mb.Entity<UserProfileData>().ToTable("user_profile_data");
        mb.Entity<UserProfileData>().HasKey(c => c.UserUID);
        mb.Entity<UserProfileGalleryImage>().ToTable("user_profile_gallery_images");
        mb.Entity<UserProfileGalleryImage>().HasIndex(g => g.UserUID);
        mb.Entity<UserProfileGalleryImage>().HasOne(g => g.Album).WithMany(a => a.Images)
            .HasForeignKey(g => g.AlbumId).OnDelete(DeleteBehavior.SetNull);
        mb.Entity<UserProfileGalleryAlbum>().ToTable("user_profile_gallery_albums");
        mb.Entity<UserProfileGalleryAlbum>().HasIndex(a => a.UserUID);
        mb.Entity<UserProfileGalleryImageLike>().ToTable("user_profile_gallery_image_likes");
        mb.Entity<UserProfileGalleryImageLike>().HasKey(l => new { l.ImageId, l.LikerUID });
        mb.Entity<UserProfileGalleryImageLike>().HasOne(l => l.Image).WithMany()
            .HasForeignKey(l => l.ImageId).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<UserProfileGalleryImageLike>().HasIndex(l => l.LikerUID);
        mb.Entity<UserProfileBlogPost>().ToTable("user_profile_blog_posts");
        mb.Entity<UserProfileBlogPost>().HasIndex(b => b.UserUID);
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
        mb.Entity<SyncshellWelcomePage>().HasIndex(w => w.IsListedPublicly);

        mb.Entity<SyncshellCommunityEvent>().ToTable("syncshell_community_events");
        mb.Entity<SyncshellCommunityEvent>().HasKey(e => e.Id);
        mb.Entity<SyncshellCommunityEvent>().HasOne(e => e.Group).WithMany().HasForeignKey(e => e.GroupGID).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<SyncshellCommunityEvent>().HasIndex(e => e.GroupGID);
        mb.Entity<SyncshellCommunityEvent>().HasIndex(e => e.StartUtc);
        // RsvpVersion is a plain monotonic counter - concurrent RSVP writers
        // serialize on the event row's FOR UPDATE lock, not on a token.

        mb.Entity<SyncshellCommunityEventParticipant>().ToTable("community_event_participants");
        mb.Entity<SyncshellCommunityEventParticipant>().HasKey(p => new { p.EventId, p.UserUID });
        mb.Entity<SyncshellCommunityEventParticipant>().HasOne(p => p.Event)
            .WithMany(e => e.Participants).HasForeignKey(p => p.EventId).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<SyncshellCommunityEventParticipant>().HasOne(p => p.User).WithMany()
            .HasForeignKey(p => p.UserUID).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<SyncshellCommunityEventParticipant>().HasIndex(p => new { p.EventId, p.Status });
        mb.Entity<SyncshellCommunityEventParticipant>().HasIndex(p => p.WaitlistPosition)
            .HasFilter("waitlist_position IS NOT NULL");

        mb.Entity<SyncshellCommunityEventException>().ToTable("community_event_exceptions");
        mb.Entity<SyncshellCommunityEventException>().HasKey(x => new { x.EventId, x.OccurrenceStartUtc });
        mb.Entity<SyncshellCommunityEventException>().HasOne(x => x.Event)
            .WithMany(e => e.Exceptions).HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Cascade);

        mb.Entity<SyncshellCommunityNewsPost>().ToTable("syncshell_community_news");
        mb.Entity<SyncshellCommunityNewsPost>().HasKey(n => n.Id);
        mb.Entity<SyncshellCommunityNewsPost>().HasOne(n => n.Group).WithMany().HasForeignKey(n => n.GroupGID).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<SyncshellCommunityNewsPost>().HasIndex(n => n.GroupGID);
        mb.Entity<SyncshellCommunityNewsPost>().HasIndex(n => n.PublishedAt);

        mb.Entity<SyncshellCommunityGalleryImage>().ToTable("syncshell_community_gallery");
        mb.Entity<SyncshellCommunityGalleryImage>().HasKey(g => g.Id);
        mb.Entity<SyncshellCommunityGalleryImage>().HasOne(g => g.Group).WithMany().HasForeignKey(g => g.GroupGID).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<SyncshellCommunityGalleryImage>().HasIndex(g => g.GroupGID);
        mb.Entity<SyncshellCommunityGalleryImage>().HasIndex(g => g.AlbumId);

        mb.Entity<SyncshellCommunityGalleryAlbum>().ToTable("syncshell_community_gallery_albums");
        mb.Entity<SyncshellCommunityGalleryAlbum>().HasKey(a => a.Id);
        mb.Entity<SyncshellCommunityGalleryAlbum>().HasOne(a => a.Group).WithMany().HasForeignKey(a => a.GroupGID).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<SyncshellCommunityGalleryAlbum>().HasIndex(a => a.GroupGID);

        mb.Entity<SyncshellCommunityStaffRole>().ToTable("syncshell_community_staff_roles");
        mb.Entity<SyncshellCommunityStaffRole>().HasKey(r => r.Id);
        mb.Entity<SyncshellCommunityStaffRole>().HasOne(r => r.Group).WithMany().HasForeignKey(r => r.GroupGID).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<SyncshellCommunityStaffRole>().HasIndex(r => r.GroupGID);

        mb.Entity<SyncshellCommunityStaffAssignment>().ToTable("syncshell_community_staff_assignments");
        mb.Entity<SyncshellCommunityStaffAssignment>().HasKey(a => a.Id);
        mb.Entity<SyncshellCommunityStaffAssignment>().HasOne(a => a.Group).WithMany().HasForeignKey(a => a.GroupGID).OnDelete(DeleteBehavior.Cascade);

        mb.Entity<SyncshellCommunityGalleryImageLike>().ToTable("syncshell_community_gallery_image_likes");
        mb.Entity<SyncshellCommunityGalleryImageLike>().HasKey(l => new { l.ImageId, l.UserUID });
        mb.Entity<SyncshellCommunityGalleryImageLike>().HasOne(l => l.Image).WithMany().HasForeignKey(l => l.ImageId).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<SyncshellCommunityGalleryImageLike>().HasOne(l => l.Group).WithMany().HasForeignKey(l => l.GroupGID).OnDelete(DeleteBehavior.NoAction);
        mb.Entity<SyncshellCommunityGalleryImageLike>().HasIndex(l => l.GroupGID);
        mb.Entity<SyncshellCommunityGalleryImageLike>().HasIndex(l => l.UserUID);
        mb.Entity<SyncshellCommunityStaffAssignment>().HasIndex(a => a.RoleId);
        mb.Entity<SyncshellCommunityStaffAssignment>().HasIndex(a => new { a.GroupGID, a.UserUID, a.RoleId }).IsUnique();
        
        mb.Entity<UserHousingProperty>().ToTable("user_housing_properties");
        mb.Entity<UserHousingProperty>().HasKey(p => p.Id);
        mb.Entity<UserHousingProperty>().HasOne(p => p.User).WithMany().HasForeignKey(p => p.UserUID).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<UserHousingProperty>().HasIndex(p => p.UserUID);
        mb.Entity<UserHousingProperty>().HasIndex(p => new { p.UserUID, p.ServerId, p.TerritoryId, p.WardId, p.HouseId, p.RoomId }).IsUnique();
        
        mb.Entity<PendingFileTransfer>().ToTable("pending_file_transfers");
        mb.Entity<PendingFileTransfer>().HasKey(p => p.Id);
        mb.Entity<PendingFileTransfer>().HasIndex(p => p.RecipientUID);

        mb.Entity<Announcement>().ToTable("announcements");
        mb.Entity<Announcement>().HasKey(a => a.Id);
        mb.Entity<Announcement>().HasIndex(a => a.PublishedAt);
        mb.Entity<Announcement>().HasIndex(a => a.IsDraft);

        mb.Entity<UserNotification>().ToTable("user_notifications");
        mb.Entity<UserNotification>().HasKey(n => n.Id);
        mb.Entity<UserNotification>().HasOne(n => n.Recipient).WithMany()
            .HasForeignKey(n => n.RecipientUID).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<UserNotification>().HasIndex(n => n.RecipientUID);
        mb.Entity<UserNotification>().HasIndex(n => new { n.RecipientUID, n.IsRead });
        mb.Entity<UserNotification>().HasIndex(n => new { n.RecipientUID, n.DedupeKey })
            .IsUnique().HasFilter("dedupe_key IS NOT NULL");

        // Phase 4 - comments: polymorphic target + depth-2 threading.
        // Roots carry parent_id IS NULL AND root_id IS NULL; replies carry both.
        // The CHECK pins the invariant - a reply with a null root_id can never
        // commit, which removes the need to pre-fetch a generated id.
        mb.Entity<CommunityComment>().ToTable("community_comments", t =>
            t.HasCheckConstraint("ck_community_comments_root",
                "(parent_id IS NULL) = (root_id IS NULL)"));
        mb.Entity<CommunityComment>().HasKey(c => c.Id);
        mb.Entity<CommunityComment>().HasOne(c => c.Group).WithMany()
            .HasForeignKey(c => c.GroupGID).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<CommunityComment>().HasOne(c => c.Owner).WithMany()
            .HasForeignKey(c => c.OwnerUid).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<CommunityComment>().HasOne(c => c.Author).WithMany()
            .HasForeignKey(c => c.AuthorUid).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<CommunityComment>().HasOne(c => c.Parent).WithMany()
            .HasForeignKey(c => c.ParentId).OnDelete(DeleteBehavior.Restrict);
        mb.Entity<CommunityComment>().HasOne(c => c.Root).WithMany()
            .HasForeignKey(c => c.RootId).OnDelete(DeleteBehavior.Restrict);
        mb.Entity<CommunityComment>().HasIndex(c => new { c.TargetKind, c.TargetId, c.State, c.Id });
        mb.Entity<CommunityComment>().HasIndex(c => new { c.RootId, c.Id })
            .HasFilter("root_id IS NOT NULL");
        mb.Entity<CommunityComment>().HasIndex(c => c.AuthorUid);
        mb.Entity<CommunityComment>().HasIndex(c => c.GroupGID);

        mb.Entity<CommunityReaction>().ToTable("community_reactions");
        mb.Entity<CommunityReaction>().HasKey(r => new { r.TargetKind, r.TargetId, r.UserUID, r.Kind });
        mb.Entity<CommunityReaction>().HasOne(r => r.User).WithMany()
            .HasForeignKey(r => r.UserUID).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<CommunityReaction>().HasIndex(r => new { r.TargetKind, r.TargetId, r.Kind });
        mb.Entity<CommunityReaction>().HasIndex(r => r.GroupGID);
        mb.Entity<CommunityReaction>().HasIndex(r => r.UserUID);

        mb.Entity<CommunityReport>().ToTable("community_reports");
        mb.Entity<CommunityReport>().HasKey(r => r.Id);
        mb.Entity<CommunityReport>().HasOne(r => r.Group).WithMany()
            .HasForeignKey(r => r.GroupGID).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<CommunityReport>().HasOne(r => r.Owner).WithMany()
            .HasForeignKey(r => r.OwnerUid).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<CommunityReport>().HasOne(r => r.Reporter).WithMany()
            .HasForeignKey(r => r.ReporterUid).OnDelete(DeleteBehavior.Cascade);
        // One OPEN report per reporter per target - resolved reports may be
        // re-filed; the partial unique index is the dedupe authority.
        mb.Entity<CommunityReport>().HasIndex(r => new { r.ReporterUid, r.TargetKind, r.TargetId })
            .IsUnique().HasFilter("state = 0");
        mb.Entity<CommunityReport>().HasIndex(r => new { r.GroupGID, r.State });
        mb.Entity<CommunityReport>().HasIndex(r => new { r.OwnerUid, r.State });

        mb.Entity<CommunityModerationLog>().ToTable("community_moderation_log");
        mb.Entity<CommunityModerationLog>().HasKey(l => l.Id);
        mb.Entity<CommunityModerationLog>().HasIndex(l => new { l.TargetKind, l.TargetId });
        mb.Entity<CommunityModerationLog>().HasIndex(l => l.ActorUid);
        mb.Entity<CommunityModerationLog>().HasIndex(l => l.GroupGID);

        mb.Entity<ChatMessageCipher>().ToTable("chat_message_ciphers");
        mb.Entity<ChatMessageCipher>().HasKey(c => new { c.MessageId, c.ForDeviceId });
        mb.Entity<ChatMessageCipher>().HasOne(c => c.Message).WithMany(m => m.Ciphers)
            .HasForeignKey(c => c.MessageId).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<ChatMessageCipher>().HasIndex(c => c.ForUID);

        mb.Entity<ChatDeviceKey>().ToTable("chat_device_keys");
        mb.Entity<ChatDeviceKey>().HasKey(k => new { k.UserUID, k.DeviceId });
        mb.Entity<ChatDeviceKey>().HasOne(k => k.User).WithMany()
            .HasForeignKey(k => k.UserUID).OnDelete(DeleteBehavior.Cascade);

        mb.Entity<ChatKeyBackup>().ToTable("chat_key_backups");
        mb.Entity<ChatKeyBackup>().HasKey(k => k.UserUID);
        mb.Entity<ChatKeyBackup>().Property(k => k.Revision).IsConcurrencyToken();
        mb.Entity<ChatKeyBackup>().HasOne(k => k.User).WithMany()
            .HasForeignKey(k => k.UserUID).OnDelete(DeleteBehavior.Cascade);
    }
}
