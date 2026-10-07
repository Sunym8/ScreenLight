using System.Drawing.Drawing2D;

namespace ScreenLight;

internal static class UiAssets
{
    internal static Icon LoadIcon()
    {
        using var stream = typeof(UiAssets).Assembly.GetManifestResourceStream("ScreenLight.AppIcon") ?? throw new InvalidOperationException("找不到程序图标。");
        using var icon = new Icon(stream, new Size(32, 32));
        return (Icon)icon.Clone();
    }
    internal static Bitmap Gear(int size)
    {
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var center = size / 2f;
        var points = Enumerable.Range(0, 48).Select(i =>
        {
            var radius = i % 6 is 1 or 2 or 3 or 4 ? size * .45f : size * .34f;
            var angle = i * Math.PI / 24;
            return new PointF(center + radius * (float)Math.Cos(angle), center + radius * (float)Math.Sin(angle));
        }).ToArray();
        using var path = new GraphicsPath(FillMode.Alternate);
        path.AddPolygon(points);
        path.AddEllipse(size * .34f, size * .34f, size * .32f, size * .32f);
        g.FillPath(Brushes.White, path);
        return bitmap;
    }
}
