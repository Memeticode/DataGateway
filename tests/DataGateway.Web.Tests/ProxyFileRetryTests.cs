extern alias RegistrationProxy;
using RegistrationProxy::DataGateway.Proxy;
using System.Diagnostics;
using System.Reflection;
using Xunit;

namespace DataGateway.Web.Tests;

public sealed class ProxyFileRetryTests
{
    [Fact]
    public void T1_move_replaces_target_after_lock_released()
    {
        using var f = new Fixture();
        var source = f.Seed("temp", [4, 5, 6]);
        var target = f.Seed("target", [1, 2, 3]);
        f.AfterUnlock(target, files => files.MoveFile(source, target, overwrite: true));
        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(target));
        Assert.False(File.Exists(source));
        Assert.NotEmpty(f.Sleeps);
        Assert.All(f.Sleeps, pause => Assert.Equal(50, pause));
    }

    [Fact]
    public void T2_copy_creates_backup_after_source_lock_released()
    {
        using var f = new Fixture();
        var source = f.Seed("source", [7, 8, 9]);
        var target = Path.Combine(f.Root, "backup");
        f.AfterUnlock(source, files => files.CopyFile(source, target));
        Assert.Equal(new byte[] { 7, 8, 9 }, File.ReadAllBytes(target));
        Assert.Equal(new byte[] { 7, 8, 9 }, File.ReadAllBytes(source));
        Assert.NotEmpty(f.Sleeps);
    }

    [Fact]
    public void T2_delete_succeeds_after_lock_released()
    {
        using var f = new Fixture();
        var target = f.Seed("target", [1, 2, 3]);
        f.AfterUnlock(target, files => files.DeleteFile(target));
        Assert.False(File.Exists(target));
        Assert.NotEmpty(f.Sleeps);
    }

    [Fact]
    public void ReadAllBytes_succeeds_after_lock_released()
    {
        using var f = new Fixture();
        var target = f.Seed("target", [10, 11, 12]);
        byte[] actual = [];
        f.AfterUnlock(target, files => actual = files.ReadAllBytes(target));
        Assert.Equal(new byte[] { 10, 11, 12 }, actual);
        Assert.NotEmpty(f.Sleeps);
    }

    [Fact]
    public void WriteNewFile_retries_access_denied_then_creates_file()
    {
        using var f = new Fixture();
        var target = Path.Combine(f.Root, "temp");
        Directory.CreateDirectory(target);
        var files = new RegistrationFiles(4, 7, pause =>
        {
            f.Sleeps.Add(pause);
            Directory.Delete(target);
        });
        files.WriteNewFile(target, [13, 14, 15]);
        Assert.Equal(new[] { 7 }, f.Sleeps);
        Assert.Equal(new byte[] { 13, 14, 15 }, File.ReadAllBytes(target));
    }

    [Fact]
    public void T3_held_target_exhausts_exact_attempt_count_without_changes()
    {
        using var f = new Fixture();
        var source = f.Seed("temp", [4, 5, 6]);
        var target = f.Seed("target", [1, 2, 3]);
        using (var held = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var baseline = Record.Exception(() => File.Move(source, target, overwrite: true));
            Assert.NotNull(baseline);
            var files = new RegistrationFiles(4, 7, f.Sleeps.Add);
            var actual = Record.Exception(() => files.MoveFile(source, target, overwrite: true));
            Assert.NotNull(actual);
            Assert.Equal(baseline.GetType(), actual.GetType());
            Assert.Equal(baseline.HResult, actual.HResult);
            Assert.Equal(new[] { 7, 7, 7 }, f.Sleeps);
        }
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(target));
        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(source));
    }

    [Fact]
    public void T4_copy_existing_destination_is_not_retried()
    {
        using var f = new Fixture();
        var source = f.Seed("source", [4, 5, 6]);
        var target = f.Seed("backup", [1, 2, 3]);
        var files = new RegistrationFiles(4, 7, f.Sleeps.Add);
        var error = Assert.Throws<IOException>(() => files.CopyFile(source, target));
        Assert.Contains(error.HResult, new[] { unchecked((int)0x80070050), unchecked((int)0x800700B7) });
        Assert.Empty(f.Sleeps);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(target));
    }

    [Fact]
    public void T4_missing_move_source_is_not_retried()
    {
        using var f = new Fixture();
        var target = f.Seed("target", [1, 2, 3]);
        var files = new RegistrationFiles(4, 7, f.Sleeps.Add);
        Assert.Throws<FileNotFoundException>(() => files.MoveFile(Path.Combine(f.Root, "missing"), target, overwrite: true));
        Assert.Empty(f.Sleeps);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(target));
    }

    [Fact]
    public void T4_missing_read_path_is_not_retried()
    {
        using var f = new Fixture();
        var files = new RegistrationFiles(4, 7, f.Sleeps.Add);
        Assert.Throws<FileNotFoundException>(() => files.ReadAllBytes(Path.Combine(f.Root, "missing")));
        Assert.Empty(f.Sleeps);
    }

    [Fact]
    public void WriteNewFile_existing_file_is_not_owned_or_retried()
    {
        using var f = new Fixture();
        var target = f.Seed("temp", [1, 2, 3]);
        var files = new RegistrationFiles(4, 7, f.Sleeps.Add);
        Assert.Throws<IOException>(() => files.WriteNewFile(target, [4, 5, 6]));
        Assert.Empty(f.Sleeps);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(target));
    }

    [Fact]
    public void T5_default_held_target_waits_for_bounded_retry_window()
    {
        using var f = new Fixture();
        var source = f.Seed("temp", [4, 5, 6]);
        var target = f.Seed("target", [1, 2, 3]);
        using var held = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None);
        var files = new RegistrationFiles();
        var clock = Stopwatch.StartNew();
        var error = Record.Exception(() => files.MoveFile(source, target, overwrite: true));
        clock.Stop();
        Assert.NotNull(error);
        Assert.InRange(clock.Elapsed, TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(5));
        Assert.Equal(10, typeof(RegistrationFiles).GetField("attempts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(files));
        Assert.Equal(50, typeof(RegistrationFiles).GetField("pauseMilliseconds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(files));
    }

    [Theory]
    [InlineData("access", true)]
    [InlineData("sharing", true)]
    [InlineData("lock", true)]
    [InlineData("exists50", false)]
    [InlineData("existsB7", false)]
    [InlineData("missing", false)]
    [InlineData("directory", false)]
    [InlineData("cleanup", false)]
    [InlineData("io", false)]
    [InlineData("other", false)]
    public void Retry_filter_preserves_exception_instance(string kind, bool transient)
    {
        Exception expected = kind switch
        {
            "access" => new UnauthorizedAccessException(),
            "sharing" => new IOException("sharing", unchecked((int)0x80070020)),
            "lock" => new IOException("lock", unchecked((int)0x80070021)),
            "exists50" => new IOException("exists", unchecked((int)0x80070050)),
            "existsB7" => new IOException("exists", unchecked((int)0x800700B7)),
            "missing" => new FileNotFoundException(),
            "directory" => new DirectoryNotFoundException(),
            "cleanup" => new RegistrationFiles.TemporaryCleanupException(),
            "io" => new IOException("permanent"),
            _ => new InvalidOperationException()
        };
        var calls = 0;
        var sleeps = new List<int>();
        var files = new RegistrationFiles(4, 7, sleeps.Add);
        Action operation = () =>
        {
            calls++;
            throw expected;
        };
        var retry = typeof(RegistrationFiles).GetMethod("Retry", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var wrapper = Assert.Throws<TargetInvocationException>(() => retry.Invoke(files, [operation]));
        Assert.Same(expected, wrapper.InnerException);
        Assert.Equal(transient ? 4 : 1, calls);
        Assert.Equal(transient ? new[] { 7, 7, 7 } : Array.Empty<int>(), sleeps);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "dg-file-retry-" + Guid.NewGuid().ToString("N"));
        internal List<int> Sleeps { get; } = [];

        internal Fixture()
        {
            Directory.CreateDirectory(Root);
        }

        internal string Seed(string name, byte[] bytes)
        {
            var path = Path.Combine(Root, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        internal void AfterUnlock(string path, Action<RegistrationFiles> operation)
        {
            using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            using var firstSleep = new ManualResetEventSlim();
            var release = new Thread(() =>
            {
                firstSleep.Wait();
                Thread.Sleep(150);
                held.Dispose();
            });
            release.IsBackground = true;
            release.Start();
            var files = new RegistrationFiles(20, 50, pause =>
            {
                Sleeps.Add(pause);
                firstSleep.Set();
                Thread.Sleep(pause);
            });
            try
            {
                operation(files);
            }
            finally
            {
                firstSleep.Set();
                Assert.True(release.Join(TimeSpan.FromSeconds(5)), "File-lock release thread did not stop");
            }
        }

        public void Dispose()
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
