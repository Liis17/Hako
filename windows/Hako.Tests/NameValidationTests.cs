using Hako;

namespace Hako.Tests;

[TestClass]
public sealed class NameValidationTests
{
    [TestMethod]
    public async Task InvalidatedAsyncNameCheckCannotReplaceTheLatestResult()
    {
        var validation = new NameValidation();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishOldCheck = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldCheck = validation.ValidateAsync("New build", async _ =>
        {
            started.SetResult();
            return await finishOldCheck.Task;
        });

        await started.Task;
        validation.Invalidate();
        var currentCheck = await validation.ValidateAsync("New build", _ => Task.FromResult<string?>(null));
        finishOldCheck.SetResult("duplicate");
        var staleCheck = await oldCheck;

        Assert.IsTrue(currentCheck.IsCurrent);
        Assert.IsNull(currentCheck.ErrorKey);
        Assert.IsFalse(staleCheck.IsCurrent);
        Assert.IsNull(staleCheck.ErrorKey);
    }
}
