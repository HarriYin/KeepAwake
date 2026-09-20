namespace KeepAwake
{
    internal static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            //ApplicationConfiguration.Initialize();
            //Application.Run(new Form1());



            bool createdNew;
            using (var mutex = new Mutex(true, "KeepAwake_SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("KeepAwake 已在运行中，请查看屏幕右下角的托盘图标。",
                        "KeepAwake", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayContext());
            }


        }
    }
}