namespace OpenCaddis.App
{
    public partial class AppShell : Shell
    {
        public AppShell(
            MainPage mainPage,
            ServerPage serverPage,
            BuilderPage builderPage,
            SurfacePage surfacePage)
        {
            InitializeComponent();
            HomeContent.Content = mainPage;
            ServerContent.Content = serverPage;
            BuilderContent.Content = builderPage;
            SurfaceContent.Content = surfacePage;
        }
    }
}
