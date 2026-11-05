using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Screenshot.App.Capture;

public partial class CaptureOverlayWindow
{
    private TaskCompletionSource? _residentEditorRendered;
    private IntPtr _residentPresentationCover;
    private IntPtr _residentPreviousOwner;

    private void ConfigureResidentPresentation()
    {
        if (_options?.ResidentSelectionWindow is not { } cover || cover == IntPtr.Zero) return;
        _residentEditorRendered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ContentRendered += OnResidentEditorContentRendered;
        Closing += OnResidentEditorClosing;
    }

    private async Task WaitForResidentEditorCompositionAsync(CancellationToken token)
    {
        CaptureTimingDiagnostics.Mark("editor-content-wait-start");
        await _residentEditorRendered!.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        CaptureTimingDiagnostics.Mark("editor-content-wait-end");
        _nativeSelectionMask.PrepareCoveredPresentation();
        CaptureTimingDiagnostics.Mark("editor-mask-ready");
        // ContentRendered confirms WPF submitted its visual tree. Wait for
        // the compositor without blocking input before releasing the cover.
        CaptureTimingDiagnostics.Mark("editor-composition-wait-start");
        await Task.Run(() => FlushResidentEditorComposition(), token).WaitAsync(TimeSpan.FromSeconds(2), token);
        CaptureTimingDiagnostics.Mark("editor-composition-wait-end");
        if (_isCompleted || _isDisposed) return;
        CommitResidentPresentation();
        CaptureTimingDiagnostics.Mark("editor-cover-released");
    }

    [DllImport("dwmapi.dll", EntryPoint = "DwmFlush")]
    private static extern int FlushResidentEditorComposition();

    private void CommitResidentPresentation()
    {
        var cover = _residentPresentationCover;
        if (cover == IntPtr.Zero) return;
        // The mask is already directly below the cover and above its owner.
        // Removing the cover must not reposition/recreate the layered mask.
        if (!HideResidentPresentationCover(cover, IntPtr.Zero, 0, 0, 0, 0,
                0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0080 | 0x0200))
            throw new InvalidOperationException("截图编辑画面交接失败。");
        _nativeSelectionMask.CompleteCoveredPresentation(_nativeSelectionFrame.Handle);
        _ = NativeMethods.SetWindowLongPtr(cover, -8, _residentPreviousOwner);
        _residentPresentationCover = IntPtr.Zero;
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowPos")]
    private static extern bool HideResidentPresentationCover(IntPtr window, IntPtr after,
        int x, int y, int width, int height, uint flags);

    private void AttachResidentPresentationCover(IntPtr editor)
    {
        if (_options?.ResidentSelectionWindow is not { } cover || cover == IntPtr.Zero) return;
        // Owned windows stay above their owner during activation and first
        // WPF layout. The resident is still responsible for input/cancellation
        // and destroys this temporary cover after the rendered acknowledgement.
        _residentPreviousOwner = NativeMethods.SetWindowLongPtr(cover, -8 /* GWLP_HWNDPARENT */, editor);
        if (NativeMethods.GetWindowLongPtr(cover, -8) != editor) return;
        _residentPresentationCover = cover;
        _nativeSelectionMask.SetPresentationCover(cover);
    }

    private void KeepResidentEditorBelowMask(IntPtr positionPointer)
    {
        // WPF can submit a late HWND_TOPMOST move after ContentRendered. Fix
        // that move before USER32 applies it, rather than repairing the mask
        // one compositor frame after the undimmed owner has covered it.
        if (_residentEditorRendered is null || _isCompleted || _isDisposed ||
            _isScrollCaptureSelectionLocked || positionPointer == IntPtr.Zero) return;
        var above = _nativeSelectionFrame.IsMaskEnabled ? _nativeSelectionMask.Handle : _residentPresentationCover;
        if (above == IntPtr.Zero) return;
        var position = Marshal.PtrToStructure<ResidentWindowPosition>(positionPointer);
        if ((position.Flags & 0x0004) != 0) return;
        position.After = above;
        Marshal.StructureToPtr(position, positionPointer, false);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ResidentWindowPosition
    {
        public IntPtr Window, After;
        public int X, Y, Width, Height;
        public uint Flags;
    }

    private void OnResidentEditorContentRendered(object? sender, EventArgs args)
    {
        CaptureTimingDiagnostics.Mark("editor-content-rendered");
        ContentRendered -= OnResidentEditorContentRendered;
        _residentEditorRendered?.TrySetResult();
    }

    private void OnResidentEditorClosing(object? sender, CancelEventArgs args)
    {
        ContentRendered -= OnResidentEditorContentRendered;
        _residentEditorRendered?.TrySetCanceled();
        // Do not destroy another process's input surface when this editor is
        // cancelled before its first frame. The resident owns its lifetime.
        _nativeSelectionMask.SetPresentationCover(IntPtr.Zero);
        if (_residentPresentationCover != IntPtr.Zero &&
            NativeMethods.GetWindowLongPtr(_residentPresentationCover, -8) == _windowHandle)
            _ = NativeMethods.SetWindowLongPtr(_residentPresentationCover, -8, _residentPreviousOwner);
        _residentPresentationCover = IntPtr.Zero;
        _nativeSelectionMask.SetPresentationCover(IntPtr.Zero);
    }
}
