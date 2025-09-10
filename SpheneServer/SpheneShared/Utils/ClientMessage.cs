using Sphene.API.Data.Enum;

namespace SpheneShared.Utils;
public record ClientMessage(MessageSeverity Severity, string Message, string UID);
