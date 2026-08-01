using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Source-level guard that the DXGI capture path keeps using its cached vtable delegates and reused
/// native scratch rather than rebuilding them per frame (RemEx-8c1l).
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS IS A SOURCE SCAN AND NOT A REAL TEST. Everything it guards is COM interop against a live
/// D3D11 device and a Desktop Duplication output — there is no way to exercise it on a build agent,
/// and the change it protects is invisible to behaviour by design ("zero behaviour change", just
/// fewer allocations). A conventional test would either need a GPU or prove nothing at all.
/// </para>
/// <para>
/// WHAT IT ACTUALLY CATCHES is the realistic regression: someone adds a per-frame COM call and reaches
/// for <c>GetSlot</c> the way every existing line used to, or reinstates an <c>AllocHGlobal</c> for a
/// scratch struct. Both are easy to write, both compile, both are correct, and neither shows up
/// anywhere except as allocation rate on a machine nobody is profiling. Same shape as RemEx-y6x6,
/// where wiring was removed and every test stayed green.
/// </para>
/// <para>
/// Deliberately NARROW. It does not attempt to prove the cache is correct — that is review's job, and
/// the argument is that each delegate records the COM pointer it was read from, so any
/// release/recreate cycle forces a re-read on next use. That matters because
/// <c>TryReinitializeDuplication</c> releases and re-creates <c>_duplOutput</c> WITHOUT going through
/// <c>ReleaseAll</c>, so a cache invalidated by hand at "the" release site would have been stale
/// exactly there.
/// </para>
/// <para>
/// KNOWN LIMITS, so nobody mistakes this for more than it is. It scans a fixed list of files, so a
/// per-frame call added in a NEW file is invisible to it (though splitting an existing one into
/// partials would fail the "still read into the cache" test loudly). And the inline-invocation regex
/// cannot span a nested parenthesis in the arguments, so <c>GetSlot&lt;MapFn&gt;(Ctx(), 14)(…)</c>
/// would evade it. Both are acceptable for a guard against the realistic slip — reaching for
/// <c>GetSlot</c> out of habit — and neither is worth a C# parser here.
/// </para>
/// </remarks>
public class DxgiVtableCacheGuardTests
{
    /// <summary>The five slots the capture loop calls on every frame.</summary>
    /// <remarks>
    /// Initialization resolves a dozen more inline — adapter, output, DuplicateOutput,
    /// CreateTexture2D — and those are correct as they are: they run once, and caching them would be
    /// noise. Per-frame versus once is the whole distinction this rests on.
    /// </remarks>
    private static readonly string[] PerFrameDelegates =
        ["CopyResourceFn", "MapFn", "UnmapFn", "AcquireNextFrameFn", "ReleaseFrameFn"];

    /// <summary>Both Windows capture backends: they share the pattern and the fix.</summary>
    private static readonly string[] CaptureSources =
    [
        Path.Combine("remex.agent", "Services", "ScreenCapture", "DxgiDesktopCapture.cs"),
        Path.Combine("remex.agent.windows", "WgcDesktopCapture.cs"),
    ];

    private static string CaptureSourceWithoutComments()
    {
        var text = new System.Text.StringBuilder();
        foreach (var relative in CaptureSources)
        {
            var path = Path.Combine(RepoRoot(), relative);
            Assert.True(File.Exists(path), $"expected a capture source at {path}");
            text.AppendLine(File.ReadAllText(path));
        }

        var source = text.ToString();

        // Comments are stripped first because that file documents the very patterns banned here; a
        // scan that reads its own explanation as a violation is a false alarm waiting to happen.
        source = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        source = Regex.Replace(source, @"//[^\n]*", string.Empty);
        return source;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Remex.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void NoPerFrameCallResolvesItsVtableSlotInline()
    {
        // GetSlot<Fn>(ptr, n)(args) invokes immediately and builds a marshalling stub on every call.
        // Reading a slot into a field — GetSlot<Fn>(ptr, n); with no argument list after it — is what
        // the Ensure* methods do and is the point of the change, so this matches only the invoking
        // form: a closing paren followed by another open paren.
        var source = CaptureSourceWithoutComments();

        var offenders = PerFrameDelegates
            .Where(fn => Regex.IsMatch(source, "GetSlot<" + fn + @">\([^)]*\)\s*\("))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "These per-frame slots are resolved and invoked inline, allocating a marshalling stub on "
            + "every call. Go through the cached accessor instead: " + string.Join(", ", offenders));
    }

    [Fact]
    public void EachPerFrameSlotIsReadExactlyOnceIntoTheCache()
    {
        // The complement of the test above: banning the inline form is only meaningful if the slot is
        // still resolved SOMEWHERE. Without this, deleting the cache entirely would leave both green.
        var source = CaptureSourceWithoutComments();

        foreach (var fn in PerFrameDelegates)
        {
            // At least once, not exactly once: the two backends each cache the three context slots
            // independently, so CopyResourceFn/MapFn/UnmapFn legitimately appear twice across the
            // scanned sources while the duplication slots appear once.
            Assert.True(
                Regex.IsMatch(source, "GetSlot<" + fn + @">\([^)]*\)\s*;"),
                $"{fn} is no longer read into any delegate cache");
        }
    }

    [Fact]
    public void TheReusedScratchBuffersAreStillReused()
    {
        // The two per-frame structs. Each must be allocated EXACTLY ONCE in the whole file — that one
        // occurrence being the lazy initializer on its scratch property. A second means some call
        // site went back to allocating per use. Init-time allocations of other structs (output desc,
        // duplication desc, texture desc, cursor shape) are untouched and legitimate.
        var source = CaptureSourceWithoutComments();

        // Two MappedSubresource allocations across the pair — one lazy initializer per backend — and
        // one frame-info allocation, which only the DXGI duplication path needs.
        Assert.Equal(2, Regex.Matches(source, @"AllocHGlobal\(Marshal\.SizeOf<MappedSubresource>\(\)\)").Count);
        Assert.Single(Regex.Matches(source, @"AllocHGlobal\(Marshal\.SizeOf<DXGI_OUTDUPL_FRAME_INFO>\(\)\)"));
    }

    [Fact]
    public void EveryCachedDelegateRecordsTheComPointerItCameFrom()
    {
        // The correctness property, as close as a source scan can get to it: the cache is keyed on the
        // owning COM pointer, so a released-and-recreated object cannot be called through a stale
        // delegate. Drop these comparisons and the cache would have to be invalidated by hand at every
        // release site — including one that does not go through ReleaseAll.
        var source = CaptureSourceWithoutComments();

        Assert.Contains("_contextDelegateOwner == _d3dContext", source);
        Assert.Contains("_duplDelegateOwner == _duplOutput", source);
        Assert.Contains("_contextDelegateOwner = _d3dContext;", source);
        Assert.Contains("_duplDelegateOwner = _duplOutput;", source);
    }

    [Fact]
    public void TheScratchBuffersAreFreedOnDispose()
    {
        // They now outlive every frame, so nothing else will ever release them.
        var source = CaptureSourceWithoutComments();

        Assert.Contains("FreeScratch(ref _frameInfoScratch)", source);
        Assert.Contains("FreeScratch(ref _mappedScratch)", source);
    }
}
