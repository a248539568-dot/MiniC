using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using MiniC.ViewModels;

namespace MiniC.Services;

/// <summary>读取 Windows Shell 文件剪贴板及其复制或剪切效果。</summary>
public sealed class ClipboardItemStateService
{
    private const string PreferredDropEffectFormat = "Preferred DropEffect";
    private const uint DropEffectCopy = 1;
    private const uint DropEffectMove = 2;

    public static ClipboardItemSnapshot ReadSnapshot()
    {
        try
        {
            var data = System.Windows.Clipboard.GetDataObject();
            if (data is null || !data.GetDataPresent(System.Windows.DataFormats.FileDrop, autoConvert: true))
                return ClipboardItemSnapshot.Empty;

            var paths = (data.GetData(System.Windows.DataFormats.FileDrop, autoConvert: true) as string[] ?? [])
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (paths.Length == 0) return ClipboardItemSnapshot.Empty;

            var effect = ReadDropEffect(data.GetData(PreferredDropEffectFormat, autoConvert: false));
            var state = (effect & DropEffectMove) != 0
                ? ClipboardTransferState.Cut
                : ClipboardTransferState.Copied;
            return new ClipboardItemSnapshot(paths, state);
        }
        catch (ExternalException)
        {
            return ClipboardItemSnapshot.Empty;
        }
    }

    internal static uint ReadDropEffect(object? value)
    {
        if (value is byte[] bytes && bytes.Length >= sizeof(uint)) return BitConverter.ToUInt32(bytes, 0);
        if (value is not MemoryStream stream) return DropEffectCopy;
        var position = stream.CanSeek ? stream.Position : 0;
        try
        {
            if (stream.CanSeek) stream.Position = 0;
            Span<byte> buffer = stackalloc byte[sizeof(uint)];
            return stream.Read(buffer) == buffer.Length ? BitConverter.ToUInt32(buffer) : DropEffectCopy;
        }
        finally
        {
            if (stream.CanSeek) stream.Position = position;
        }
    }

    internal static bool ValidateForSmokeTest()
    {
        using var move = new MemoryStream(BitConverter.GetBytes(DropEffectMove));
        using var copy = new MemoryStream(BitConverter.GetBytes(DropEffectCopy));
        var item = new DesktopItem { Path = @"C:\Temp\clipboard-test.txt", Name = "clipboard-test.txt" };
        item.ClipboardTransferState = ClipboardTransferState.Copied;
        var copied = item.IsCopiedToClipboard && !item.IsCutToClipboard;
        item.ClipboardTransferState = ClipboardTransferState.Cut;
        return copied && item.IsCutToClipboard && !item.IsCopiedToClipboard
                      && ReadDropEffect(move) == DropEffectMove
                      && ReadDropEffect(copy) == DropEffectCopy;
    }
}

public sealed record ClipboardItemSnapshot(IReadOnlyList<string> Paths, ClipboardTransferState State)
{
    public static ClipboardItemSnapshot Empty { get; } = new([], ClipboardTransferState.None);
}
