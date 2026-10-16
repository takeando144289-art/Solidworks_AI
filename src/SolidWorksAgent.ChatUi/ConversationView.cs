using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace SolidWorksAgent.ChatUi;

/// <summary>Selectable message text, with a separate bubble for each user instruction.</summary>
public sealed class ConversationView : Panel
{
    private readonly List<MessageBubble> _messages = [];
    private bool _arranging;
    public event Action<Control>? MessageAdded;
    public string TranscriptText => string.Join("\n\n", _messages.Select(x => x.Role + "\n" + x.MessageText));

    public ConversationView()
    {
        AutoScroll = true; BackColor = Color.White; Dock = DockStyle.Fill;
        DoubleBuffered = true;
        AccessibleName = "チャット履歴";
    }

    public MessageBubble AddMessage(string role, string text)
    {
        var message = new MessageBubble(role, text, role == "あなた") { Font = Font };
        _messages.Add(message); Controls.Add(message);
        ArrangeMessages(); ScrollControlIntoView(message);
        MessageAdded?.Invoke(message);
        return message;
    }

    public void UpdateMessage(MessageBubble message, string text)
    {
        // Do not jump away while the user is reading older output.
        var follow = VerticalScroll.Value + ClientSize.Height >= DisplayRectangle.Height - 32;
        message.MessageText = text; ArrangeMessages();
        if (follow) ScrollControlIntoView(message);
    }

    public void Clear()
    {
        _messages.Clear();
        while (Controls.Count > 0) Controls[0].Dispose();
        AutoScrollMinSize = Size.Empty;
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); ArrangeMessages(); }
    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        foreach (var message in _messages) message.Font = Font;
        ArrangeMessages();
    }
    private void ArrangeMessages()
    {
        if (_arranging || IsDisposed) return;
        _arranging = true;
        try
        {
            var unit = DeviceDpi / 96f;
            var edge = (int)(6 * unit); var gap = (int)(14 * unit);
            // Reserve a scrollbar even before it appears, so wrapping stays stable.
            var available = Math.Max(100, ClientSize.Width - SystemInformation.VerticalScrollBarWidth - edge * 2);
            var y = edge;
            foreach (var message in _messages)
            {
                var width = message.IsUser ? (int)(available * .85f) : available - (int)(18 * unit);
                var x = message.IsUser ? edge + available - width : edge;
                message.SetBounds(x, y + AutoScrollPosition.Y, width, message.MeasureHeight(width));
                message.PerformLayout();
                y += message.Height + gap;
            }
            AutoScrollMinSize = new Size(0, y);
        }
        finally { _arranging = false; }
    }
}

public sealed class MessageBubble : UserControl
{
    private readonly Label _role = new() { AutoSize = false };
    private readonly MessageTextBox _body = new()
    {
        ReadOnly = true, BorderStyle = BorderStyle.None, DetectUrls = false,
        ScrollBars = RichTextBoxScrollBars.None, WordWrap = true, TabStop = true
    };
    private readonly bool _showRole;
    private int _contentHeight, _measuredWidth, _measuredHeight, _measuredDpi;
    private string? _measuredText;
    private Font? _measuredFont;
    public string Role { get; }
    public bool IsUser { get; }
    public string MessageText { get => _body.Text; set => _body.Text = value; }
    private int Inset => (int)((IsUser ? 12 : 4) * DeviceDpi / 96f);
    private int RoleHeight => _showRole ? Font.Height + (int)(7 * DeviceDpi / 96f) : 0;

    public MessageBubble(string role, string text, bool isUser)
    {
        Role = role; IsUser = isUser; DoubleBuffered = true;
        _showRole = role is not ("あなた" or "Codex" or "Claude" or "Gemini");
        _role.Visible = _showRole;
        BackColor = isUser ? Color.FromArgb(240, 244, 250) : Color.White;
        _role.Text = role; _role.ForeColor = Color.FromArgb(82, 96, 116);
        _role.TextAlign = isUser ? ContentAlignment.MiddleRight : ContentAlignment.MiddleLeft;
        _body.BackColor = BackColor; _body.ForeColor = Color.FromArgb(24, 32, 46);
        _body.AccessibleName = role + "のメッセージ";
        Controls.Add(_role); Controls.Add(_body);
        _body.ContentsResized += (_, e) => _contentHeight = e.NewRectangle.Height;
        MessageText = text;
    }
    public int MeasureHeight(int width)
    {
        if (_measuredWidth == width && _measuredText == MessageText && Equals(_measuredFont, Font) && _measuredDpi == DeviceDpi)
            return _measuredHeight;
        SuspendLayout();
        try
        {
            // Ask RichEdit itself: GDI text measurement wraps Japanese text differently.
            // Suspend parent layout so it cannot restore the previous width during measurement.
            _body.Size = new Size(Math.Max(30, width - Inset * 2), 1);
            SendMessage(_body.Handle, 0x0441, 0, 0); // EM_REQUESTRESIZE
            _measuredWidth = width; _measuredText = MessageText; _measuredFont = Font; _measuredDpi = DeviceDpi;
            return _measuredHeight = Inset * 2 + RoleHeight + Math.Max(Font.Height, _contentHeight) + (int)(6 * DeviceDpi / 96f);
        }
        finally { ResumeLayout(false); }
    }
    [DllImport("user32.dll")] private static extern nint SendMessage(nint hwnd, uint message, nint wparam, nint lparam);

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        _role.SetBounds(Inset, Inset, Math.Max(1, Width - Inset * 2), RoleHeight);
        _body.SetBounds(Inset, Inset + RoleHeight, Math.Max(1, Width - Inset * 2), Math.Max(1, Height - Inset * 2 - RoleHeight));
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!IsUser || Width < 20 || Height < 20) return;
        using var path = new GraphicsPath();
        var diameter = (int)(16 * DeviceDpi / 96f);
        var right = Width - 1; var bottom = Height - 1;
        path.AddArc(0, 0, diameter, diameter, 180, 90);
        path.AddArc(right - diameter, 0, diameter, diameter, 270, 90);
        path.AddArc(right - diameter, bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(0, bottom - diameter, diameter, diameter, 90, 90); path.CloseFigure();
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Color.FromArgb(210, 220, 234));
        e.Graphics.DrawPath(pen, path);
    }
    private sealed class MessageTextBox : RichTextBox
    {
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x020A) // WM_MOUSEWHEEL: scroll the conversation, not the inner RichEdit.
            {
                Control? parent = Parent;
                while (parent is not null && parent is not ConversationView) parent = parent.Parent;
                if (parent is not null)
                {
                    message.Result = SendMessage(parent.Handle, (uint)message.Msg, message.WParam, message.LParam);
                    return;
                }
            }
            base.WndProc(ref message);
        }
    }

}
