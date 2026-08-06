namespace OpenCaddis.App
{
    public partial class AppShell : Shell
    {
        public AppShell(MainPage mainPage, ServerPage serverPage)
        {
            InitializeComponent();
            HomeContent.Content = mainPage;
            ServerContent.Content = serverPage;
        }
    }
}
