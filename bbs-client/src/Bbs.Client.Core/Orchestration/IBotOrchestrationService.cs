using System.Threading;
using System.Threading.Tasks;
using Bbs.Client.Core.Domain;

namespace Bbs.Client.Core.Orchestration;

public interface IBotOrchestrationService
{
    Task<BotOrchestrationResult> LaunchBotAsync(BotProfile profile, CancellationToken cancellationToken = default);
    Task<BotOrchestrationResult> StopBotAsync(BotProfile profile, CancellationToken cancellationToken = default);
}

public sealed record BotOrchestrationResult(
    bool Succeeded,
    AgentRuntimeState RuntimeState,
    string Message);
