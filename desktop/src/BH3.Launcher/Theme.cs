using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace BH3.Launcher;

internal static class Theme
{
    internal static readonly Color Background = Color.FromArgb(15, 18, 23), Surface = Color.FromArgb(23, 28, 35), Border = Color.FromArgb(48, 55, 66), Text = Color.FromArgb(244, 246, 248), Muted = Color.FromArgb(171, 179, 188), Accent = Color.FromArgb(232, 189, 105), Cyan = Color.FromArgb(104, 220, 197), Error = Color.FromArgb(240, 128, 112);
    internal static Font Font(float size = 10, bool bold = false) => new("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular);
    internal static Label Label(string text, float size = 10, Color? color = null, bool bold = false) => new() { Text = text, ForeColor = color ?? Text, Font = Font(size, bold), AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent };
    internal static TextBox Input(string text = "", bool readOnly = false) => new() { Text = text, BackColor = Background, ForeColor = Text, BorderStyle = BorderStyle.FixedSingle, Font = Font(), ReadOnly = readOnly };
    internal static void DarkTitle(Form form) { int yes = 1; DwmSetWindowAttribute(form.Handle, 20, ref yes, 4); }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
internal sealed class ActionButton : Button
{
    private bool hover;
    [System.ComponentModel.DefaultValue(false)] internal bool Primary { get; set; }
    [System.ComponentModel.DefaultValue(false)] internal bool Selected { get; set; }
    internal ActionButton(string text, bool primary = false)
    {
        Text = text; Primary = primary; AccessibleName = text; Font = Theme.Font(10, primary);
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; Cursor = Cursors.Hand; Height = 40;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
    }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var fill = !Enabled ? Theme.Border : Primary ? (hover ? Color.FromArgb(246, 205, 130) : Theme.Accent) : hover || Selected ? Color.FromArgb(42, 47, 53) : Theme.Surface;
        e.Graphics.Clear(fill);
        using var pen = new Pen(Selected ? Theme.Accent : Theme.Border); e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        var ink = !Enabled ? Theme.Muted : Primary ? Theme.Background : Selected ? Theme.Accent : Theme.Text;
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4), ink, fill);
    }
}
internal sealed class HeroPanel : Panel
{
    private Image? art;
    internal HeroPanel() { DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw, true); AccessibleName = "崩坏 3 主视觉"; }
    internal void SetArt(string path)
    {
        Image? next = null;
        if (!string.IsNullOrWhiteSpace(path)) { using var image = Image.FromFile(path); next = new Bitmap(image); }
        art?.Dispose(); art = next; Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; float scale = DeviceDpi / 96f;
        using var gradient = new LinearGradientBrush(ClientRectangle, Color.FromArgb(18, 37, 51), Color.FromArgb(7, 12, 20), 75);
        g.FillRectangle(gradient, ClientRectangle);
        if (art is not null)
        {
            float ratio = Math.Max(Width / (float)art.Width, Height / (float)art.Height);
            g.DrawImage(art, (Width - art.Width * ratio) / 2, (Height - art.Height * ratio) / 2, art.Width * ratio, art.Height * ratio);
        }
        else
        {
            using var grid = new Pen(Color.FromArgb(20, Theme.Cyan));
            for (int x = 0; x < Width; x += (int)(40 * scale)) g.DrawLine(grid, x, 0, x, Height);
            for (int y = 0; y < Height; y += (int)(40 * scale)) g.DrawLine(grid, 0, y, Width, y);
            float cx = Width * .57f, cy = Height * .38f, radius = Math.Min(Width * .27f, Height * .28f);
            using var outer = new Pen(Color.FromArgb(48, Theme.Cyan), 1);
            for (int i = 0; i < 3; i++) { float r = radius + i * 26 * scale; g.DrawEllipse(outer, cx - r, cy - r, r * 2, r * 2); }
            PointF[] left = [new(cx, cy - radius), new(cx - radius * .72f, cy + radius * .17f), new(cx, cy + radius)];
            PointF[] right = [new(cx, cy - radius), new(cx + radius * .72f, cy + radius * .17f), new(cx, cy + radius)];
            using var crystal = new LinearGradientBrush(new RectangleF(cx - radius, cy - radius, radius * 2, radius * 2), Color.FromArgb(115, 147, 247, 237), Color.FromArgb(20, 30, 96, 153), 35);
            g.FillPolygon(crystal, left); using var shade = new SolidBrush(Color.FromArgb(70, 40, 98, 139)); g.FillPolygon(shade, right);
            using var edge = new Pen(Color.FromArgb(155, Theme.Cyan), 1.3f * scale); g.DrawPolygon(edge, left); g.DrawPolygon(edge, right);
            g.DrawLine(edge, cx - radius * .72f, cy + radius * .17f, cx + radius * .72f, cy + radius * .17f);
            using var glint = new SolidBrush(Theme.Accent); g.FillEllipse(glint, cx - 3 * scale, cy - radius - 3 * scale, 6 * scale, 6 * scale);
        }
        using var fade = new LinearGradientBrush(ClientRectangle, Color.Transparent, Color.FromArgb(248, 10, 14, 20), 90); g.FillRectangle(fade, ClientRectangle);
        int pad = (int)(32 * scale), bottom = Height - (int)(185 * scale);
        using var small = Theme.Font(9, true); using var title = Theme.Font(34, true); using var body = Theme.Font(11);
        TextRenderer.DrawText(g, "H Y P E R I O N   /   LOCAL", small, new Point(pad, pad), Theme.Accent, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, "为世界上所有的美好而战", small, new Point(pad, bottom), Theme.Accent, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, "崩坏 3", title, new Point(pad - 2, bottom + (int)(30 * scale)), Theme.Text, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, "9.1.0  /  本地启动", body, new Point(pad, bottom + (int)(101 * scale)), Theme.Muted, TextFormatFlags.NoPadding);
        using var border = new Pen(Theme.Border); g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
    }
    protected override void Dispose(bool disposing) { if (disposing) art?.Dispose(); base.Dispose(disposing); }
}
