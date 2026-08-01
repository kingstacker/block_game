using System.Drawing;
using System.Drawing.Drawing2D;

namespace BlockGame.DropBridge;

internal sealed class DropBridgeForm : Form
{
    private static readonly Color SurfaceColor = Color.FromArgb(239, 246, 255);
    private static readonly Color BorderColor = Color.FromArgb(191, 219, 254);
    private static readonly Color AccentColor = Color.FromArgb(29, 78, 216);
    private readonly Action<IReadOnlyList<string>> _filesSelected;

    public DropBridgeForm(Action<IReadOnlyList<string>> filesSelected)
    {
        _filesSelected = filesSelected ?? throw new ArgumentNullException(nameof(filesSelected));
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = SurfaceColor;
        ClientSize = new Size(720, 58);
        DoubleBuffered = true;
        FormBorderStyle = FormBorderStyle.None;
        Location = new Point(-32_000, -32_000);
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;

        var surface = new RoundedSurfacePanel
        {
            BackColor = SurfaceColor,
            BorderColor = BorderColor,
            CornerRadius = 8,
            Dock = DockStyle.Fill
        };
        var content = new TableLayoutPanel
        {
            BackColor = Color.Transparent,
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            Padding = new Padding(14, 9, 14, 9),
            RowCount = 1
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var label = new Label
        {
            AutoEllipsis = true,
            BackColor = Color.Transparent,
            Dock = DockStyle.Fill,
            Font = new Font("Microsoft YaHei UI", 9F),
            ForeColor = AccentColor,
            Margin = Padding.Empty,
            Text = "将桌面或开始菜单中的 .lnk 快捷方式拖到这里，或点击右侧按钮选择文件。",
            TextAlign = ContentAlignment.MiddleLeft
        };
        var browseButton = new RoundedButton
        {
            Anchor = AnchorStyles.None,
            BackColor = Color.White,
            BorderColor = BorderColor,
            CanvasBackColor = SurfaceColor,
            CornerRadius = 6,
            Cursor = Cursors.Hand,
            Font = new Font("Microsoft YaHei UI", 9F),
            ForeColor = AccentColor,
            HoverBackColor = SurfaceColor,
            Margin = new Padding(16, 0, 0, 0),
            PressedBackColor = Color.FromArgb(219, 234, 254),
            Size = new Size(126, 38),
            Text = "选择快捷方式",
            UseVisualStyleBackColor = false
        };
        browseButton.Click += (_, _) => BrowseShortcutFiles();
        content.Controls.Add(label, 0, 0);
        content.Controls.Add(browseButton, 1, 0);
        surface.Controls.Add(content);
        Controls.Add(surface);

        RegisterDropTarget(this);
        RegisterDropTarget(surface);
        RegisterDropTarget(content);
        RegisterDropTarget(label);
        RegisterDropTarget(browseButton);
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateRoundedRegion();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UpdateRoundedRegion();
    }

    private void RegisterDropTarget(Control control)
    {
        control.AllowDrop = true;
        control.DragEnter += DropTarget_DragEnter;
        control.DragOver += DropTarget_DragEnter;
        control.DragDrop += DropTarget_DragDrop;
    }

    private void DropTarget_DragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = GetShortcutPaths(e.Data).Length > 0
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void DropTarget_DragDrop(object? sender, DragEventArgs e)
    {
        string[] paths = GetShortcutPaths(e.Data);
        if (paths.Length > 0)
        {
            _filesSelected(paths);
        }
    }

    private void BrowseShortcutFiles()
    {
        using var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            DefaultExt = ".lnk",
            Filter = "Windows 快捷方式 (*.lnk)|*.lnk",
            Multiselect = true,
            Title = "选择 Windows 快捷方式"
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _filesSelected(dialog.FileNames);
        }
    }

    private static string[] GetShortcutPaths(IDataObject? data)
    {
        if (data?.GetDataPresent(DataFormats.FileDrop) != true
            || data.GetData(DataFormats.FileDrop) is not string[] files)
        {
            return [];
        }

        return files
            .Where(path => string.Equals(
                Path.GetExtension(path),
                ".lnk",
                StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(256)
            .ToArray();
    }

    private void UpdateRoundedRegion()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            return;
        }

        int radius = ScaleLogicalPixels(8);
        using GraphicsPath path = RoundedGeometry.CreatePath(
            new Rectangle(0, 0, ClientSize.Width, ClientSize.Height),
            radius);
        Region? previousRegion = Region;
        Region = new Region(path);
        previousRegion?.Dispose();
    }

    private int ScaleLogicalPixels(int value)
        => Math.Max(1, (int)Math.Round(value * DeviceDpi / 96d));
}

internal sealed class RoundedSurfacePanel : Panel
{
    internal Color BorderColor = SystemColors.ControlDark;

    internal int CornerRadius = 8;

    public RoundedSurfacePanel()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint,
            true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle bounds = ClientRectangle;
        bounds.Width = Math.Max(1, bounds.Width - 1);
        bounds.Height = Math.Max(1, bounds.Height - 1);
        using GraphicsPath path = RoundedGeometry.CreatePath(
            bounds,
            ScaleLogicalPixels(CornerRadius));
        using var backgroundBrush = new SolidBrush(BackColor);
        using var borderPen = new Pen(BorderColor);
        e.Graphics.FillPath(backgroundBrush, path);
        e.Graphics.DrawPath(borderPen, path);
    }

    private int ScaleLogicalPixels(int value)
        => Math.Max(1, (int)Math.Round(value * DeviceDpi / 96d));
}

internal sealed class RoundedButton : Button
{
    private bool _hovered;
    private bool _pressed;

    internal Color BorderColor = SystemColors.ControlDark;

    internal Color CanvasBackColor = SystemColors.Control;

    internal int CornerRadius = 6;

    internal Color HoverBackColor = SystemColors.ControlLight;

    internal Color PressedBackColor = SystemColors.ControlLightLight;

    public RoundedButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint,
            true);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = false;
        _pressed = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _pressed = true;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _pressed = false;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(CanvasBackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle bounds = ClientRectangle;
        bounds.Width = Math.Max(1, bounds.Width - 1);
        bounds.Height = Math.Max(1, bounds.Height - 1);
        Color background = _pressed
            ? PressedBackColor
            : _hovered
                ? HoverBackColor
                : BackColor;
        if (!Enabled)
        {
            background = ControlPaint.Light(background);
        }

        using GraphicsPath path = RoundedGeometry.CreatePath(
            bounds,
            ScaleLogicalPixels(CornerRadius));
        using var backgroundBrush = new SolidBrush(background);
        using var borderPen = new Pen(BorderColor);
        e.Graphics.FillPath(backgroundBrush, path);
        e.Graphics.DrawPath(borderPen, path);
        TextRenderer.DrawText(
            e.Graphics,
            Text,
            Font,
            bounds,
            Enabled ? ForeColor : SystemColors.GrayText,
            TextFormatFlags.HorizontalCenter
                | TextFormatFlags.VerticalCenter
                | TextFormatFlags.SingleLine
                | TextFormatFlags.EndEllipsis
                | TextFormatFlags.NoPrefix);
    }

    private int ScaleLogicalPixels(int value)
        => Math.Max(1, (int)Math.Round(value * DeviceDpi / 96d));
}

internal static class RoundedGeometry
{
    public static GraphicsPath CreatePath(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        if (bounds.Width <= 1 || bounds.Height <= 1)
        {
            path.AddRectangle(bounds);
            return path;
        }

        int diameter = Math.Min(
            Math.Max(2, radius * 2),
            Math.Min(bounds.Width, bounds.Height));
        var arc = new Rectangle(bounds.Location, new Size(diameter, diameter));
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }
}
