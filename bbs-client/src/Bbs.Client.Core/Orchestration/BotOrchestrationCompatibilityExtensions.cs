using System.Threading;
using System.Threading.Tasks;
using Bbs.Client.Core.Domain;

namespace Bbs.Client.Core.Orchestration;

public static class BotOrchestrationCompatibilityExtensions
{
    public static Task<BotOrchestrationResult> ArmBotAsync(this IBotOrchestrationService service, BotProfile profile, CancellationToken cancellationToken = default)
        => service.LaunchBotAsync(profile, cancellationToken);

    public static Task<BotOrchestrationResult> DisarmBotAsync(this IBotOrchestrationService service, BotProfile profile, CancellationToken cancellationToken = default)
        => service.StopBotAsync(profile, cancellationToken);
}
