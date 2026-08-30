using System.Drawing;
using System.Windows.Forms;
using RemoteControl.Capture;

namespace RemoteControl.Tools.LoopbackHarness;

internal sealed class PresentationWindow : IDisposable
{
    private readonly Form _form;
    private Size? _pendingClientSize;

    public nint Handle => _form.Handle;
    public bool IsClosed { get; private set; }
    public uint ClientWidth => (uint)Math.Max(_form.ClientSize.Width, 0);
    public uint ClientHeight => (uint)Math.Max(_form.ClientSize.Height, 0);

    public PresentationWindow(DisplayInfo display)
    {
        var width = Math.Min(1280, Math.Max(640, display.Width - 120));
        var height = Math.Min(720, Math.Max(360, display.Height - 160));
        _form = new Form
        {
            Text = "Remote-Control-Native — Phase 0 live loopback",
            StartPosition = FormStartPosition.Manual,
            Location = new Point(display.Left + 60, display.Top + 60),
            ClientSize = new Size(width, height),
            MinimumSize = new Size(480, 270),
            BackColor = Color.Black,
        };
        _form.FormClosed += (_, _) => IsClosed = true;
        _form.ClientSizeChanged += (_, _) => _pendingClientSize = _form.ClientSize;
        _form.Show();
        Application.DoEvents();
        _pendingClientSize = null;
    }

    public void PumpEvents() => Application.DoEvents();

    public void ResizeClient(int width, int height)
    {
        _form.ClientSize = new Size(width, height);
        Application.DoEvents();
    }

    public void Minimize()
    {
        _form.WindowState = FormWindowState.Minimized;
        Application.DoEvents();
    }

    public void Restore()
    {
        _form.WindowState = FormWindowState.Normal;
        _form.Activate();
        Application.DoEvents();
    }

    public bool TryConsumeResize(out uint width, out uint height)
    {
        if (_pendingClientSize is not { } size)
        {
            width = 0;
            height = 0;
            return false;
        }

        _pendingClientSize = null;
        width = (uint)Math.Max(size.Width, 0);
        height = (uint)Math.Max(size.Height, 0);
        return true;
    }

    public void Dispose() => _form.Dispose();
}
