using System.Drawing.Drawing2D;

namespace GpuSwitcher.App;

static class Theme
{
    public static bool Dark => Application.IsDarkModeEnabled;

    public static Color Game => Color.FromArgb(46, 160, 67);
    public static Color AppC => Color.FromArgb(56, 132, 255);
    public static Color Media => Color.FromArgb(163, 113, 247);
    public static Color Warn => Dark ? Color.FromArgb(240, 170, 60) : Color.FromArgb(190, 110, 0);
    public static Color Bad => Dark ? Color.FromArgb(255, 110, 100) : Color.FromArgb(200, 40, 40);
    public static Color Good => Dark ? Color.FromArgb(90, 200, 120) : Color.FromArgb(30, 130, 60);
    public static Color Muted => Dark ? Color.FromArgb(140, 140, 140) : Color.FromArgb(120, 120, 120);
    public static Color Card => Dark ? Color.FromArgb(38, 38, 42) : Color.White;
    public static Color CardBorder => Dark ? Color.FromArgb(64, 64, 70) : Color.FromArgb(218, 220, 224);
    public static Color Surface => Dark ? Color.FromArgb(28, 28, 30) : Color.FromArgb(243, 244, 246);
    public static Color Text => Dark ? Color.FromArgb(235, 235, 235) : Color.FromArgb(30, 30, 30);
    public static Color Track => Dark ? Color.FromArgb(60, 60, 66) : Color.FromArgb(230, 231, 235);

    public static Color RoleColor(Role r) => r switch { Role.Game => Game, Role.App => AppC, Role.Media => Media, _ => Muted };
    public static string RoleLabel(Role r) => r switch { Role.Game => "Games", Role.App => "Apps", Role.Media => "Media", _ => "Windows" };

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>The tray/window icon: a GPU board with three colored lanes (games, apps, media).</summary>
    public static Icon MakeIcon(int size = 32)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = size / 32f;
            using var board = Rounded(new RectangleF(2 * s, 6 * s, 28 * s, 20 * s), 4 * s);
            using (var b = new SolidBrush(Color.FromArgb(32, 36, 44))) g.FillPath(b, board);
            Color[] lanes = { Game, AppC, Media };
            for (int i = 0; i < 3; i++)
            {
                using var lane = Rounded(new RectangleF(6 * s, (9.5f + i * 5.5f) * s, 20 * s, 3.6f * s), 1.8f * s);
                using var lb = new SolidBrush(lanes[i]);
                g.FillPath(lb, lane);
            }
            using var pin = new SolidBrush(Color.FromArgb(200, 170, 80));
            for (int i = 0; i < 5; i++) g.FillRectangle(pin, (7 + i * 4) * s, 26 * s, 2 * s, 3 * s);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public static string Mb(double bytes) => bytes >= 1 << 30 ? $"{bytes / (1 << 30):0.0} GB" : $"{bytes / (1 << 20):0} MB";
}

/// <summary>ListView without flicker.</summary>
sealed class SmoothListView : ListView
{
    public SmoothListView()
    {
        DoubleBuffered = true;
        View = View.Details;
        FullRowSelect = true;
        HideSelection = false;
        BorderStyle = BorderStyle.None;
    }
}
