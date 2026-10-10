using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
namespace CaptureApp;
// Keep the native single-line editor, but let the surrounding frame share the buttons' height.
internal sealed class CaseNameInput : UserControl
{
    private readonly TextBox editor = new() { BorderStyle = BorderStyle.None, AutoSize = true, Multiline = false };
    public CaseNameInput()
    {
        DoubleBuffered = true; BackColor = Color.White;
        Controls.Add(editor);
        editor.TextChanged += (_, _) => OnTextChanged(EventArgs.Empty);
        editor.GotFocus += (_, _) => Invalidate();
        editor.LostFocus += (_, _) => Invalidate();
    }
    [AllowNull]
    public override string Text { get => editor.Text; set => editor.Text = value; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string PlaceholderText { get => editor.PlaceholderText; set => editor.PlaceholderText = value; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int MaxLength { get => editor.MaxLength; set => editor.MaxLength = value; }
    public void Clear() => editor.Clear();
    protected override void OnEnter(EventArgs e) { base.OnEnter(e); editor.Focus(); }
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); ArrangeEditor(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); editor.Font = Font; ArrangeEditor(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); ArrangeEditor(); }
    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        editor.BackColor = BackColor = Enabled ? Color.White : Color.FromArgb(235, 239, 245);
        Invalidate();
    }
    private void ArrangeEditor()
    {
        var inset = (int)Math.Round(10 * DeviceDpi / 96F);
        var height = editor.PreferredHeight;
        editor.SetBounds(inset, Math.Max(1, (ClientSize.Height - height) / 2), Math.Max(1, ClientSize.Width - inset * 2), height);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(editor.Focused ? Color.FromArgb(27, 102, 221) : Color.FromArgb(127, 141, 158));
        e.Graphics.DrawRectangle(pen, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
    }
}
