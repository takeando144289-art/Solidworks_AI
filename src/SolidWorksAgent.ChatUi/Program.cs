namespace SolidWorksAgent.ChatUi;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        using var window = new Form { Text = "SolidWorks AI Chat — プレビュー", Size = new(460, 900), MinimumSize = new(340, 640) };
        window.Controls.Add(new ChatPanel(0));
        if (args.Length == 2 && args[0] == "--smoke-test")
        {
            window.Shown += (_, _) =>
            {
                window.Refresh();
                var panel = window.Controls[0];
                using var bitmap = new Bitmap(panel.Width, panel.Height);
                panel.DrawToBitmap(bitmap, panel.ClientRectangle);
                bitmap.Save(Path.GetFullPath(args[1]), System.Drawing.Imaging.ImageFormat.Png);
                window.Close();
            };
        }
        Application.Run(window);
    }
}
