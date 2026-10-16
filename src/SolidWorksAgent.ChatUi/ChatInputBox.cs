namespace SolidWorksAgent.ChatUi;

/// <summary>Owns Enter handling when hosted in SOLIDWORKS' native dialog loop.</summary>
internal sealed class ChatInputBox : TextBox
{
    private bool _imeComposing;
    private bool _imeConfirmKey;

    public event EventHandler? SubmitRequested;

    public ChatInputBox()
    {
        Multiline = true;
        AcceptsReturn = true;
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) == Keys.Enter || base.IsInputKey(keyData);

    protected override void WndProc(ref Message message)
    {
        const int WmGetDlgCode = 0x0087, WantMessage = 0x0004;
        const int WmImeStartComposition = 0x010D, WmImeEndComposition = 0x010E;
        const int WmKeyDown = 0x0100, WmKeyUp = 0x0101, WmImeKeyDown = 0x0290, WmImeKeyUp = 0x0291;
        if (message.Msg == WmImeStartComposition) _imeComposing = true;
        if (message.Msg == WmImeEndComposition) _imeComposing = false;
        if ((message.Msg == WmKeyDown && _imeComposing || message.Msg == WmImeKeyDown)
            && message.WParam == (nint)Keys.Enter) _imeConfirmKey = true;

        base.WndProc(ref message);
        // The native dialog manager asks the focused HWND before dispatching Enter.
        // KeyDown/IsInputKey alone only cover the managed WinForms preprocessing path.
        if (message.Msg == WmGetDlgCode && message.WParam == (nint)Keys.Enter)
            message.Result = (nint)((long)message.Result | WantMessage);
        if ((message.Msg == WmKeyUp || message.Msg == WmImeKeyUp)
            && (message.WParam == (nint)Keys.Enter || message.WParam == (nint)Keys.ProcessKey))
            _imeConfirmKey = false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter && !e.Control && !e.Alt && !_imeComposing && !_imeConfirmKey)
        {
            e.SuppressKeyPress = true;
            if (e.Shift) SelectedText = Environment.NewLine;
            else SubmitRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        _imeComposing = _imeConfirmKey = false;
        base.OnLostFocus(e);
    }
}
