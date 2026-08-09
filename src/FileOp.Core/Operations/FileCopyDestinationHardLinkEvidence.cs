using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;

namespace FileOp.Core.Operations;

/// <summary>
/// Obtains a verified positive hard-link-count observation for the recorded Copy
/// destination while the caller still holds the mutation lease. Implementations
/// must fail closed if the recorded root/leaf identity or namespace binding changed.
/// </summary>
public interface IFileCopyDestinationHardLinkEvidenceSource
{
    ValueTask<uint> ReadVerifiedHardLinkCountAsync(
        FileOperationActionHistory history,
        int ordinal,
        FileIdentity expectedDestinationIdentity,
        CancellationToken cancellationToken = default);
}
