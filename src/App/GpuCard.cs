using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace GpuSwitcher.App;

/// <summary>Live tile for one GPU: roles, engines (3D / decode / encode / copy) and VRAM.</summary>
sealed class GpuCard : Control
{
    public GpuAdapter? Adapter { get; private set; }
    AdapterUsage? _usage;
    List<Role> _roles = new();
    int _appsOn, _appsRouted;

    public GpuCard()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Size = new Size(300, 168);
        Margin = new Padding(0, 0, 12, 0);
    }

    public void Bind(GpuAdapter adapter, AdapterUsage? usage, IEnumerable<Role> roles, int appsOn, int appsRouted)
    {
        Adapter = adapter;
        _usage = usage;
        _roles = roles.ToList();
        _appsOn = appsOn;
        _appsRouted = appsRouted;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Parent?.BackColor ?? Theme.Surface);
        float k = DeviceDpi / 96f;

        var rect = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using (var path = Theme.Rounded(rect, 8 * k))
        {
            using var bg = new SolidBrush(Theme.Card);
            using var border = new Pen(Theme.CardBorder);
            g.FillPath(bg, path);
            g.DrawPath(border, path);
        }
        if (Adapter == null) return;

        float x = 12 * k, y = 10 * k, w = Width - 24 * k;
        using var title = new Font("Segoe UI Semibold", 10.5f);
        using var small = new Font("Segoe UI", 8f);
        using var tiny = new Font("Segoe UI", 7.5f);
        using var text = new SolidBrush(Theme.Text);
        using var muted = new SolidBrush(Theme.Muted);

        g.DrawString(Adapter.ShortName, title, text, x, y);
        y += 20 * k;
        var kind = Adapter.Kind == AdapterKind.Integrated ? "Integrated" : "Discrete";
        var mons = Adapter.Monitors.Count == 0 ? "no monitors" : $"{Adapter.Monitors.Count} monitor{(Adapter.Monitors.Count > 1 ? "s" : "")}";
        var vram = Adapter.Kind == AdapterKind.Integrated ? "shared memory" : Theme.Mb(Adapter.DedicatedVideoMemory);
        g.DrawString($"{kind} · {vram} · {mons}", small, muted, x, y);
        y += 18 * k;

        // role pills
        float px = x;
        if (_roles.Count == 0)
        {
            g.DrawString("no role assigned", tiny, muted, px, y + 1 * k);
        }
        foreach (var r in _roles)
        {
            var label = Theme.RoleLabel(r);
            var sz = g.MeasureString(label, tiny);
            var pill = new RectangleF(px, y, sz.Width + 10 * k, 16 * k);
            using var pp = Theme.Rounded(pill, 8 * k);
            using var pb = new SolidBrush(Theme.RoleColor(r));
            g.FillPath(pb, pp);
            g.DrawString(label, tiny, Brushes.White, px + 5 * k, y + 1.5f * k);
            px += pill.Width + 6 * k;
        }
        y += 24 * k;

        Bar(g, "3D", _usage?.Render ?? 0, x, ref y, w, k, small, Theme.RoleColor(_roles.FirstOrDefault(Role.Ignore)));
        Bar(g, "Decode", _usage?.Decode ?? 0, x, ref y, w, k, small, Theme.Media);
        Bar(g, "Encode", _usage?.Encode ?? 0, x, ref y, w, k, small, Theme.Media);

        long used = (_usage?.DedicatedUsed ?? 0) + (Adapter.Kind == AdapterKind.Integrated ? _usage?.SharedUsed ?? 0 : 0);
        var memText = Adapter.Kind == AdapterKind.Integrated ? $"{Theme.Mb(used)} used" : $"{Theme.Mb(used)} / {Theme.Mb(Adapter.DedicatedVideoMemory)}";
        g.DrawString($"Memory {memText}   ·   {_appsOn} app{(_appsOn == 1 ? "" : "s")} on it, {_appsRouted} routed here", tiny, muted, x, y + 2 * k);
    }

    static void Bar(Graphics g, string label, double pct, float x, ref float y, float w, float k, Font f, Color c)
    {
        using var muted = new SolidBrush(Theme.Muted);
        using var text = new SolidBrush(Theme.Text);
        g.DrawString(label, f, muted, x, y - 1 * k);
        float bx = x + 52 * k, bw = w - 52 * k - 42 * k, bh = 7 * k, by = y + 4 * k;
        using (var tp = Theme.Rounded(new RectangleF(bx, by, bw, bh), bh / 2))
        using (var tb = new SolidBrush(Theme.Track)) g.FillPath(tb, tp);
        float fill = (float)Math.Clamp(pct / 100.0, 0, 1) * bw;
        if (fill >= 1)
        {
            using var fp = Theme.Rounded(new RectangleF(bx, by, Math.Max(fill, bh), bh), bh / 2);
            using var fb = new SolidBrush(c);
            g.FillPath(fb, fp);
        }
        var s = $"{pct:0}%";
        var sz = g.MeasureString(s, f);
        g.DrawString(s, f, text, x + w - sz.Width, y - 1 * k);
        y += 16 * k;
    }
}
