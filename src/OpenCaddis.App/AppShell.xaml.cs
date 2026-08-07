namespace OpenCaddis.App
{
    public partial class AppShell : Shell
    {
        public AppShell(
            MainPage mainPage,
            ServerPage serverPage,
            ConnectionsPage connectionsPage,
            BuilderPage builderPage,
            SurfacePage surfacePage)
        {
            InitializeComponent();
            HomeContent.Content = mainPage;
            ServerContent.Content = serverPage;
            ConnectionsContent.Content = connectionsPage;
            BuilderContent.Content = builderPage;
            SurfaceContent.Content = surfacePage;
        }
    }
}
