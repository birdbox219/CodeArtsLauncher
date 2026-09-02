using System.Threading;
using System.Threading.Tasks;
using Launcher.Core.Models;

namespace Launcher.Core.Abstractions;

public interface IConfigService
{
    Task<LauncherConfig> LoadConfigAsync(CancellationToken ct = default);
    Task SaveConfigAsync(LauncherConfig config, CancellationToken ct = default);
}
