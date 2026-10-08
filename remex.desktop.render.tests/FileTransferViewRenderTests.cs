using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Desktop.Services.FileTransfer;
using Remex.Desktop.ViewModels;
using Remex.Desktop.Views;

namespace Remex.Desktop.Render.Tests;

/// <summary>
/// The File Transfer screen of the 2026-10-08 redesign, laid out and drawn headlessly against a scripted host: the
/// tree lists This PC's shared folders, the list shows a folder, and the preview follows the selection with real
/// range reads. Set <c>REMEX_RENDER_SNAPSHOT_DIR</c> to also write PNGs of what was drawn, so the screen can be
/// LOOKED AT on a machine with no display (the ui-verify skill needs a live Windows desktop).
/// </summary>
public sealed class FileTransferViewRenderTests
{
    private const string Log =
        "2026-10-08 14:03:21.004 INFO  RemEx agent starting\n" +
        "2026-10-08 14:03:21.120 DEBUG Loading shared folders from file_transfer_roots.json\n" +
        "2026-10-08 14:03:22.517 WARN  Pairing PIN expires in 30 seconds\n" +
        "2026-10-08 14:03:24.900 ERROR Thumbnail failed for IMG_0142.HEIC: no decoder\n" +
        "fail: Remex.Agent.Handlers.FileTransferHandler[0] {\"rootId\": \"docs\", \"bytes\": 1048576, \"ok\": false}\n" +
        "2026-10-08 14:03:25.002 INFO  Phone connected: Pixel 9\n";

    /// <summary>A host that answers like the PC's own: two shared folders, one listing, range reads and details.</summary>
    private sealed class ScriptedHost : IFileTransferConnection
    {
        public event Action<RemexMessage>? FileTransferMessageReceived;

        public Task SendAsync(RemexMessage m)
        {
            RemexMessage? reply = m.Type switch
            {
                MessageTypes.FileRootsRequest => new RemexMessage
                {
                    Type = MessageTypes.FileRootsResponse,
                    FileRootsResponse = new FileRootsResponse
                    {
                        Roots =
                        [
                            new FileSharedRoot { RootId = "docs", DisplayName = "Documents", IsWritable = true, CanRename = true, CanMove = true, CanDelete = true },
                            new FileSharedRoot { RootId = "pics", DisplayName = "Pictures", IsWritable = false },
                        ],
                        FileCapabilities = new FileCapabilities
                        {
                            Protocol = 3, Binary = true, Resume = true, ReadRange = true, Hash = true,
                            Ops = ["delete", "rename", "copy", "move", "mkdir", "search", "manifest"],
                        },
                    },
                },
                MessageTypes.FileBrowseRequest => new RemexMessage
                {
                    Type = MessageTypes.FileBrowseResponse,
                    FileBrowseResponse = new FileBrowseResponse
                    {
                        RequestId = m.FileBrowseRequest!.RequestId,
                        Entries = (m.FileBrowseRequest.RelativePath ?? string.Empty).Trim('/').Length == 0
                            ?
                            [
                                new FileEntry { Name = "Logs", IsDirectory = true, ModifiedUnixMs = 1759900000000 },
                                new FileEntry { Name = "Taxes 2026", IsDirectory = true, ModifiedUnixMs = 1759800000000 },
                                new FileEntry { Name = "agent.log", IsDirectory = false, SizeBytes = Log.Length, ModifiedUnixMs = 1759931000000 },
                                new FileEntry { Name = "notes.md", IsDirectory = false, SizeBytes = 4096, ModifiedUnixMs = 1759700000000 },
                                new FileEntry { Name = "IMG_0142.jpg", IsDirectory = false, SizeBytes = 2_150_000, ModifiedUnixMs = 1759600000000 },
                                new FileEntry { Name = "installer.exe", IsDirectory = false, SizeBytes = 98_000_000, ModifiedUnixMs = 1759500000000 },
                            ]
                            : [new FileEntry { Name = "2025", IsDirectory = true, ModifiedUnixMs = 1759000000000 }],
                    },
                },
                MessageTypes.FileReadRangeRequest => ReadRange(m.FileReadRangeRequest!),
                MessageTypes.FileMetadataRequest => new RemexMessage
                {
                    Type = MessageTypes.FileMetadataResponse,
                    FileMetadataResponse = new FileMetadataResponse
                    {
                        RequestId = m.FileMetadataRequest!.RequestId, Size = Log.Length,
                        CreatedUtc = 1759900000000, ModifiedUtc = 1759931000000, MimeType = "text/plain",
                    },
                },
                MessageTypes.FileHashRequest => new RemexMessage
                {
                    Type = MessageTypes.FileHashResponse,
                    FileHashResponse = new FileHashResponse
                    {
                        RequestId = m.FileHashRequest!.RequestId,
                        Sha256Base64 = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Log))),
                    },
                },
                MessageTypes.FileThumbnailRequest => new RemexMessage
                {
                    Type = MessageTypes.FileThumbnailResponse,
                    FileThumbnailResponse = new FileThumbnailResponse { RequestId = m.FileThumbnailRequest!.RequestId },
                },
                _ => null,
            };
            if (reply is not null)
                Dispatcher.UIThread.Post(() => FileTransferMessageReceived?.Invoke(reply));
            return Task.CompletedTask;
        }

        private static RemexMessage ReadRange(FileReadRangeRequest r)
        {
            var bytes = Encoding.UTF8.GetBytes(Log);
            var start = r.FromEnd ? Math.Max(0, bytes.Length - r.Length) : r.Offset;
            var count = (int)Math.Clamp(bytes.Length - start, 0, r.Length);
            return new RemexMessage
            {
                Type = MessageTypes.FileReadRangeResponse,
                FileReadRangeResponse = new FileReadRangeResponse
                {
                    RequestId = r.RequestId, Offset = start, FileSize = bytes.Length, Eof = start + count >= bytes.Length,
                    DataBase64 = Convert.ToBase64String(bytes, (int)start, count),
                },
            };
        }
    }

    private static void Pump(int rounds = 40)
    {
        for (var i = 0; i < rounds; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(15);
        }
    }

    private static (Window Window, FileTransferView View, FileTransferViewModel Vm) Open(double width = 1400, double height = 860)
    {
        var vm = new FileTransferViewModel(
            new ConnectionViewModel { IsConnected = true },
            transferQueue: new FileTransferQueue(post: work => Dispatcher.UIThread.Post(work)),
            client: new FileTransferClient(new ScriptedHost()),
            phoneAccess: () => null,
            phoneTransfers: () => null);
        var view = new FileTransferView { DataContext = vm };
        var window = new Window { Width = width, Height = height, Content = view };
        window.Show();
        Pump();
        return (window, view, vm);
    }

    private static void Snapshot(Window window, string name)
    {
        var dir = Environment.GetEnvironmentVariable("REMEX_RENDER_SNAPSHOT_DIR");
        if (string.IsNullOrWhiteSpace(dir))
            return;
        Directory.CreateDirectory(dir);
        using var stream = File.Create(Path.Combine(dir, name + ".png"));
        window.CaptureRenderedFrame()?.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    [AvaloniaFact]
    public void TheTreeContentsAndPreviewPanesAllLayOut_AndTheTreeShowsThisPcsSharedFolders()
    {
        var (window, view, vm) = Open();

        var tree = view.FindControl<TreeView>("FolderTree")!;
        var list = view.FindControl<ListBox>("RemoteFileList")!;
        var preview = view.FindControl<DockPanel>("PreviewPane")!;
        tree.Bounds.Width.Should().BeGreaterThan(150, "the tree is a real pane, not a collapsed column");
        list.Bounds.Width.Should().BeGreaterThan(300);
        preview.Bounds.Width.Should().BeGreaterThan(200);

        vm.Tree.Devices.Should().ContainSingle(d => d.IsThisPc);
        var thisPc = vm.Tree.Devices.Single();
        thisPc.IsExpanded.Should().BeTrue("browsing a folder reveals it in the tree");
        thisPc.Children.Select(c => c.Name).Should().Equal("Documents", "Pictures");
        vm.RemoteEntries.Select(e => e.Name).Should().Contain("agent.log");

        Snapshot(window, "file-transfer-1-folder");
        window.Close();
    }

    [AvaloniaFact]
    public void SelectingALog_PreviewsItAsColouredText_WithItsDetailsAndFingerprint()
    {
        var (window, _, vm) = Open();

        vm.SelectedRemoteEntry = vm.RemoteEntries.Single(e => e.Name == "agent.log");
        Pump(60);

        vm.Preview.State.Should().Be(PreviewState.Text);
        vm.Preview.Lines.Should().HaveCount(Log.TrimEnd('\n').Split('\n').Length);
        vm.Preview.Lines.Should().Contain(l => l.Spans.Any(s => s.Kind == Services.FilePreview.SyntaxKind.LogError));
        vm.Preview.Metadata.Should().NotBeNull();
        vm.Preview.CanComputeHash.Should().BeTrue();

        vm.Preview.ComputeHashCommand.Execute(null);
        Pump(30);
        vm.Preview.HashHex.Should().HaveLength(64);

        Snapshot(window, "file-transfer-2-text-preview");
        window.Close();
    }

    [AvaloniaFact]
    public void TheTransferStrip_CountsVerifiedTransfers_AndOpensIntoTheList()
    {
        var (window, _, vm) = Open();

        var done = vm.TransferQueue.Enqueue(FileTransferQueueKind.Download, "IMG_0142.jpg", (progress, _) =>
        {
            progress.ReportVerified("ungWv48Bz+pBQUDeXa4iI7ADYaOWF3qctBD/YfIAFa0=");
            return Task.CompletedTask;
        });
        vm.TransferQueue.Enqueue(FileTransferQueueKind.Upload, "holiday-video.mp4", (progress, ct) =>
        {
            progress.Report(new TransferProgress(420_000_000, 1_000_000_000));
            return Task.Delay(Timeout.Infinite, ct);
        });
        Pump(40);
        vm.IsQueueExpanded = true;
        vm.SelectedRemoteEntry = vm.RemoteEntries.Single(e => e.Name == "agent.log");
        Pump(60);

        done.IsVerified.Should().BeTrue();
        vm.VerifiedTransferCount.Should().Be(1);
        vm.ActiveTransferCount.Should().Be(1);

        Snapshot(window, "file-transfer-3-transfers");
        vm.TransferQueue.CancelAll();
        Pump(10);
        window.Close();
    }
}
