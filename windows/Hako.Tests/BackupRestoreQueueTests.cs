namespace Hako.Tests;

[TestClass]
public sealed class BackupRestoreQueueTests
{
    [TestMethod]
    public void EnqueueNormalizesPathsSuppressesDuplicatesAndPreservesOrder()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var firstPath = Path.Combine(root, "build.hakobackup");
        var duplicatePath = Path.Combine(root, "nested", "..", "BUILD.HAKOBACKUP");
        var worldPath = Path.Combine(root, "world.hakoworld");
        var queue = new BackupRestoreQueue();

        Assert.IsTrue(queue.Enqueue(firstPath));
        Assert.IsFalse(queue.Enqueue(duplicatePath));
        Assert.IsFalse(queue.Enqueue(Path.Combine(root, "other.zip")));
        Assert.IsTrue(queue.Enqueue(worldPath));

        Assert.IsTrue(queue.TryDequeue(out var first));
        Assert.AreEqual(Path.GetFullPath(firstPath), first.Path);
        Assert.IsTrue(queue.TryDequeue(out var world));
        Assert.AreEqual(Path.GetFullPath(worldPath), world.Path);
        Assert.IsFalse(queue.HasPending);

        queue.Complete(first);
        Assert.IsTrue(queue.Enqueue(duplicatePath));
    }
}
