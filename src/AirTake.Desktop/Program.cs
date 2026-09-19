namespace AirTake.Desktop;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ShowError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Storage.Log(e.ExceptionObject.ToString() ?? "Unhandled error");
        try
        {
            var smoke = args.Contains("--smoke-test");
            using var window = new MainWindow(smoke);
            if (smoke)
            {
                window.Show();
                Application.DoEvents();
                using var bitmap = new Bitmap(window.Width, window.Height);
                window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                var output = args.Length > 1 ? args[1] : "airtake-smoke.png";
                bitmap.Save(output, System.Drawing.Imaging.ImageFormat.Png);
                window.Close();
                return 0;
            }
            Application.Run(window);
            return 0;
        }
        catch (Exception e) { if (!args.Contains("--smoke-test")) ShowError(e); else File.WriteAllText("smoke-error.txt", e.ToString()); return 1; }
    }
    private static void ShowError(Exception exception)
    {
        Storage.Log(exception.ToString());
        MessageBox.Show(exception.Message, "AirTake — ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
