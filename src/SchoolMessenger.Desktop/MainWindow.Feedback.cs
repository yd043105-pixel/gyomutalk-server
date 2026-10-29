using System.Windows;

namespace SchoolMessenger.Desktop;

public partial class MainWindow
{
    void OpenFeedback(object sender,RoutedEventArgs e){if(Api is not null)new FeedbackWindow(this).Show();}
    void OpenPrivacyRequest(object sender,RoutedEventArgs e){if(Api is not null)new FeedbackWindow(this,Api,false,true).Show();}
}
