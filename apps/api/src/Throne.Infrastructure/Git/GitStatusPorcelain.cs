namespace Throne.Infrastructure.Git;

/// <summary>
/// Detects the on-disk signature of an interrupted <c>git checkout</c>: the index
/// already matches the target commit (it was written first), but files never made
/// it to the working tree because the process was killed mid-way through the
/// (blob-by-blob, lazily-fetched) checkout. <c>git status --porcelain</c> reports
/// those paths as an <em>unstaged</em> deletion — index column ' ', worktree column
/// 'D' — which is distinct from a deliberate <c>git rm</c>/staged delete (index
/// column 'D'), so this never mistakes real user intent for a broken checkout.
/// </summary>
internal static class GitStatusPorcelain
{
    public static bool HasUnstagedDeletions(string? porcelainOutput)
    {
        if (string.IsNullOrEmpty(porcelainOutput))
        {
            return false;
        }

        foreach (var line in porcelainOutput.Split('\n'))
        {
            if (line.Length >= 2 && line[0] == ' ' && line[1] == 'D')
            {
                return true;
            }
        }

        return false;
    }
}
