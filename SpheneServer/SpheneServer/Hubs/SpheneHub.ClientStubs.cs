using Sphene.API.Data;
using Sphene.API.Data.Enum;
using Sphene.API.Dto;
using Sphene.API.Dto.CharaData;
using Sphene.API.Dto.Group;
using Sphene.API.Dto.User;

namespace SpheneServer.Hubs
{
    public partial class SpheneHub
    {
        public Task Client_DownloadReady(Guid requestId) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");

        public Task Client_GroupChangePermissions(GroupPermissionDto groupPermission) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");

        public Task Client_GroupDelete(GroupDto groupDto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");

        public Task Client_GroupPairChangeUserInfo(GroupPairUserInfoDto dto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");

        public Task Client_GroupPairJoined(GroupPairFullInfoDto groupPairInfoDto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");

        public Task Client_GroupPairLeft(GroupPairDto groupPairDto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");

        public Task Client_GroupSendFullInfo(GroupFullInfoDto groupInfo) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");

        public Task Client_GroupSendInfo(GroupInfoDto dto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");

        public Task Client_ReceiveServerMessage(MessageSeverity messageSeverity, string message) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");

        public Task Client_UpdateSystemInfo(SystemInfoDto systemInfo) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");

        public Task Client_UserAddClientPair(UserPairDto dto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");

        public Task Client_UserReceiveCharacterData(OnlineUserCharaDataDto dataDto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");

        public Task Client_UserReceiveUploadStatus(UserDto dto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");

        public Task Client_UserRemoveClientPair(UserDto dto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");

        public Task Client_UserSendOffline(UserDto dto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");

        public Task Client_UserSendOnline(OnlineUserIdentDto dto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");

        public Task Client_UserUpdateOtherPairPermissions(UserPermissionsDto dto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");

        public Task Client_UserUpdateProfile(UserDto dto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");

        public Task Client_UserUpdateSelfPairPermissions(UserPermissionsDto dto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_UserUpdateDefaultPermissions(DefaultPermissionsDto dto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_UserReceiveCharacterDataAcknowledgment(CharacterDataAcknowledgmentDto acknowledgmentDto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_UpdateUserIndividualPairStatusDto(UserIndividualPairStatusDto dto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_GroupChangeUserPairPermissions(GroupPairUserPermissionDto dto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_GposeLobbyJoin(UserData userData) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_GposeLobbyLeave(UserData userData) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_GposeLobbyPushCharacterData(CharaDataDownloadDto charaDownloadDto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_GposeLobbyPushPoseData(UserData userData, PoseData poseData) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_GposeLobbyPushWorldData(UserData userData, WorldData worldData) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_AreaBoundJoinRequest(AreaBoundJoinRequestDto dto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_AreaBoundJoinResponse(AreaBoundJoinResponseDto dto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_AreaBoundSyncshellBroadcast(AreaBoundBroadcastDto dto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_AreaBoundSyncshellConfigurationUpdate() => throw new PlatformNotSupportedException("Calling clientside method on server not supported");

        // Deathroll client stub methods
        public Task Client_DeathrollInvitationReceived(DeathrollInvitationDto invitation) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_DeathrollInvitationResponse(DeathrollInvitationResponseDto response) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_DeathrollGameStateUpdate(DeathrollGameStateDto gameState) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        
        // New lobby system client stub methods
        public Task Client_DeathrollLobbyJoinRequest(DeathrollJoinLobbyDto joinRequest) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_DeathrollLobbyOpenClose(string gameId, bool isOpen) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_DeathrollLobbyLeave(DeathrollLeaveLobbyDto leaveInfo) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_DeathrollGameStart(string gameId) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_DeathrollLobbyCanceled(string gameId) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_DeathrollPlayerReady(string gameId, string playerId, bool isReady) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_DeathrollLobbyAnnouncement(DeathrollLobbyAnnouncementDto announcement) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
        public Task Client_DeathrollTournamentUpdate(DeathrollTournamentStateDto dto) => throw new PlatformNotSupportedException("Calling clientside method on server not supported");
    }
}
