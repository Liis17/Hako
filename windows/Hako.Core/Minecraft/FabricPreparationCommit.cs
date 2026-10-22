namespace Hako.Core.Minecraft;

internal static class FabricPreparationCommit
{
    public static async Task<bool> PrepareAndCommitIfCurrentAsync(
        Func<Task<FabricPreparation>> prepareAsync,
        Func<bool> isCurrent,
        Action<FabricPreparation> commit)
    {
        var preparation = await prepareAsync();
        if (!isCurrent()) return false;

        commit(preparation);
        return true;
    }
}
