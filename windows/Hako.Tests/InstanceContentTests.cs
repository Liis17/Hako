using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hako.Core;
using Hako.Core.Instances;
using Hako.Core.Minecraft;

namespace Hako.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class InstanceContentTests
{
    [TestMethod]
    public async Task LocalImportCopiesJarZipAndPackFolderAndClearsModOrigin()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root) = await CreateReadyInstanceAsync(temporary, legacyTexturepacks: false);
        var content = new InstanceContent(store);
        var minecraft = Path.Combine(root, "minecraft");
        var mods = Path.Combine(minecraft, "mods");
        Directory.CreateDirectory(mods);
        var oldBytes = Encoding.UTF8.GetBytes("old mod");
        var newBytes = Encoding.UTF8.GetBytes("local replacement");
        await File.WriteAllBytesAsync(Path.Combine(mods, "Local.jar"), oldBytes);
        var original = Origin("old-version", oldBytes);
        InstanceContent.WriteRegistry(root, new ModRegistry
        {
            ApiProvisioned = true,
            Files = new Dictionary<string, ModOrigin>(StringComparer.Ordinal) { ["local.jar"] = original }
        });

        var sourceJar = Path.Combine(temporary.Root, "Local.jar");
        await File.WriteAllBytesAsync(sourceJar, newBytes);
        await content.ImportAsync(instance.Id, ModrinthContentKind.Mod, sourceJar, replace: true);
        var listedMods = await content.ListAsync(instance.Id, ModrinthContentKind.Mod);
        Assert.AreEqual(1, listedMods.Count);
        Assert.IsNull(listedMods.Single().Origin);
        CollectionAssert.AreEqual(newBytes, await File.ReadAllBytesAsync(Path.Combine(mods, "Local.jar")));
        var modRegistry = InstanceContent.ReadRegistry(root);
        Assert.IsTrue(modRegistry.ApiProvisioned);
        Assert.IsFalse(modRegistry.Files.ContainsKey("local.jar"));

        var zipSource = Path.Combine(temporary.Root, "Pack.zip");
        var zipBytes = Encoding.UTF8.GetBytes("zip bytes remain an archive");
        await File.WriteAllBytesAsync(zipSource, zipBytes);
        await content.ImportAsync(instance.Id, ModrinthContentKind.ResourcePack, zipSource);
        CollectionAssert.AreEqual(zipBytes, await File.ReadAllBytesAsync(Path.Combine(minecraft, "resourcepacks", "Pack.zip")));

        var folderSource = Path.Combine(temporary.Root, "FolderPack");
        Directory.CreateDirectory(folderSource);
        await File.WriteAllTextAsync(Path.Combine(folderSource, "pack.mcmeta"), "{\"pack\":{\"pack_format\":15,\"description\":\"test\"}}");
        await File.WriteAllBytesAsync(Path.Combine(folderSource, "pack.png"), [1, 2, 3]);
        await content.ImportAsync(instance.Id, ModrinthContentKind.ResourcePack, folderSource);
        Assert.IsTrue(File.Exists(Path.Combine(minecraft, "resourcepacks", "FolderPack", "pack.mcmeta")));
        Assert.IsTrue(File.Exists(Path.Combine(minecraft, "resourcepacks", "FolderPack", "pack.png")));
        Assert.AreEqual(2, (await content.ListAsync(instance.Id, ModrinthContentKind.ResourcePack)).Count);
    }

    [TestMethod]
    public async Task MacOsModAndResourcepackRegistryJSONLoadsWithOptionalOriginsAndExactKeys()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root) = await CreateReadyInstanceAsync(temporary);
        var bytes = Encoding.UTF8.GetBytes("macOS format");
        var sha512 = Hash(bytes);
        var minecraft = Path.Combine(root, "minecraft");
        var mods = Path.Combine(minecraft, "mods");
        var packs = Path.Combine(minecraft, "resourcepacks");
        Directory.CreateDirectory(mods);
        Directory.CreateDirectory(packs);
        await File.WriteAllBytesAsync(Path.Combine(mods, "Fixture.jar.disabled"), bytes);
        await File.WriteAllBytesAsync(Path.Combine(packs, "Fixture.zip"), bytes);
        var origin = $"{{\"source\":\"modrinth\",\"projectID\":\"project123\",\"versionID\":\"version123\",\"pageURL\":\"https://modrinth.com/mod/project123\",\"sha512\":\"{sha512}\"}}";
        await File.WriteAllTextAsync(Path.Combine(minecraft, ".hako-mods.json"),
            $"{{\"apiProvisioned\":true,\"files\":{{\"fixture.jar\":{origin}}}}}");
        await File.WriteAllTextAsync(Path.Combine(minecraft, ".hako-resourcepacks.json"),
            $"{{\"apiProvisioned\":false,\"files\":{{\"fixture.zip\":{origin}}}}}");

        var content = new InstanceContent(store);
        var mod = (await content.ListAsync(instance.Id, ModrinthContentKind.Mod)).Single();
        var pack = (await content.ListAsync(instance.Id, ModrinthContentKind.ResourcePack)).Single();

        Assert.IsFalse(mod.Enabled);
        Assert.AreEqual("project123", mod.Origin?.ProjectID);
        Assert.IsNull(mod.Origin?.VersionNumber);
        Assert.AreEqual("version123", pack.Origin?.VersionID);
        Assert.IsNull(pack.Origin?.IconURL);
        Assert.IsTrue(InstanceContent.ReadRegistry(root).ApiProvisioned);
        Assert.IsFalse(InstanceContent.ReadRegistry(root, ".hako-resourcepacks.json").ApiProvisioned);
    }

    [TestMethod]
    public async Task ListingAndTogglingDoNotLoseOriginAndApiProvisioningState()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root) = await CreateReadyInstanceAsync(temporary);
        var bytes = Encoding.UTF8.GetBytes("fabric api mod");
        var folder = Path.Combine(root, "minecraft", "mods");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "fabric-api.jar");
        await File.WriteAllBytesAsync(path, bytes);
        InstanceContent.WriteRegistry(root, new ModRegistry
        {
            ApiProvisioned = true,
            Files = new Dictionary<string, ModOrigin>(StringComparer.Ordinal) { ["fabric-api.jar"] = Origin("api-version", bytes) }
        });
        var content = new InstanceContent(store);

        var enabled = (await content.ListAsync(instance.Id, ModrinthContentKind.Mod)).Single();
        await content.SetEnabledAsync(instance.Id, enabled, ModrinthContentKind.Mod, false);
        var disabled = (await content.ListAsync(instance.Id, ModrinthContentKind.Mod)).Single();

        Assert.AreEqual("api-version", disabled.Origin?.VersionID);
        Assert.IsTrue(InstanceContent.ReadRegistry(root).ApiProvisioned);
        Assert.AreEqual(Hash(bytes), disabled.Origin?.Sha512);
    }

    [TestMethod]
    public async Task TogglePreservesLogicalModAndPackAndUsesSwiftDisabledPackFolders()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root) = await CreateReadyInstanceAsync(temporary, legacyTexturepacks: false);
        var content = new InstanceContent(store);
        var modSource = Path.Combine(temporary.Root, "toggle.jar");
        await File.WriteAllTextAsync(modSource, "mod");
        await content.ImportAsync(instance.Id, ModrinthContentKind.Mod, modSource);
        var mod = (await content.ListAsync(instance.Id, ModrinthContentKind.Mod)).Single();
        await content.SetEnabledAsync(instance.Id, mod, ModrinthContentKind.Mod, enabled: false);
        var afterDisable = await content.ListAsync(instance.Id, ModrinthContentKind.Mod);
        Assert.AreEqual(1, afterDisable.Count, "Files: " + string.Join(",", Directory.GetFiles(Path.Combine(root, "minecraft", "mods")).Select(Path.GetFileName)));
        Assert.AreEqual(0, (await content.ListAsync(instance.Id, ModrinthContentKind.Mod, includeDisabled: false)).Count);
        var disabledMod = afterDisable.Single();
        Assert.IsFalse(disabledMod.Enabled);
        Assert.AreEqual("toggle.jar.disabled", disabledMod.Name);
        await content.SetEnabledAsync(instance.Id, disabledMod, ModrinthContentKind.Mod, enabled: true);
        Assert.AreEqual("toggle.jar", (await content.ListAsync(instance.Id, ModrinthContentKind.Mod)).Single().Name);

        var packFolder = Path.Combine(temporary.Root, "PackFolder");
        Directory.CreateDirectory(packFolder);
        await File.WriteAllTextAsync(Path.Combine(packFolder, "pack.mcmeta"), "{\"pack\":{}}");
        await content.ImportAsync(instance.Id, ModrinthContentKind.ResourcePack, packFolder);
        var pack = (await content.ListAsync(instance.Id, ModrinthContentKind.ResourcePack)).Single();
        await content.SetEnabledAsync(instance.Id, pack, ModrinthContentKind.ResourcePack, enabled: false);
        var disabledPack = (await content.ListAsync(instance.Id, ModrinthContentKind.ResourcePack)).Single();
        Assert.IsTrue(disabledPack.DisabledPack);
        Assert.AreEqual(Path.Combine("minecraft", ".hako-disabled-resourcepacks", "PackFolder"),
            Path.GetRelativePath(root, disabledPack.Path));
        await content.SetEnabledAsync(instance.Id, disabledPack, ModrinthContentKind.ResourcePack, enabled: true);
        Assert.IsFalse((await content.ListAsync(instance.Id, ModrinthContentKind.ResourcePack)).Single().DisabledPack);

        var (legacyStore, legacyInstance, legacyRoot) = await CreateReadyInstanceAsync(temporary, "Legacy", legacyTexturepacks: true);
        var legacyContent = new InstanceContent(legacyStore);
        var legacySource = Path.Combine(temporary.Root, "LegacyPack.zip");
        await File.WriteAllBytesAsync(legacySource, [4, 5, 6]);
        await legacyContent.ImportAsync(legacyInstance.Id, ModrinthContentKind.ResourcePack, legacySource);
        var legacyPack = (await legacyContent.ListAsync(legacyInstance.Id, ModrinthContentKind.ResourcePack)).Single();
        Assert.IsTrue(legacyPack.Path.Contains(Path.Combine("minecraft", "texturepacks"), StringComparison.OrdinalIgnoreCase));
        await legacyContent.SetEnabledAsync(legacyInstance.Id, legacyPack, ModrinthContentKind.ResourcePack, enabled: false);
        var legacyDisabled = (await legacyContent.ListAsync(legacyInstance.Id, ModrinthContentKind.ResourcePack)).Single();
        Assert.AreEqual(Path.Combine("minecraft", ".hako-disabled-texturepacks", "LegacyPack.zip"),
            Path.GetRelativePath(legacyRoot, legacyDisabled.Path));
    }

    [TestMethod]
    public async Task CaseInsensitiveDisabledCollisionsPreserveBothFiles()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root) = await CreateReadyInstanceAsync(temporary);
        var content = new InstanceContent(store);
        var mods = Path.Combine(root, "minecraft", "mods");
        Directory.CreateDirectory(mods);
        await File.WriteAllTextAsync(Path.Combine(mods, "collision.jar"), "enabled");
        await File.WriteAllTextAsync(Path.Combine(mods, "COLLISION.jar.disabled"), "disabled");
        var mod = (await content.ListAsync(instance.Id, ModrinthContentKind.Mod)).First(item => item.Name == "collision.jar");
        await Assert.ThrowsExactlyAsync<IOException>(() => content.SetEnabledAsync(instance.Id, mod, ModrinthContentKind.Mod, false));
        Assert.AreEqual("enabled", await File.ReadAllTextAsync(Path.Combine(mods, "collision.jar")));
        Assert.AreEqual("disabled", await File.ReadAllTextAsync(Path.Combine(mods, "COLLISION.jar.disabled")));

        var enabledFolder = Path.Combine(root, "minecraft", "resourcepacks");
        var disabledFolder = Path.Combine(root, "minecraft", ".hako-disabled-resourcepacks");
        Directory.CreateDirectory(enabledFolder);
        Directory.CreateDirectory(disabledFolder);
        await File.WriteAllBytesAsync(Path.Combine(enabledFolder, "Texture.zip"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(disabledFolder, "texture.ZIP"), [2]);
        var pack = (await content.ListAsync(instance.Id, ModrinthContentKind.ResourcePack)).First(item => item.Name == "Texture.zip");
        await Assert.ThrowsExactlyAsync<IOException>(() => content.SetEnabledAsync(instance.Id, pack, ModrinthContentKind.ResourcePack, false));
        CollectionAssert.AreEqual(new byte[] { 1 }, await File.ReadAllBytesAsync(Path.Combine(enabledFolder, "Texture.zip")));
        CollectionAssert.AreEqual(new byte[] { 2 }, await File.ReadAllBytesAsync(Path.Combine(disabledFolder, "texture.ZIP")));
    }

    [TestMethod]
    public async Task ReplaceChecksExpectedHashPreservesDisabledStateAndRollsBackPublicationFailure()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root) = await CreateReadyInstanceAsync(temporary);
        var initialContent = new InstanceContent(store);
        var oldBytes = Encoding.UTF8.GetBytes("old mod bytes");
        var newBytes = Encoding.UTF8.GetBytes("new mod bytes");
        var oldSource = Path.Combine(temporary.Root, "old.jar");
        var newSource = Path.Combine(temporary.Root, "new.jar");
        await File.WriteAllBytesAsync(oldSource, oldBytes);
        await File.WriteAllBytesAsync(newSource, newBytes);
        await initialContent.ImportAsync(instance.Id, ModrinthContentKind.Mod, oldSource);
        var modFolder = Path.Combine(root, "minecraft", "mods");
        InstanceContent.WriteRegistry(root, new ModRegistry
        {
            ApiProvisioned = true,
            Files = new Dictionary<string, ModOrigin>(StringComparer.Ordinal) { ["old.jar"] = Origin("old-version", oldBytes) }
        });
        var item = (await initialContent.ListAsync(instance.Id, ModrinthContentKind.Mod)).Single();
        await initialContent.SetEnabledAsync(instance.Id, item, ModrinthContentKind.Mod, enabled: false);
        var disabled = (await initialContent.ListAsync(instance.Id, ModrinthContentKind.Mod)).Single();
        var expected = Hash(oldBytes);
        var failure = new InstanceContent(store, null, () => throw new IOException("Injected registry commit failure."));

        await Assert.ThrowsExactlyAsync<IOException>(() => failure.ReplaceAsync(instance.Id, disabled, ModrinthContentKind.Mod,
            newSource, "new.jar", expected, Origin("new-version", newBytes)));
        CollectionAssert.AreEqual(oldBytes, await File.ReadAllBytesAsync(Path.Combine(modFolder, "old.jar.disabled")));
        Assert.IsFalse(File.Exists(Path.Combine(modFolder, "new.jar.disabled")));
        var unchanged = InstanceContent.ReadRegistry(root);
        Assert.IsTrue(unchanged.ApiProvisioned);
        Assert.AreEqual("old-version", unchanged.Files["old.jar"].VersionID);

        var changedHash = await Assert.ThrowsExactlyAsync<IOException>(() => initialContent.ReplaceAsync(instance.Id, disabled,
            ModrinthContentKind.Mod, newSource, "new.jar", Hash(Encoding.UTF8.GetBytes("stale")), Origin("new-version", newBytes)));
        StringAssert.Contains(changedHash.Message, "changed");

        await initialContent.ReplaceAsync(instance.Id, disabled, ModrinthContentKind.Mod, newSource, "new.jar", expected,
            Origin("new-version", newBytes));
        CollectionAssert.AreEqual(newBytes, await File.ReadAllBytesAsync(Path.Combine(modFolder, "new.jar.disabled")));
        Assert.IsFalse(File.Exists(Path.Combine(modFolder, "old.jar.disabled")));
        var updated = InstanceContent.ReadRegistry(root);
        Assert.IsTrue(updated.ApiProvisioned);
        Assert.IsFalse(updated.Files.ContainsKey("old.jar"));
        Assert.AreEqual("new-version", updated.Files["new.jar"].VersionID);
    }

    [TestMethod]
    public async Task ImportRollbackKeepsBackupAndReportsBothFailuresWhenSameNamedTargetIsLocked()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root) = await CreateReadyInstanceAsync(temporary);
        var mods = Path.Combine(root, "minecraft", "mods");
        Directory.CreateDirectory(mods);
        var oldBytes = Encoding.UTF8.GetBytes("old mod");
        var newBytes = Encoding.UTF8.GetBytes("new mod");
        var target = Path.Combine(mods, "same.jar");
        var source = Path.Combine(temporary.Root, "same.jar");
        await File.WriteAllBytesAsync(target, oldBytes);
        await File.WriteAllBytesAsync(source, newBytes);
        InstanceContent.WriteRegistry(root, new ModRegistry
        {
            ApiProvisioned = true,
            Files = new Dictionary<string, ModOrigin>(StringComparer.Ordinal) { ["same.jar"] = Origin("old-version", oldBytes) }
        });

        FileStream? targetLock = null;
        var registryFailure = new IOException("Injected registry commit failure.");
        var content = new InstanceContent(store, null, () =>
        {
            targetLock = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
            throw registryFailure;
        });
        IOException failure;
        try
        {
            failure = await Assert.ThrowsExactlyAsync<IOException>(() => content.ImportAsync(
                instance.Id, ModrinthContentKind.Mod, source, replace: true));
        }
        finally
        {
            targetLock?.Dispose();
        }

        Assert.IsInstanceOfType<AggregateException>(failure.InnerException);
        var rollbackErrors = (AggregateException)failure.InnerException!;
        Assert.AreSame(registryFailure, rollbackErrors.InnerExceptions[0]);
        Assert.AreEqual(3, rollbackErrors.InnerExceptions.Count);
        StringAssert.Contains(failure.Message, registryFailure.Message);
        var backup = Directory.GetFiles(mods, "same.jar.hako-backup-*").Single();
        StringAssert.Contains(failure.Message, backup);
        CollectionAssert.AreEqual(newBytes, await File.ReadAllBytesAsync(target));
        CollectionAssert.AreEqual(oldBytes, await File.ReadAllBytesAsync(backup));
        var registry = InstanceContent.ReadRegistry(root);
        Assert.IsTrue(registry.ApiProvisioned);
        Assert.AreEqual("old-version", registry.Files["same.jar"].VersionID);
    }

    [TestMethod]
    public async Task ReplaceRollbackRestoresOldNameWhenLockedDifferentTargetCannotBeRemoved()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root) = await CreateReadyInstanceAsync(temporary);
        var initial = new InstanceContent(store);
        var oldBytes = Encoding.UTF8.GetBytes("old mod");
        var newBytes = Encoding.UTF8.GetBytes("new mod");
        var oldSource = Path.Combine(temporary.Root, "old.jar");
        var newSource = Path.Combine(temporary.Root, "new.jar");
        await File.WriteAllBytesAsync(oldSource, oldBytes);
        await File.WriteAllBytesAsync(newSource, newBytes);
        await initial.ImportAsync(instance.Id, ModrinthContentKind.Mod, oldSource);
        var item = (await initial.ListAsync(instance.Id, ModrinthContentKind.Mod)).Single();
        var mods = Path.Combine(root, "minecraft", "mods");
        var target = Path.Combine(mods, "new.jar");

        FileStream? targetLock = null;
        var registryFailure = new IOException("Injected registry commit failure.");
        var content = new InstanceContent(store, null, () =>
        {
            targetLock = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
            throw registryFailure;
        });
        IOException failure;
        try
        {
            failure = await Assert.ThrowsExactlyAsync<IOException>(() => content.ReplaceAsync(instance.Id, item,
                ModrinthContentKind.Mod, newSource, "new.jar", Hash(oldBytes), Origin("new-version", newBytes)));
        }
        finally
        {
            targetLock?.Dispose();
        }

        Assert.IsInstanceOfType<AggregateException>(failure.InnerException);
        var rollbackErrors = (AggregateException)failure.InnerException!;
        Assert.AreSame(registryFailure, rollbackErrors.InnerExceptions[0]);
        Assert.AreEqual(2, rollbackErrors.InnerExceptions.Count);
        StringAssert.Contains(failure.Message, registryFailure.Message);
        CollectionAssert.AreEqual(oldBytes, await File.ReadAllBytesAsync(Path.Combine(mods, "old.jar")));
        CollectionAssert.AreEqual(newBytes, await File.ReadAllBytesAsync(target));
        Assert.AreEqual(0, Directory.GetFiles(mods, "*.hako-backup-*").Length);
        var items = await initial.ListAsync(instance.Id, ModrinthContentKind.Mod);
        Assert.AreEqual(2, items.Count);
        CollectionAssert.AreEquivalent(new[] { "old.jar", "new.jar" }, items.Select(item => item.Name).ToArray());
    }

    [TestMethod]
    public async Task ReplaceRollbackRetainsBackupWhenSameNamedTargetIsLocked()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root) = await CreateReadyInstanceAsync(temporary);
        var initial = new InstanceContent(store);
        var oldBytes = Encoding.UTF8.GetBytes("old mod");
        var newBytes = Encoding.UTF8.GetBytes("new mod");
        var oldSource = Path.Combine(temporary.Root, "same.jar");
        var newSource = Path.Combine(temporary.Root, "replacement.jar");
        await File.WriteAllBytesAsync(oldSource, oldBytes);
        await File.WriteAllBytesAsync(newSource, newBytes);
        await initial.ImportAsync(instance.Id, ModrinthContentKind.Mod, oldSource);
        var item = (await initial.ListAsync(instance.Id, ModrinthContentKind.Mod)).Single();
        var mods = Path.Combine(root, "minecraft", "mods");
        var target = Path.Combine(mods, "same.jar");

        FileStream? targetLock = null;
        var registryFailure = new IOException("Injected registry commit failure.");
        var content = new InstanceContent(store, null, () =>
        {
            targetLock = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
            throw registryFailure;
        });
        IOException failure;
        try
        {
            failure = await Assert.ThrowsExactlyAsync<IOException>(() => content.ReplaceAsync(instance.Id, item,
                ModrinthContentKind.Mod, newSource, "same.jar", Hash(oldBytes), Origin("new-version", newBytes)));
        }
        finally
        {
            targetLock?.Dispose();
        }

        Assert.IsInstanceOfType<AggregateException>(failure.InnerException);
        var rollbackErrors = (AggregateException)failure.InnerException!;
        Assert.AreSame(registryFailure, rollbackErrors.InnerExceptions[0]);
        Assert.AreEqual(3, rollbackErrors.InnerExceptions.Count);
        StringAssert.Contains(failure.Message, registryFailure.Message);
        var backup = Directory.GetFiles(mods, "same.jar.hako-backup-*").Single();
        StringAssert.Contains(failure.Message, backup);
        CollectionAssert.AreEqual(newBytes, await File.ReadAllBytesAsync(target));
        CollectionAssert.AreEqual(oldBytes, await File.ReadAllBytesAsync(backup));
    }

    [TestMethod]
    public async Task FailedRecycleLeavesFileAndRegistryWhileSuccessfulRecycleRemovesOnlyOrigin()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root) = await CreateReadyInstanceAsync(temporary);
        var file = Path.Combine(root, "minecraft", "mods", "owned.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var bytes = Encoding.UTF8.GetBytes("owned mod");
        await File.WriteAllBytesAsync(file, bytes);
        InstanceContent.WriteRegistry(root, new ModRegistry
        {
            ApiProvisioned = true,
            Files = new Dictionary<string, ModOrigin>(StringComparer.Ordinal) { ["owned.jar"] = Origin("owned-version", bytes) }
        });
        var item = (await new InstanceContent(store).ListAsync(instance.Id, ModrinthContentKind.Mod)).Single();

        var failedContent = new InstanceContent(store, new RecycleBin(new FakeRecycleOperation(RecycleBinItemStatus.Failed)));
        Assert.AreEqual(RecycleBinItemStatus.Failed, await failedContent.TrashAsync(instance.Id, item, ModrinthContentKind.Mod));
        Assert.IsTrue(File.Exists(file));
        Assert.IsTrue(InstanceContent.ReadRegistry(root).Files.ContainsKey("owned.jar"));

        var successfulContent = new InstanceContent(store, new RecycleBin(new FakeRecycleOperation(RecycleBinItemStatus.Recycled, delete: true)));
        Assert.AreEqual(RecycleBinItemStatus.Recycled, await successfulContent.TrashAsync(instance.Id, item, ModrinthContentKind.Mod));
        Assert.IsFalse(File.Exists(file));
        var registry = InstanceContent.ReadRegistry(root);
        Assert.IsTrue(registry.ApiProvisioned);
        Assert.IsFalse(registry.Files.ContainsKey("owned.jar"));
    }

    [TestMethod]
    public async Task CorruptRegistryIsNeverOverwrittenAndLeaseBlocksMutation()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root) = await CreateReadyInstanceAsync(temporary);
        var registryPath = Path.Combine(root, "minecraft", ".hako-resourcepacks.json");
        Directory.CreateDirectory(Path.GetDirectoryName(registryPath)!);
        await File.WriteAllTextAsync(registryPath, "{broken");
        var source = Path.Combine(temporary.Root, "local.zip");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        var content = new InstanceContent(store);
        JsonException? registryError = null;
        try { await content.ImportAsync(instance.Id, ModrinthContentKind.ResourcePack, source); }
        catch (JsonException exception) { registryError = exception; }
        Assert.IsNotNull(registryError);
        Assert.AreEqual("{broken", await File.ReadAllTextAsync(registryPath));
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "minecraft", "resourcepacks")));

        File.Delete(registryPath);
        Assert.IsTrue(store.TryBeginContent(instance.Id, out var lease, out _));
        try
        {
            var blocked = await Assert.ThrowsExactlyAsync<InstanceStorageException>(() => content.ImportAsync(
                instance.Id, ModrinthContentKind.ResourcePack, source));
            Assert.AreEqual(InstanceStorageError.BuildFilesBusy, blocked.Error);
            Assert.IsFalse(File.Exists(Path.Combine(root, "minecraft", "resourcepacks", "local.zip")));
        }
        finally
        {
            lease!.Dispose();
        }
    }

    [TestMethod]
    public async Task PackFolderImportRejectsNestedJunctionInsteadOfFollowingIt()
    {
        await using var temporary = new TemporaryDirectory();
        var (store, instance, root) = await CreateReadyInstanceAsync(temporary);
        var source = Path.Combine(temporary.Root, "LinkedPack");
        var outside = Path.Combine(temporary.Root, "Outside");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(source, "pack.mcmeta"), "{\"pack\":{}}");
        await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "outside");
        var junction = Path.Combine(source, "linked");
        CreateJunction(junction, outside);
        try
        {
            await Assert.ThrowsExactlyAsync<IOException>(() => new InstanceContent(store).ImportAsync(
                instance.Id, ModrinthContentKind.ResourcePack, source));
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "minecraft", "resourcepacks", "LinkedPack")));
            Assert.AreEqual("outside", await File.ReadAllTextAsync(Path.Combine(outside, "secret.txt")));
        }
        finally
        {
            Directory.Delete(junction);
        }
    }

    private static async Task<(InstanceStore Store, GameInstance Instance, string Root)> CreateReadyInstanceAsync(
        TemporaryDirectory temporary, string name = "Build", bool legacyTexturepacks = false)
    {
        var store = temporary.CreateStore();
        if (!store.IsLoaded) await store.LoadAsync();
        var instance = store.Create(new InstanceDraft { Name = name }, "1.20.4", legacyTexturepacks: legacyTexturepacks);
        var registryPath = Path.Combine(temporary.Paths.GameRoot, "instances.json");
        var registry = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(registryPath), HakoJsonContext.Default.InstanceRegistryFile)!;
        var ready = registry with
        {
            Instances = registry.Instances.Select(candidate => candidate.Id == instance.Id
                ? candidate with { State = InstanceInstallationState.Ready }
                : candidate).ToList()
        };
        await File.WriteAllBytesAsync(registryPath, JsonSerializer.SerializeToUtf8Bytes(ready, HakoJsonContext.Default.InstanceRegistryFile));
        await store.LoadAsync();
        return (store, store.List().Single(candidate => candidate.Id == instance.Id), store.GetInstanceDirectory(instance.Id));
    }

    private static ModOrigin Origin(string versionId, byte[] bytes) => new()
    {
        Source = ModSource.Modrinth,
        ProjectID = "project123",
        VersionID = versionId,
        PageURL = new Uri("https://modrinth.com/mod/project123"),
        Sha512 = Hash(bytes),
        VersionNumber = versionId
    };

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA512.HashData(bytes)).ToLowerInvariant();

    private static void CreateJunction(string junction, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "/c", "mklink", "/J", junction, target }
        });
        process!.WaitForExit();
        if (process.ExitCode != 0) Assert.Inconclusive("Could not create a junction for the isolated reparse-point test.");
    }

    private sealed class FakeRecycleOperation(RecycleBinItemStatus status, bool delete = false) : IRecycleBinOperation
    {
        public Task<RecycleBinItemStatus> DeleteAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (delete && status == RecycleBinItemStatus.Recycled)
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(path, recursive: true);
                else File.Delete(path);
            }

            return Task.FromResult(status);
        }
    }
}
