namespace OpenCaddis.App
{
    public partial class AppShell : Shell
    {
        public AppShell(MainPage mainPage, ServerPage serverPage, SurfacePage surfacePage)
        {
            InitializeComponent();
            HomeContent.Content = mainPage;
            ServerContent.Content = serverPage;
            SurfaceContent.Content = surfacePage;
        }
    }
}
