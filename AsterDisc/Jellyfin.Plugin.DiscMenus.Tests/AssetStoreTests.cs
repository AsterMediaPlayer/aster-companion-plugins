using Xunit;

namespace Jellyfin.Plugin.DiscMenus.Tests;

/// <summary>Uploads are the one place an admin hands the plugin arbitrary bytes that are later served to anyone, so the rules are tested hard.</summary>
public sealed class AssetStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dm-assets-" + Guid.NewGuid().ToString("N"));
    private readonly AssetStore _store;
    private const string Folder = "3f2b8c1e-6d4a-4e2b-9a71-0c5d2e8f1a44";

    public AssetStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new AssetStore(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly Dictionary<string, byte[]> Good = new()
    {
        [".png"] = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 },
        [".jpg"] = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 },
        [".jpeg"] = new byte[] { 0xFF, 0xD8, 0xFF, 0xE1, 1, 2, 3 },
        [".webp"] = "RIFF\0\0\0\0WEBPVP8 "u8.ToArray(),
        [".wav"] = "RIFF\0\0\0\0WAVEfmt "u8.ToArray(),
        [".ogg"] = new byte[] { 0x4F, 0x67, 0x67, 0x53, 0, 2, 1, 2 },
        [".opus"] = new byte[] { 0x4F, 0x67, 0x67, 0x53, 0, 2, 1, 2 },
        [".m4a"] = new byte[] { 0, 0, 0, 0x20, 0x66, 0x74, 0x79, 0x70, 0x4D, 0x34, 0x41, 0x20 },
        [".mp3"] = new byte[] { 0x49, 0x44, 0x33, 3, 0, 0, 0, 0, 0, 0 },
    };

    private AssetResult Put(string name, byte[] bytes, string folder = Folder) => _store.SaveAsync(folder, name, new AsyncOnlyStream(bytes)).GetAwaiter().GetResult();

    [Theory]
    [InlineData(".png")]
    [InlineData(".jpg")]
    [InlineData(".jpeg")]
    [InlineData(".webp")]
    [InlineData(".wav")]
    [InlineData(".ogg")]
    [InlineData(".opus")]
    [InlineData(".m4a")]
    [InlineData(".mp3")]
    public void EachAllowedTypeIsStoredAndListed(string ext)
    {
        var result = Put("picture" + ext, Good[ext]);
        Assert.Equal(AssetResultKind.Ok, result.Kind);
        Assert.Equal($"asset:{Folder}/picture{ext}", result.File!.Ref);
        Assert.Equal(Good[ext], File.ReadAllBytes(Path.Combine(_dir, Folder, "picture" + ext)));
        Assert.Contains(_store.List(Folder), f => f.Name == "picture" + ext && f.Kind == (ext is ".png" or ".jpg" or ".jpeg" or ".webp" ? "image" : "audio"));
    }

    [Fact]
    public void AnMp3WithoutATagStartsWithAFrameHeader()
    {
        Assert.Equal(AssetResultKind.Ok, Put("a.mp3", new byte[] { 0xFF, 0xFB, 0x90, 0x00, 1 }).Kind);
        Assert.Equal(AssetResultKind.BadType, Put("b.mp3", new byte[] { 0xFF, 0x00, 0x90, 0x00 }).Kind);
    }

    [Theory]
    [InlineData("evil.svg", "<svg xmlns='http://www.w3.org/2000/svg' onload='alert(1)'/>")]
    [InlineData("page.html", "<script>alert(1)</script>")]
    [InlineData("x.js", "alert(1)")]
    [InlineData("x.exe", "MZ")]
    [InlineData("x.gif", "GIF89a")]
    [InlineData("noext", "hello")]
    [InlineData("x.png.html", "<html>")]
    public void OtherTypesAreRefused(string name, string content)
    {
        var result = Put(name, System.Text.Encoding.UTF8.GetBytes(content));
        Assert.NotEqual(AssetResultKind.Ok, result.Kind);
        Assert.False(Directory.Exists(Path.Combine(_dir, Folder)) && Directory.GetFiles(Path.Combine(_dir, Folder)).Length > 0);
    }

    [Theory]
    [InlineData("a.png", ".jpg")]
    [InlineData("a.jpg", ".png")]
    [InlineData("a.mp3", ".png")]
    [InlineData("a.png", ".mp3")]
    public void TheContentsMustBeWhatTheNameSays(string name, string contentOf)
    {
        Assert.Equal(AssetResultKind.BadType, Put(name, Good[contentOf]).Kind);
    }

    [Fact]
    public void HtmlDressedAsAPictureIsRefused()
    {
        Assert.Equal(AssetResultKind.BadType, Put("photo.png", "<html><script>alert(1)</script></html>"u8.ToArray()).Kind);
        Assert.Equal(AssetResultKind.BadType, Put("empty.png", Array.Empty<byte>()).Kind);
    }

    [Theory]
    [InlineData("../../etc/passwd.png")]
    [InlineData("..\\..\\x.png")]
    [InlineData("/abs/path/pic.png")]
    [InlineData("sub/dir/pic.png")]
    public void PathsInANameAreReducedToTheFileName(string name)
    {
        var result = Put(name, Good[".png"]);
        Assert.Equal(AssetResultKind.Ok, result.Kind);
        Assert.Matches("^(passwd|x|pic)\\.png$", result.File!.Name);
        Assert.Equal(Path.GetFullPath(Path.Combine(_dir, Folder)), Path.GetDirectoryName(Path.GetFullPath(Path.Combine(_dir, Folder, result.File.Name))));
        Assert.False(File.Exists(Path.Combine(_dir, "x.png")));
    }

    [Theory]
    [InlineData("My Holiday Photo (1).PNG", "My-Holiday-Photo-1.png")]
    [InlineData("héllo wörld.jpg", "h-llo-w-rld.jpg")]
    [InlineData("a   b.webp", "a-b.webp")]
    [InlineData("UPPER.Mp3", "UPPER.mp3")]
    public void NamesAreReducedToWhatAReferenceAllows(string original, string expected) => Assert.Equal(expected, AssetStore.CleanName(original));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".png")]
    [InlineData("....png")]
    [InlineData("---.png")]
    [InlineData("noextension")]
    [InlineData("x.")]
    [InlineData(null)]
    public void NamesWithNothingUsableAreRefused(string? original) => Assert.Null(AssetStore.CleanName(original));

    [Fact]
    public void ALongNameIsCutToTheLimitAndKeepsItsExtension()
    {
        var name = AssetStore.CleanName(new string('a', 300) + ".png")!;
        Assert.True(name.Length <= 64);
        Assert.EndsWith(".png", name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("-x")]
    [InlineData(null)]
    public void BadFoldersAreRefusedEverywhere(string? folder)
    {
        Assert.Equal(AssetResultKind.BadFolder, Put("a.png", Good[".png"], folder!).Kind);
        Assert.Equal(AssetResultKind.BadFolder, _store.Delete(folder, "a.png").Kind);
        Assert.Empty(_store.List(folder));
    }

    [Fact]
    public void AnExistingFileIsNeverReplaced()
    {
        var first = Put("pic.png", Good[".png"]);
        var changed = Good[".png"].Concat(new byte[] { 9, 9, 9 }).ToArray();
        var second = Put("pic.png", changed);
        var third = Put("pic.png", changed);
        Assert.Equal("pic.png", first.File!.Name);
        Assert.Equal("pic-2.png", second.File!.Name);
        Assert.Equal("pic-3.png", third.File!.Name);
        Assert.Equal(Good[".png"], File.ReadAllBytes(Path.Combine(_dir, Folder, "pic.png")));
    }

    [Fact]
    public void AnImageOverFiveMegabytesIsRefused()
    {
        var big = Good[".png"].Concat(new byte[(int)AssetStore.MaxImageBytes]).ToArray();
        Assert.Equal(AssetResultKind.TooLarge, Put("big.png", big).Kind);
        Assert.Empty(_store.List(Folder));
    }

    [Fact]
    public void AnImageJustUnderTheLimitIsAccepted()
    {
        var ok = Good[".png"].Concat(new byte[(int)AssetStore.MaxImageBytes - Good[".png"].Length]).ToArray();
        Assert.Equal(AssetResultKind.Ok, Put("ok.png", ok).Kind);
    }

    [Fact]
    public void AnEndlessStreamStopsAtTheLimit()
    {
        var result = _store.SaveAsync(Folder, "forever.png", new EndlessStream(Good[".png"])).GetAwaiter().GetResult();
        Assert.Equal(AssetResultKind.TooLarge, result.Kind);
    }

    [Fact]
    public void AudioHasAHigherLimitThanImages()
    {
        var bytes = Good[".wav"].Concat(new byte[(int)AssetStore.MaxImageBytes + 10]).ToArray();
        Assert.Equal(AssetResultKind.Ok, Put("long.wav", bytes).Kind);
        var tooBig = Good[".wav"].Concat(new byte[(int)AssetStore.MaxAudioBytes]).ToArray();
        Assert.Equal(AssetResultKind.TooLarge, Put("huge.wav", tooBig).Kind);
    }

    [Fact]
    public void AFolderHoldsAtMostTwoHundredFiles()
    {
        Directory.CreateDirectory(Path.Combine(_dir, Folder));
        for (var i = 0; i < AssetStore.MaxFilesPerFolder; i++)
        {
            File.WriteAllBytes(Path.Combine(_dir, Folder, $"f{i}.png"), Good[".png"]);
        }

        Assert.Equal(AssetResultKind.QuotaExceeded, Put("one-more.png", Good[".png"]).Kind);
    }

    [Fact]
    public void AFolderHoldsAtMostOneHundredMegabytes()
    {
        Directory.CreateDirectory(Path.Combine(_dir, Folder));
        var chunk = new byte[20 * 1024 * 1024];
        for (var i = 0; i < 5; i++)
        {
            File.WriteAllBytes(Path.Combine(_dir, Folder, $"f{i}.wav"), chunk);
        }

        Assert.Equal(AssetResultKind.QuotaExceeded, Put("more.png", Good[".png"]).Kind);
    }

    [Fact]
    public void ListingShowsOnlyAllowedFilesAndOtherFoldersAreSeparate()
    {
        Put("a.png", Good[".png"]);
        Put("b.mp3", Good[".mp3"]);
        Put("c.png", Good[".png"], "other-menu");
        File.WriteAllText(Path.Combine(_dir, Folder, "notes.txt"), "x");
        File.WriteAllText(Path.Combine(_dir, Folder, "bad.svg"), "<svg/>");
        var names = _store.List(Folder).Select(f => f.Name).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "a.png", "b.mp3" }, names);
        Assert.Single(_store.List("other-menu"));
        Assert.Empty(_store.List("never-used"));
    }

    [Fact]
    public void DeleteRemovesOnlyThatFile()
    {
        Put("a.png", Good[".png"]);
        Put("b.png", Good[".png"]);
        Assert.Equal(AssetResultKind.Ok, _store.Delete(Folder, "a.png").Kind);
        Assert.Equal(new[] { "b.png" }, _store.List(Folder).Select(f => f.Name));
        Assert.Equal(AssetResultKind.NotFound, _store.Delete(Folder, "a.png").Kind);
    }

    [Theory]
    [InlineData("../a.png")]
    [InlineData("notes.txt")]
    [InlineData("a/b.png")]
    [InlineData(null)]
    public void DeleteRefusesNamesThatAreNotManagedFiles(string? name)
    {
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "keep");
        Directory.CreateDirectory(Path.Combine(_dir, Folder));
        File.WriteAllText(Path.Combine(_dir, Folder, "notes.txt"), "keep");
        Assert.NotEqual(AssetResultKind.Ok, _store.Delete(Folder, name).Kind);
        Assert.True(File.Exists(Path.Combine(_dir, Folder, "notes.txt")));
        Assert.True(File.Exists(Path.Combine(_dir, "notes.txt")));
    }

    [Fact]
    public void ASymbolicLinkIsNeitherListedNorDeleted()
    {
        Directory.CreateDirectory(Path.Combine(_dir, Folder));
        var secret = Path.Combine(_dir, "secret.png");
        File.WriteAllBytes(secret, Good[".png"]);
        try
        {
            File.CreateSymbolicLink(Path.Combine(_dir, Folder, "link.png"), secret);
        }
        catch (Exception)
        {
            return; // links can't be made on this filesystem: nothing to test
        }

        Assert.Empty(_store.List(Folder));
        Assert.Equal(AssetResultKind.NotFound, _store.Delete(Folder, "link.png").Kind);
        Assert.True(File.Exists(secret));
    }

    /// <summary>Like the web server's request body: a synchronous Read throws, only ReadAsync works.</summary>
    private sealed class AsyncOnlyStream : Stream
    {
        private readonly MemoryStream _inner;

        public AsyncOnlyStream(byte[] bytes) => _inner = new MemoryStream(bytes);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous operations are disallowed.");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class EndlessStream : Stream
    {
        private readonly byte[] _head;
        private long _position;

        public EndlessStream(byte[] head) => _head = head;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous operations are disallowed.");

        public override ValueTask<int> ReadAsync(Memory<byte> memory, CancellationToken cancellationToken = default)
        {
            var buffer = memory.Span;
            var count = buffer.Length;
            for (var i = 0; i < count; i++)
            {
                buffer[i] = _position < _head.Length ? _head[_position] : (byte)0;
                _position++;
            }

            return new ValueTask<int>(count);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
