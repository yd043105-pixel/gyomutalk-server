using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace SchoolMessenger.Desktop;

static class AttachmentTransferWindow
{
    public static Task<bool> Save(Window owner, Api api, string id, string destination)
    {
        using var cancellation=new CancellationTokenSource();
        var clock=Stopwatch.StartNew();
        var window=new Window{Owner=owner,Title="첨부파일 다운로드",Width=440,Height=225,ResizeMode=ResizeMode.NoResize,
            WindowStartupLocation=WindowStartupLocation.CenterOwner,Style=(Style)owner.FindResource("SchoolWindow")};
        var panel=new StackPanel{Margin=new Thickness(20)};
        panel.Children.Add(AdminUi.Heading(Path.GetFileName(destination),15));
        var bar=new ProgressBar{Height=12,Margin=new Thickness(0,12,0,8),Maximum=100,IsIndeterminate=true};panel.Children.Add(bar);
        var status=new TextBlock{Text="서버에서 파일을 확인하고 있습니다.",TextWrapping=TextWrapping.Wrap};panel.Children.Add(status);
        var pause=new Button{Content="일시 중지",HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,12,0,0)};panel.Children.Add(pause);
        window.Content=panel;
        var finished=false;var saved=false;ExceptionDispatchInfo? error=null;
        pause.Click+=(_,_)=>{pause.IsEnabled=false;status.Text="중지 중… 같은 이름과 위치를 선택하면 이어받을 수 있습니다.";cancellation.Cancel();};
        window.Closing+=(_,args)=>{if(!finished){args.Cancel=true;cancellation.Cancel();}};
        window.Loaded+=async(_,_)=>
        {
            try
            {
                await api.Download(id,destination,value=>
                {
                    bar.IsIndeterminate=value.Total<=0;
                    if(value.Total>0)bar.Value=value.Received*100.0/value.Total;
                    var remaining=value.BytesPerSecond>0&&value.Total>value.Received?TimeSpan.FromSeconds(Math.Min(86400,(value.Total-value.Received)/value.BytesPerSecond)).ToString(@"hh\:mm\:ss"):"—";
                    status.Text=$"{value.Received/1048576.0:F1} / {value.Total/1048576.0:F1} MiB · {value.BytesPerSecond/1048576:F1} MiB/s\n경과 {clock.Elapsed:hh\\:mm\\:ss} · 남은 시간 {remaining}";
                },cancellation.Token);
                saved=true;
            }
            catch(OperationCanceledException)when(cancellation.IsCancellationRequested){}
            catch(Exception failure){error=ExceptionDispatchInfo.Capture(failure);}
            finally{finished=true;window.Close();}
        };
        window.ShowDialog();error?.Throw();return Task.FromResult(saved);
    }
}
