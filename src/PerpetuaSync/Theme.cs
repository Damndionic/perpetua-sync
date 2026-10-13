using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace PerpetuaSync;

/// <summary>
/// The site's look (src/perpetua.css): navy and gold, Cinzel for headings and buttons, Cormorant Garamond for text.
/// The fonts are the addon's own (Media/Fonts, SIL Open Font License), built into the exe; nothing is installed.
/// </summary>
static class Theme
{
    public static readonly Color Gold = Hex("#d4af37"), GoldPale = Hex("#f2d78c"), GoldWarm = Hex("#e8c874");
    public static readonly Color Navy = Hex("#0e1a40"), Midnight = Hex("#0c1730"), Royal = Hex("#152a5e"), RoyalLight = Hex("#1c2f6e");
    public static readonly Color Text = Hex("#efe6c8"), Muted = Hex("#a3acc9"), Danger = Hex("#ff8a7a"), Ok = Hex("#8fd18a");
    /// <summary>--line: gold at 32% over the navy.</summary>
    public static readonly Color Line = Blend(Gold, Navy, .32);

    static Color Hex(string h) => ColorTranslator.FromHtml(h);
    public static Color Blend(Color a, Color b, double t) =>
        Color.FromArgb((int)(a.R * t + b.R * (1 - t)), (int)(a.G * t + b.G * (1 - t)), (int)(a.B * t + b.B * (1 - t)));

    static readonly PrivateFontCollection Fonts = new();

    [DllImport("gdi32.dll")]
    static extern IntPtr AddFontMemResourceEx(IntPtr font, uint size, IntPtr reserved, ref uint count);

    /// <summary>Loads the built-in fonts, for GDI+ (drawing) and for GDI (text boxes), for this process only.</summary>
    public static void Load()
    {
        var asm = typeof(Theme).Assembly;
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.EndsWith(".ttf", StringComparison.Ordinal)))
        {
            using var s = asm.GetManifestResourceStream(name)!;
            var bytes = new byte[s.Length];
            s.ReadExactly(bytes);
            var mem = Marshal.AllocCoTaskMem(bytes.Length); // kept for the life of the process, as both APIs need
            Marshal.Copy(bytes, 0, mem, bytes.Length);
            Fonts.AddMemoryFont(mem, bytes.Length);
            uint count = 0;
            AddFontMemResourceEx(mem, (uint)bytes.Length, IntPtr.Zero, ref count);
        }
    }

    static Font Make(string[] families, float size, FontStyle style)
    {
        foreach (var f in families)
        {
            var fam = Fonts.Families.FirstOrDefault(x => x.Name == f);
            if (fam != null && fam.IsStyleAvailable(style)) return new Font(fam, size, style, GraphicsUnit.Point);
        }
        return new Font("Georgia", size, style == FontStyle.Italic ? FontStyle.Italic : FontStyle.Regular, GraphicsUnit.Point);
    }

    /// <summary>Cinzel: bold for the title, semibold for captions and buttons.</summary>
    public static Font Display(float size, bool bold = false) =>
        bold ? Make(["Cinzel"], size, FontStyle.Bold) : Make(["Cinzel SemiBold", "Cinzel"], size, FontStyle.Regular);

    /// <summary>Cormorant Garamond semibold, upright or italic.</summary>
    public static Font Serif(float size, bool italic = false) =>
        Make(["Cormorant Garamond SemiBold", "Cormorant Garamond"], size, italic ? FontStyle.Italic : FontStyle.Regular);

    /// <summary>Text with letter spacing (Cinzel headings are spaced out on the site). Returns the width drawn.</summary>
    public static float DrawSpaced(Graphics g, string text, Font font, Color color, float x, float y, float spacingEm)
    {
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var gap = font.SizeInPoints * g.DpiX / 72f * spacingEm;
        var start = x;
        using var brush = new SolidBrush(color);
        using var fmt = (StringFormat)StringFormat.GenericTypographic.Clone();
        fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
        foreach (var ch in text)
        {
            var s = ch.ToString();
            g.DrawString(s, font, brush, x, y, fmt);
            x += g.MeasureString(s, font, PointF.Empty, fmt).Width + gap;
        }
        return x - gap - start;
    }

    public static SizeF MeasureSpaced(Graphics g, string text, Font font, float spacingEm)
    {
        var gap = font.SizeInPoints * g.DpiX / 72f * spacingEm;
        using var fmt = (StringFormat)StringFormat.GenericTypographic.Clone();
        fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
        float w = 0, h = 0;
        foreach (var ch in text)
        {
            var sz = g.MeasureString(ch.ToString(), font, PointF.Empty, fmt);
            w += sz.Width + gap;
            h = Math.Max(h, sz.Height);
        }
        return new SizeF(Math.Max(0, w - gap), h);
    }

    /// <summary>Dark title bar to match (Windows 10 20H1+), and a navy caption on Windows 11. Ignored elsewhere.</summary>
    public static void DarkTitleBar(Form form)
    {
        try
        {
            var on = 1;
            DwmSetWindowAttribute(form.Handle, 20, ref on, sizeof(int));
            var caption = Navy.R | Navy.G << 8 | Navy.B << 16; // COLORREF is 0x00BBGGRR
            DwmSetWindowAttribute(form.Handle, 35, ref caption, sizeof(int));
            var text = GoldPale.R | GoldPale.G << 8 | GoldPale.B << 16;
            DwmSetWindowAttribute(form.Handle, 36, ref text, sizeof(int));
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}

/// <summary>The site's background: a soft royal glow at the top fading to navy and midnight.</summary>
sealed class Backdrop : Panel
{
    public Backdrop() { DoubleBuffered = true; ResizeRedraw = true; BackColor = Theme.Navy; }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var r = ClientRectangle;
        if (r.Width <= 0 || r.Height <= 0) return;
        using var b = new LinearGradientBrush(r, Theme.RoyalLight, Theme.Midnight, LinearGradientMode.Vertical);
        b.InterpolationColors = new ColorBlend { Colors = [Theme.RoyalLight, Theme.Navy, Theme.Midnight], Positions = [0f, .55f, 1f] };
        e.Graphics.FillRectangle(b, r);
    }
}

/// <summary>The crest from the site (src/perpetua.js #crest): two rings and the infinity knot, drawn as vectors.</summary>
sealed class Crest : Control
{
    public Crest() { SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true); BackColor = Color.Transparent; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var s = Math.Min(Width, Height) / 120f;
        g.ScaleTransform(s, s);
        using var ring = new Pen(Theme.Gold, 3f);
        using var inner = new Pen(Color.FromArgb(128, Theme.Gold), 1.2f);
        using var knot = new Pen(Theme.Gold, 5f) { LineJoin = LineJoin.Round };
        g.DrawEllipse(ring, 5, 5, 110, 110);
        g.DrawEllipse(inner, 13, 13, 94, 94);
        using var p = new GraphicsPath();
        p.AddBeziers([
            new PointF(60, 60), new PointF(52, 48), new PointF(44, 44), new PointF(38, 44),
            new PointF(29, 44), new PointF(24, 51), new PointF(24, 60),
            new PointF(24, 69), new PointF(29, 76), new PointF(38, 76),
            new PointF(44, 76), new PointF(52, 72), new PointF(60, 60),
            new PointF(68, 48), new PointF(76, 44), new PointF(82, 44),
            new PointF(91, 44), new PointF(96, 51), new PointF(96, 60),
            new PointF(96, 69), new PointF(91, 76), new PointF(82, 76),
            new PointF(76, 76), new PointF(68, 72), new PointF(60, 60),
        ]);
        p.CloseFigure();
        g.DrawPath(knot, p);
    }
}

/// <summary>Spaced capitals in Cinzel, like the site's headings and section captions.</summary>
sealed class SpacedLabel : Control
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public float Spacing { get; set; } = .16f;

    public SpacedLabel(string text, Font font, Color color, float spacing = .16f)
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        Text = text;
        Font = font;
        ForeColor = color;
        Spacing = spacing;
        Fit();
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Fit(); Invalidate(); }

    void Fit()
    {
        using var g = CreateGraphics();
        var sz = Theme.MeasureSpaced(g, Text.ToUpperInvariant(), Font, Spacing);
        Size = new Size((int)Math.Ceiling(sz.Width) + 4, (int)Math.Ceiling(sz.Height) + 2);
    }

    protected override void OnPaint(PaintEventArgs e) => Theme.DrawSpaced(e.Graphics, Text.ToUpperInvariant(), Font, ForeColor, 0, 1, Spacing);
}

/// <summary>The site's .btn / .cta: gold outline, solid gold for the main action, coral for danger. A real Button
/// underneath, so focus, Enter/Space and screen readers work as usual.</summary>
sealed class GoldButton : Button
{
    public enum Kinds { Outline, Solid, Danger }
    Kinds _kind;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Kinds Kind { get => _kind; set { _kind = value; Invalidate(); } }
    bool _hover, _down;

    public GoldButton(string text, Kinds kind = Kinds.Outline)
    {
        Text = text;
        Kind = kind;
        Font = Theme.Display(8.5f);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Padding = new Padding(14, 9, 14, 9);
        Fit();
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Fit(); Invalidate(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Fit(); }
    protected override void OnPaddingChanged(EventArgs e) { base.OnPaddingChanged(e); Fit(); }

    void Fit()
    {
        using var g = CreateGraphics();
        var sz = Theme.MeasureSpaced(g, Text.ToUpperInvariant(), Font, .14f);
        Size = new Size((int)Math.Ceiling(sz.Width) + Padding.Horizontal, (int)Math.Ceiling(sz.Height) + Padding.Vertical);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        // Parent's backdrop behind us (transparent corners and the outline buttons' insides).
        InvokePaintBackground(this, e);
        g.SmoothingMode = SmoothingMode.None;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        var edge = Kind == Kinds.Danger ? Theme.Blend(Theme.Danger, Theme.Navy, .6) : Theme.Gold;
        Color fill, ink;
        if (Kind == Kinds.Solid)
        {
            fill = _hover ? Theme.GoldPale : Theme.Gold;
            if (_down) fill = Theme.GoldWarm;
            ink = Theme.Navy;
        }
        else
        {
            fill = _hover ? Color.FromArgb(_down ? 50 : 31, Theme.Gold) : Color.Transparent;
            ink = Kind == Kinds.Danger ? Theme.Danger : _hover ? Theme.GoldPale : Theme.Gold;
        }
        if (!Enabled)
        {
            // The site's [disabled]: 45% opacity.
            fill = fill.A == 0 ? fill : Color.FromArgb(115, fill);
            edge = Color.FromArgb(115, edge);
            ink = Kind == Kinds.Solid ? Color.FromArgb(160, Theme.Navy) : Color.FromArgb(115, ink);
        }
        if (fill.A > 0) using (var b = new SolidBrush(fill)) g.FillRectangle(b, r);
        using (var p = new Pen(Kind == Kinds.Solid && Enabled ? fill : edge)) g.DrawRectangle(p, r);
        var text = Text.ToUpperInvariant();
        var sz = Theme.MeasureSpaced(g, text, Font, .14f);
        Theme.DrawSpaced(g, text, Font, ink, (Width - sz.Width) / 2f, (Height - sz.Height) / 2f, .14f);
        if (Focused && ShowFocusCues)
        {
            using var f = new Pen(Theme.GoldPale) { DashStyle = DashStyle.Dot };
            g.DrawRectangle(f, 2, 2, Width - 5, Height - 5);
        }
    }
}

/// <summary>The site's .sec box: a caption with a gold rule under it, a thin gold border, a dark blue fill.</summary>
sealed class Section : Panel
{
    readonly SpacedLabel _title;
    readonly Label _aside;
    public TableLayoutPanel Body { get; }

    public Section(string title, string? aside = null)
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Color.Transparent;
        Padding = new Padding(16, 12, 16, 14);
        Margin = new Padding(0, 0, 0, 14);
        Dock = DockStyle.Fill;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        _title = new SpacedLabel(title, Theme.Display(8.5f), Theme.Gold) { Location = new Point(16, 12) };
        _aside = new Label { Text = aside ?? "", AutoSize = true, ForeColor = Theme.Muted, BackColor = Color.Transparent, Font = Theme.Serif(11f, italic: true), UseCompatibleTextRendering = true };
        Body = new TableLayoutPanel
        {
            BackColor = Color.Transparent, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill,
            Margin = Padding.Empty, Padding = new Padding(0, _title.Height + 14, 0, 0),
        };
        Controls.Add(_title);
        Controls.Add(_aside);
        Controls.Add(Body);
        _title.BringToFront();
        _aside.BringToFront();
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Aside { get => _aside.Text; set { _aside.Text = value; PerformLayout(); } }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (_aside == null || _title == null) return; // layout runs once from the constructor, before they exist
        _aside.Location = new Point(Width - Padding.Right - _aside.Width, _title.Top + _title.Height - _aside.Height + 2);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
        var g = e.Graphics;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        if (r.Width <= 0 || r.Height <= 0) return;
        // linear-gradient(180deg, royal at 45%, midnight at 80%) over the page.
        using (var b = new LinearGradientBrush(r, Color.FromArgb(115, Theme.Royal), Color.FromArgb(204, Theme.Midnight), LinearGradientMode.Vertical))
            g.FillRectangle(b, r);
        using var line = new Pen(Theme.Line);
        g.DrawRectangle(line, r);
        var y = _title.Bottom + 6;
        g.DrawLine(line, Padding.Left, y, Width - Padding.Right, y);
    }
}

/// <summary>A text field in the site's style: midnight fill, gold-line border.</summary>
sealed class Field : Panel
{
    public TextBox Box { get; } = new()
    {
        BorderStyle = BorderStyle.None, BackColor = Theme.Midnight, ForeColor = Theme.Text, Dock = DockStyle.Fill, ReadOnly = true,
    };

    public Field()
    {
        BackColor = Theme.Midnight;
        Padding = new Padding(8, 6, 8, 6);
        Box.Font = Theme.Serif(11.5f);
        Height = Box.PreferredHeight + Padding.Vertical;
        Controls.Add(Box);
        Dock = DockStyle.Fill;
        Margin = new Padding(0, 0, 10, 0);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var p = new Pen(Theme.Line);
        e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
    }
}

/// <summary>Tabs as spaced Cinzel words; the chosen one gold with a gold rule under it (the site's nav).</summary>
sealed class TabStrip : Control
{
    readonly string[] _names;
    int _index, _hover = -1;
    RectangleF[] _boxes = [];
    public event Action<int>? Selected;

    public TabStrip(string[] names)
    {
        _names = names;
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        Font = Theme.Display(8.5f);
        Cursor = Cursors.Hand;
        TabStop = true;
        Margin = new Padding(0, 6, 0, 0);
        using var g = CreateGraphics();
        var w = names.Sum(n => Theme.MeasureSpaced(g, n.ToUpperInvariant(), Font, .16f).Width + 36);
        Size = new Size((int)Math.Ceiling(w), (int)Math.Ceiling(Theme.MeasureSpaced(g, "H", Font, 0).Height) + 16);
    }

    public void Select(int i) { _index = i; Invalidate(); }

    void Pick(int i) { if (i < 0) return; _index = i; Invalidate(); Selected?.Invoke(i); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var x = 0f;
        _boxes = new RectangleF[_names.Length];
        for (var i = 0; i < _names.Length; i++)
        {
            var t = _names[i].ToUpperInvariant();
            var sz = Theme.MeasureSpaced(g, t, Font, .16f);
            var box = new RectangleF(x, 0, sz.Width + 36, Height);
            _boxes[i] = box;
            var on = i == _index;
            Theme.DrawSpaced(g, t, Font, on ? Theme.Gold : i == _hover ? Theme.GoldPale : Theme.Muted, x + 18, 5, .16f);
            if (on) using (var p = new Pen(Theme.Gold, 2)) g.DrawLine(p, x + 18, Height - 3, x + 18 + sz.Width, Height - 3);
            if (on && Focused && ShowFocusCues) using (var f = new Pen(Theme.GoldPale) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot }) g.DrawRectangle(f, x + 4, 1, box.Width - 9, Height - 3);
            x += box.Width;
        }
    }

    int At(Point p) => Array.FindIndex(_boxes, b => b.Contains(p));
    protected override void OnMouseMove(MouseEventArgs e) { var h = At(e.Location); if (h != _hover) { _hover = h; Invalidate(); } base.OnMouseMove(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseClick(MouseEventArgs e) { Focus(); Pick(At(e.Location)); base.OnMouseClick(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override bool IsInputKey(Keys k) => k is Keys.Left or Keys.Right || base.IsInputKey(k);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Left) Pick((_index + _names.Length - 1) % _names.Length);
        else if (e.KeyCode == Keys.Right) Pick((_index + 1) % _names.Length);
        base.OnKeyDown(e);
    }
}

/// <summary>A wrapping label whose size is measured the way it's drawn (GDI+), so wrapped lines aren't cut off.</summary>
sealed class WrapLabel : Label
{
    public WrapLabel() { UseCompatibleTextRendering = true; AutoSize = true; }

    public override Size GetPreferredSize(Size proposed)
    {
        var max = MaximumSize.Width > 0 ? MaximumSize.Width : proposed.Width is > 1 and < 100_000 ? proposed.Width : 100_000;
        using var g = CreateGraphics();
        var sz = g.MeasureString(Text.Length == 0 ? " " : Text, Font, Math.Max(20, max - Padding.Horizontal - 4));
        return new Size(Math.Min(max, (int)Math.Ceiling(sz.Width) + Padding.Horizontal + 4), (int)Math.Ceiling(sz.Height) + Padding.Vertical + 2);
    }
}

/// <summary>A clickable picture (gold, pale gold on hover), drawn smoothly at any size.</summary>
sealed class IconLink : Control
{
    readonly Image _normal, _hover;
    bool _over;

    public IconLink(string normal, string hover, string tip)
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleName = tip;
        AccessibleRole = AccessibleRole.Link;
        _normal = Load(normal);
        _hover = Load(hover);
        new ToolTip().SetToolTip(this, tip);
    }

    static Image Load(string name)
    {
        using var s = typeof(IconLink).Assembly.GetManifestResourceStream(name)!;
        return Image.FromStream(s);
    }

    protected override void OnMouseEnter(EventArgs e) { _over = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _over = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnMouseClick(MouseEventArgs e) { OnClick(e); }
    protected override bool IsInputKey(Keys k) => k is Keys.Enter or Keys.Space || base.IsInputKey(k);
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode is Keys.Enter or Keys.Space) OnClick(e); base.OnKeyDown(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        e.Graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        e.Graphics.DrawImage(_over || Focused ? _hover : _normal, new Rectangle(0, 0, Width, Height));
    }
}

/// <summary>Spaced Cinzel words that act as a link (muted, pale gold on hover), for the bottom bar.</summary>
sealed class WordLink : Control
{
    bool _over;

    public WordLink(string text)
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        TabStop = true;
        Text = text;
        AccessibleName = text;
        AccessibleRole = AccessibleRole.Link;
        Font = Theme.Display(8.5f);
        using var g = CreateGraphics();
        var sz = Theme.MeasureSpaced(g, text.ToUpperInvariant(), Font, .16f);
        Size = new Size((int)Math.Ceiling(sz.Width) + 4, (int)Math.Ceiling(sz.Height) + 16);
    }

    protected override void OnMouseEnter(EventArgs e) { _over = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _over = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnMouseClick(MouseEventArgs e) { OnClick(e); }
    protected override bool IsInputKey(Keys k) => k is Keys.Enter or Keys.Space || base.IsInputKey(k);
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode is Keys.Enter or Keys.Space) OnClick(e); base.OnKeyDown(e); }

    protected override void OnPaint(PaintEventArgs e) =>
        Theme.DrawSpaced(e.Graphics, Text.ToUpperInvariant(), Font, _over || Focused ? Theme.GoldPale : Theme.Muted, 2, 5, .16f);
}
