namespace SolidWorksAgent.Core.Drawing;

public static class DrawingLayout
{
    public sealed record Size(double WidthMm, double HeightMm);
    public sealed record Cell(double Xmm, double Ymm, double WidthMm, double HeightMm);
    public sealed record Plan(string Paper, double WidthMm, double HeightMm, double Scale, Cell[] Cells);
    private static readonly (string Name, double W, double H)[] Papers =
        [("A4",297,210),("A3",420,297),("A2",594,420),("A1",841,594),("A0",1189,841)];
    public static Plan Fit(IReadOnlyList<Size> views, string paper = "auto", double? scale = null)
    {
        if (views.Count == 0 || views.Any(v => !double.IsFinite(v.WidthMm + v.HeightMm) || v.WidthMm <= 0 || v.HeightMm <= 0))
            throw new ArgumentException("ビューの寸法が不正です。");
        if (scale is double requested && (!double.IsFinite(requested) || requested <= 0)) throw new ArgumentException("尺度は正の数で指定してください。");
        var choices = paper == "auto" ? Papers : Papers.Where(p => p.Name == paper.ToUpperInvariant()).ToArray();
        if (choices.Length == 0) throw new ArgumentException("用紙は auto/A4/A3/A2/A1/A0 です。");
        int cols = views.Count <= 4 ? 2 : 3, rows = (views.Count + cols - 1) / cols;
        foreach (var sheet in choices)
        {
            double cw = (sheet.W - 24) / cols, ch = (sheet.H - 55) / rows;
            var fit = views.Min(v => Math.Min((cw - 44) / v.WidthMm, (ch - 36) / v.HeightMm));
            if (scale is double explicitScale && explicitScale > fit) { if (sheet != choices[^1]) continue; throw new ArgumentException("指定した用紙と尺度ではビューが収まりません。用紙を大きくするか尺度を下げてください。"); }
            if (paper == "auto" && scale is null && fit < 1 && sheet != choices[^1]) continue;
            double selected = scale ?? new[] { 5d,2d,1d,.5,.2,.1,.05,.02,.01,.005,.002,.001 }.FirstOrDefault(s => s <= fit, fit * .95);
            var cells = Enumerable.Range(0, views.Count).Select(i =>
            {
                // Standard four-view arrangement: front lower left, top above, right to the right, isometric above right.
                int col = views.Count == 4 ? i / 2 : i % cols;
                int row = views.Count == 4 ? i % 2 : rows - 1 - i / cols;
                return new Cell(12 + (col + .5) * cw, 35 + (row + .5) * ch, cw, ch);
            }).ToArray();
            return new(sheet.Name, sheet.W, sheet.H, selected, cells);
        }
        throw new InvalidOperationException("図面の配置を計算できませんでした。");
    }
}
